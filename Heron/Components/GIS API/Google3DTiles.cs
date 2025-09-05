using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Heron.Components.Heron3DTiles;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Heron.Components.GIS_API
{

        public class Google3DTiles : GH_Component
        {
            public Google3DTiles()
              : base("Google 3D Tiles (Photorealistic)",
                     "G3DTiles",
                     "Download + cache Google Photorealistic 3D Tiles for a boundary and import as meshes aligned to EarthAnchorPoint.",
                     "Heron", "3D Tiles")
            { }

            protected override System.Drawing.Bitmap Icon => null; // TODO
            public override Guid ComponentGuid => new Guid("f7b0f7b1-9e70-4f5a-9aeb-7d50d5d3a5f7");

            protected override void RegisterInputParams(GH_InputParamManager p)
            {

                string defaultCacheFolder;

                // Get the active Rhino document's path (null/empty if not saved yet)
                var rhinoDocPath = Rhino.RhinoDoc.ActiveDoc?.Path;
                if (!string.IsNullOrEmpty(rhinoDocPath))
                {
                    // Use a "Heron3DTilesCache" subfolder next to the current .3dm file
                    var docDir = Path.GetDirectoryName(rhinoDocPath);
                    defaultCacheFolder = Path.Combine(docDir, "Heron3DTilesCache");
                }
                else
                {
                    // Fallback: per-user local cache
                    defaultCacheFolder = Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "Heron3DTilesCache");
                }

                p.AddCurveParameter("Boundary", "B", "Boundary (planar) in model coordinates (use Heron to place by lat/lon).", GH_ParamAccess.item);
                p.AddTextParameter("API Key", "Key", "Google Maps Platform API key.", GH_ParamAccess.item);
                p.AddIntegerParameter("Max LOD", "LOD", "Maximum traversal depth (0 = root only).", GH_ParamAccess.item, 4);
                p.AddTextParameter("Cache Folder", "Folder", "Folder to store tile cache (.glb files).", GH_ParamAccess.item, defaultCacheFolder);
                p.AddBooleanParameter("Download", "D", "If true, run. If false, do nothing.", GH_ParamAccess.item, true);
                p.AddBooleanParameter("Clear Cache", "Clear", "If true, clear cache folder first.", GH_ParamAccess.item, false);
                p.AddNumberParameter("Max Size (GB)", "MaxGB", "Soft cap for total downloads this run.", GH_ParamAccess.item, 1.0);
            }
            
            protected override void RegisterOutputParams(GH_OutputParamManager p)
            {
                p.AddMeshParameter("Meshes", "M", "Imported meshes, oriented to EarthAnchorPoint.", GH_ParamAccess.list);
                p.AddTextParameter("Info", "I", "Status messages / diagnostics.", GH_ParamAccess.list);
                p.AddTextParameter("Files", "F", "Tile .glb files used (cache paths).", GH_ParamAccess.list);
                p.AddGenericParameter("Materials", "Mat", "Materials for each tile.", GH_ParamAccess.list);
            }

            protected override void SolveInstance(IGH_DataAccess da)
            {
                Curve boundary = null;
                string apiKey = null;
                int maxLod = 4;
                string cacheFolder = null;
                bool download = true;
                bool clear = false;
                double maxGb = 1.0;

                // Read inputs
                if (!da.GetData(0, ref boundary) || boundary == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary is required.");
                    return;
                }
                if (!da.GetData(1, ref apiKey) || string.IsNullOrWhiteSpace(apiKey))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "API Key is required.");
                    return;
                }
                da.GetData(2, ref maxLod);
                da.GetData(3, ref cacheFolder);
                da.GetData(4, ref download);
                da.GetData(5, ref clear);
                da.GetData(6, ref maxGb);

                // Light validation only
                if (!boundary.TryGetPlane(out var boundaryPlane))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be planar.");
                    return;
                }
                if (maxLod < 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max LOD must be >= 0.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(cacheFolder))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Cache Folder is required.");
                    return;
                }
                if (maxGb < 0.0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max Size (GB) must be >= 0.");
                    return;
                }

                // Do nothing unless Download is true
                if (!download)
                {
                    // Clear outputs explicitly to avoid stale data
                    da.SetDataList(0, new List<Mesh>());
                    da.SetDataList(1, new List<string> { "Download is false. No action taken." });
                    da.SetDataList(2, new List<string>());
                    da.SetDataList(3, new List<object>());
                    return;
                }

                // All validations passed and Download is true -> proceed with logic
                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);
                if (clear)
                {
                    try { foreach (var f in Directory.EnumerateFiles(cacheFolder)) File.Delete(f); }
                    catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Failed to clear cache: {ex.Message}"); }
                }

                // Get EarthAnchor transforms
                var eap = RhinoDoc.ActiveDoc?.EarthAnchorPoint;
                if (eap == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No active document EarthAnchorPoint found. Set EAP (Heron) first."); return; }
                var earthToModel = Heron.Convert.WGSToXYZTransform();

                // Build AOI poly in model coords (as provided), and also precompute its bbox for cheap tests
                var aoi = boundary.ToPolyline(0, 0, 0.1, 0.1).ToPolyline();
                if (aoi == null || !aoi.IsClosed) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be a closed planar curve."); return; }
                var aoiBbox = aoi.BoundingBox;

                var info = new List<string>();
                var usedFiles = new List<string>();

                try
                {
                    var api = new GoogleTilesApi(apiKey, cacheFolder);
                    // Fetch root tileset and capture session
                    var root = api.GetRootTileset(); // throws on error
                    info.Add("Fetched root tileset.");

                    // Compute AOI in WGS84 by converting model boundary corners -> lat/lon
                    var modelToEarth = Heron.Convert.XYZToWGSTransform();

                    if (aoi == null || !aoi.IsClosed) { /* error */ }

                    // Compute AOI WGS84 using Heron
                    (double minLon, double minLat, double maxLon, double maxLat) aoiWgs = GeoUtils.AoiToWgs(aoi);

                    info.Add(string.Format("AOI WGS84: [{0:F6},{1:F6}]–[{2:F6},{3:F6}]", aoiWgs.minLon, aoiWgs.minLat, aoiWgs.maxLon, aoiWgs.maxLat));

                    // Traverse with spatial pruning + max LOD
                    var walker = new TilesetWalker(api, aoiWgs, maxLod);
                    var plan = walker.PlanDownloads(root);

                    // Enforce soft size cap
                    long capBytes = (long)(maxGb * 1024 * 1024 * 1024);
                    var downloader = new TileDownloader(api, capBytes);

                    var localGlbs = downloader.Ensure(plan, download, out long totalBytes, out var skippedForCap);
                    info.Add(string.Format("Tiles planned: {0}, downloaded/used: {1}, bytes: {2:n0}", plan.Count, localGlbs.Count, totalBytes));
                    if (skippedForCap > 0) info.Add(string.Format("Skipped {0} tiles due to size cap ({1} GB).", skippedForCap, maxGb));
                    usedFiles.AddRange(localGlbs);

                    // Import GLBs into headless doc, pull meshes, orient to EarthAnchor (Earth→Model)
                    var meshes = Importer.ImportMeshesOriented(localGlbs, earthToModel, out var ghMaterials, out var importNotes);
                    info.AddRange(importNotes);

                    // Promote import failures to GH warnings/errors
                    int failedCount = 0;
                    for (int i = 0; i < importNotes.Count; i++)
                    {
                        var note = importNotes[i];
                        if (!string.IsNullOrEmpty(note) && note.StartsWith("Import failed", StringComparison.OrdinalIgnoreCase))
                        {
                            failedCount++;
                            // Limit spam: only surface first few as warnings
                            if (failedCount <= 5)
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, note);
                        }
                    }
                    if ((meshes == null || meshes.Count == 0) && usedFiles.Count > 0)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No meshes imported from downloaded tiles. See Info for failed files.");
                    }

                    da.SetDataList(0, meshes);
                    da.SetDataList(1, info);
                    da.SetDataList(2, usedFiles);
                    da.SetDataList(3, ghMaterials);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                    da.SetDataList(1, info.Concat(new[] { ex.ToString() }));
                }
            }
        }

}
