using Grasshopper.Kernel.Types;
using Newtonsoft.Json.Linq;
using OSGeo.OSR;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Render.ChildSlotNames;
using Rhino.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace Heron.Utilities.Google3DTiles
{
    public static class TileImporter
    {
        /// <summary>
        /// Imports GLBs into a headless doc (meters), converts ECEF vertex positions to model coordinates (using EarthAnchorPoint)
        /// by performing per-vertex ECEF -> WGS84 (lon,lat,h) -> Model conversion, extracts meshes and materials.
        /// Also extracts copyright information from GLB asset metadata.
        /// </summary>
        public static List<Mesh> ImportMeshesOriented(
            List<string> glbFiles,
            out List<GH_Material> ghMaterials,
            out List<string> notes,
            out HashSet<string> copyrights)
        {
            notes = new List<string>();
            ghMaterials = new List<GH_Material>();
            copyrights = new HashSet<string>();
            var outMeshes = new List<Mesh>();

            ///GDAL setup
            Heron.GdalConfiguration.ConfigureOgr();

            if (glbFiles == null || glbFiles.Count == 0)
            {
                notes.Add("No files to import.");
                return outMeshes;
            }

            // Rhino 8+ recommended (glTF import + code-driven IO)
            if (RhinoApp.ExeVersion < 8)
                throw new Exception("glTF/GLB code-driven import requires Rhino 8 or newer.");

            var activeDoc = RhinoDoc.ActiveDoc;
            if (activeDoc == null)
                throw new Exception("Active Rhino document required for EarthAnchorPoint conversion.");

            // Precompute transforms and scales once.
            var wgsToModel = Heron.Convert.WGSToXYZTransform(); // Inverse of Model->WGS transform.
            double unitScaleModelToMeters = Rhino.RhinoMath.UnitScale(activeDoc.ModelUnitSystem, UnitSystem.Meters);
            double metersToModel = unitScaleModelToMeters == 0 ? 1.0 : 1.0 / unitScaleModelToMeters;

            RhinoDoc temp = null;
            try
            {
                temp = RhinoDoc.CreateHeadless(null);
                temp.ModelUnitSystem = UnitSystem.Meters; // Imported GLBs assumed in meters (ECEF meters)

                foreach (var fp in glbFiles)
                {
                    if (string.IsNullOrWhiteSpace(fp) || !File.Exists(fp))
                    {
                        notes.Add($"Invalid path or file not found: {fp}");
                        continue;
                    }

                    // Extract copyright from GLB file before importing
                    try
                    {
                        var copyright = ExtractCopyrightFromGlb(fp);
                        if (!string.IsNullOrWhiteSpace(copyright))
                        {
                            copyrights.Add(copyright);
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"Failed to extract copyright from {Path.GetFileName(fp)}: {ex.Message}");
                    }

                    bool imported = false;
                    try
                    {
                        imported = temp.Import(fp);
                    }
                    catch { imported = false; }

                    if (!imported)
                    {
                        try
                        {
                            var readOpts = new FileReadOptions
                            {
                                BatchMode = true,
                                ImportMode = true,
                                UseScaleGeometry = false
                            };
                            imported = temp.Import(fp, readOpts.OptionsDictionary);
                        }
                        catch { imported = false; }
                    }

                    if (!imported)
                    {
                        notes.Add($"Import failed: {Path.GetFileName(fp)}");
                        continue;
                    }

                    var roList = new List<RhinoObject>();
                    foreach (var ro in temp.Objects.GetObjectList(ObjectType.Mesh))
                        roList.Add(ro);

                    if (roList.Count == 0)
                    {
                        notes.Add($"No mesh objects imported from: {Path.GetFileName(fp)}");
                        continue;
                    }

                    string baseName = Path.GetFileNameWithoutExtension(fp);
                    int incrementMaterialName = 0; // Avoid duplicate material and bitmap names to avoid known issue in Rhino

                    foreach (var ro in roList)
                    {
                        if (!(ro?.Geometry is Mesh m))
                        {
                            if (ro != null) temp.Objects.Delete(ro, true);
                            continue;
                        }

                        var dup = m.DuplicateMesh();
                        if (dup == null)
                        {
                            if (ro != null) temp.Objects.Delete(ro, true);
                            continue;
                        }

                        // Per-vertex ECEF -> WGS84 -> Model conversion.
                        // Assumptions: Source vertices are in ECEF meters (standard for Google Photorealistic 3D Tiles after glTF import).
                        var verts = dup.Vertices;
                        for (int i = 0; i < verts.Count; i++)
                        {
                            var ecef = verts.Point3dAt(i);
                            var w = GeoUtils.EcefToGeoidGdal(ecef); // (lonDeg, latDeg, hMeters)
                            // Height: convert meters to model units BEFORE using WGSToXYZ transform (which expects elevation in model units).
                            var geo = new Point3d(w.lonDeg, w.latDeg, w.h * metersToModel);
                            geo.Transform(wgsToModel); // Now in model coordinates
                            verts.SetVertex(i, geo);
                        }

                        dup.Unweld(Rhino.RhinoMath.ToRadians(10), false); // Unweld for cleaner looking meshes
                        dup.Normals.ComputeNormals();
                        dup.Compact();
                        outMeshes.Add(dup);

                        // Extract render material if available
                        try
                        {
                            var rmat = ro.RenderMaterial;
                            if (rmat != null)
                            {
                                try
                                {
                                    // Rename material to indicate Google 3D Tile source and group them together in the material list
                                    rmat.Name = "G3DTile-" + baseName + "_" + incrementMaterialName;
                                    incrementMaterialName++;

                                    // Ensure metallic is zero for typical photorealistic textures
                                    rmat.SetParameter(PhysicallyBased.Metallic, 0.0);

                                    // Copy the unpacked diffuse bitmap into the same directory as the GLB file,
                                    // renaming it to match the GLB base name while preserving the image extension.
                                    var diffuseTexture = rmat.GetTextureFromUsage(Rhino.Render.RenderMaterial.StandardChildSlots.Diffuse);
                                    var diffuseBitmapUnpacked = diffuseTexture.Filename;
                                    if (!string.IsNullOrWhiteSpace(diffuseBitmapUnpacked) && File.Exists(diffuseBitmapUnpacked))
                                    {
                                        try
                                        {
                                            var destDir = Path.GetDirectoryName(fp) ?? System.Environment.CurrentDirectory;
                                            var newName = Path.GetFileNameWithoutExtension(fp) + "_" + incrementMaterialName + Path.GetExtension(diffuseBitmapUnpacked);
                                            var destPath = Path.Combine(destDir, newName);
                                            File.Copy(diffuseBitmapUnpacked, destPath, true);
                                            // Update the texture filename to the new copied path if desired
                                            try { diffuseTexture.Filename = destPath; } catch { }
                                            //notes.Add($"Copied texture to {destPath}");
                                        }
                                        catch (Exception ex)
                                        {
                                            notes.Add($"Failed to copy texture {diffuseBitmapUnpacked} to {fp}: {ex.Message}");
                                        }
                                    }
                                }
                                catch { }
                                ghMaterials.Add(new GH_Material(rmat));
                            }
                        }
                        catch { }

                        if (ro != null)
                            temp.Objects.Delete(ro, true);
                    }

                    // Clear objects after processing each file
                    temp.Objects.Clear();
                }
            }
            finally
            {
                // Explicit cleanup and disposal
                if (temp != null)
                {
                    try
                    {
                        temp.Objects.Clear();
                        temp.Dispose();
                    }
                    catch { }
                    temp = null;
                }

                // Force garbage collection to free memory
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            notes.Add($"Imported meshes: {outMeshes.Count}, materials: {ghMaterials.Count}");
            if (copyrights.Count > 0)
            {
                notes.Add($"Collected {copyrights.Count} unique copyright(s) from GLB files");
            }
            return outMeshes;
        }

        /// <summary>
        /// Extracts copyright information from a GLB file's asset metadata.
        /// GLB format: 12-byte header + JSON chunk + BIN chunk
        /// </summary>
        private static string ExtractCopyrightFromGlb(string glbPath)
        {
            using (var fs = new FileStream(glbPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                // Read GLB header (12 bytes)
                var magic = br.ReadUInt32(); // 0x46546C67 = "glTF"
                if (magic != 0x46546C67)
                    return null;

                var version = br.ReadUInt32(); // Should be 2
                var length = br.ReadUInt32();  // Total file length

                // Read first chunk (JSON)
                var chunkLength = br.ReadUInt32();
                var chunkType = br.ReadUInt32(); // 0x4E4F534A = "JSON"

                if (chunkType != 0x4E4F534A)
                    return null;

                // Read JSON data
                var jsonBytes = br.ReadBytes((int)chunkLength);
                var jsonString = Encoding.UTF8.GetString(jsonBytes);

                // Parse JSON to extract copyright
                try
                {
                    var json = JObject.Parse(jsonString);
                    var copyright = json["asset"]?["copyright"]?.ToString();
                    return copyright;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}