using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using System.IO;


namespace Heron.Components.Heron3DTiles
{
    public static class Importer
    {
        /// <summary>
        /// Imports GLBs into a headless doc (meters), pulls meshes, applies Earth→Model transform, and returns Mesh list.
        /// Focus on robust import and material extraction similar to the provided TileImporter logic.
        /// </summary>
        public static List<Mesh> ImportMeshesOriented(
            List<string> glbFiles,
            Transform earthToModel,
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

            using (var temp = RhinoDoc.CreateHeadless(null))
            {
                temp.ModelUnitSystem = UnitSystem.Meters;

                foreach (var fp in glbFiles)
                {
                    if (string.IsNullOrWhiteSpace(fp) || !File.Exists(fp))
                    {
                        notes.Add($"Invalid path or file not found: {fp}");
                        continue;
                    }

                    // Prefer the simple Import(path) like the working reference.
                    bool imported = false;
                    try
                    {
                        imported = temp.Import(fp);
                    }
                    catch (Exception)
                    {
                        imported = false;
                    }

                    if (!imported)
                    {
                        // Fallback to Import with options dictionary
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
                        catch (Exception)
                        {
                            imported = false;
                        }
                    }

                    if (!imported)
                    {
                        notes.Add($"Import failed: {Path.GetFileName(fp)}");
                        continue;
                    }

                    // Snapshot imported mesh RhinoObjects
                    var roList = new List<RhinoObject>();
                    foreach (var ro in temp.Objects.GetObjectList(ObjectType.Mesh))
                    {
                        roList.Add(ro);
                    }

                    if (roList.Count > 0)
                    {
                        string baseName = Path.GetFileNameWithoutExtension(fp);

                        foreach (var ro in roList)
                        {
                            if (ro?.Geometry is Mesh m)
                            {
                                var dup = m.DuplicateMesh();
                                dup.Normals.ComputeNormals();
                                dup.Transform(earthToModel); // Earth → Model (Heron EAP)
                                outMeshes.Add(dup);

                                // Extract render material if available (align to reference logic)
                                try
                                {
                                    var rmat = ro.RenderMaterial;
                                    if (rmat != null)
                                    {
                                        try { if (string.IsNullOrEmpty(rmat.Name)) rmat.Name = baseName; } catch { }
                                        ghMaterials.Add(new GH_Material(rmat));
                                    }
                                }
                                catch { /* ignore */ }
                            }

                            // keep temp doc clean between files
                            if (ro != null)
                                temp.Objects.Delete(ro, true);
                        }
                    }
                    else
                    {
                        // No mesh objects created by importer
                        notes.Add($"No mesh objects imported from: {Path.GetFileName(fp)}");
                    }
                }
            }

            notes.Add($"Imported meshes: {outMeshes.Count}, materials: {ghMaterials.Count}");
            return outMeshes;
        }
    }
}