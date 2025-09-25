using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using System.IO;

namespace Heron.Utilities.Google3DTiles
{
    public static class TileImporter
    {
        /// <summary>
        /// Imports GLBs into a headless doc (meters), converts ECEF vertex positions to model coordinates (using EarthAnchorPoint)
        /// by performing per-vertex ECEF -> WGS84 (lon,lat,h) -> Model conversion, extracts meshes and materials.
        /// </summary>
        public static List<Mesh> ImportMeshesOriented(
            List<string> glbFiles,
            out List<GH_Material> ghMaterials,
            out List<string> notes)
        {
            notes = new List<string>();
            ghMaterials = new List<GH_Material>();
            var outMeshes = new List<Mesh>();

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
                            var w = GeoUtils.EcefToWgs84(ecef); // (lonDeg, latDeg, hMeters)
                            // Height: convert meters to model units BEFORE using WGSToXYZ transform (which expects elevation in model units).
                            var geo = new Point3d(w.lonDeg, w.latDeg, w.h * metersToModel);
                            geo.Transform(wgsToModel); // Now in model coordinates
                            verts.SetVertex(i, geo);
                        }

                        dup.Normals.ComputeNormals();
                        dup.Compact();
                        outMeshes.Add(dup);

                        // Extract render material if available
                        try
                        {
                            var rmat = ro.RenderMaterial;
                            if (rmat != null)
                            {
                                try { if (string.IsNullOrEmpty(rmat.Name)) rmat.Name = baseName; } catch { }
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
            return outMeshes;
        }
    }
}