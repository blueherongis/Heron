using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Heron.Components.Heron3DTiles;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System.IO;
// Alias System.Environment to avoid ambiguity with Rhino.DocObjects.Environment
using SysEnv = System.Environment;

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
            var rhinoDocPath = Rhino.RhinoDoc.ActiveDoc?.Path;
            if (!string.IsNullOrEmpty(rhinoDocPath))
            {
                var docDir = Path.GetDirectoryName(rhinoDocPath);
                defaultCacheFolder = Path.Combine(docDir, "Heron3DTilesCache");
            }
            else
            {
                defaultCacheFolder = Path.Combine(
                    SysEnv.GetFolderPath(SysEnv.SpecialFolder.LocalApplicationData),
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

            if (!download)
            {
                da.SetDataList(0, new List<Mesh>());
                da.SetDataList(1, new List<string> { "Download is false. No action taken." });
                da.SetDataList(2, new List<string>());
                da.SetDataList(3, new List<object>());
                return;
            }

            if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);
            if (clear)
            {
                try { foreach (var f in Directory.EnumerateFiles(cacheFolder)) File.Delete(f); }
                catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Failed to clear cache: {ex.Message}"); }
            }

            var activeDoc = RhinoDoc.ActiveDoc;
            if (activeDoc == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No active Rhino document found. Ensure Rhino is running and a document is open.");
                return;
            }
            var eap = activeDoc.EarthAnchorPoint;
            if (eap == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "EarthAnchorPoint is null. This may indicate the document is disposed or corrupted.");
                return;
            }
            if (!eap.EarthLocationIsSet())
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "EarthAnchorPoint location has not been set. Use Heron's SetEAP component to set the Earth Anchor Point before using 3D Tiles components.");
                return;
            }

            var aoi = boundary.ToPolyline(0, 0, 0.1, 0.1).ToPolyline();
            if (aoi == null || !aoi.IsClosed)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be a closed planar curve.");
                return;
            }

            var info = new List<string>();
            var usedFiles = new List<string>();

            try
            {
                var api = new GoogleTilesApi(apiKey, cacheFolder);
                var root = api.GetRootTileset();
                info.Add("Fetched root tileset.");

                try
                {
                    (double minLon, double minLat, double maxLon, double maxLat) aoiWgs = GeoUtils.AoiToWgs(aoi);
                    info.Add(string.Format("AOI WGS84: [{0:F6},{1:F6}]–[{2:F6},{3:F6}]", aoiWgs.minLon, aoiWgs.minLat, aoiWgs.maxLon, aoiWgs.maxLat));
                    var aoiBounds = aoi.BoundingBox;
                    info.Add(string.Format("AOI Model: [{0:F2},{1:F2}]–[{2:F2},{3:F2}]", aoiBounds.Min.X, aoiBounds.Min.Y, aoiBounds.Max.X, aoiBounds.Max.Y));
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Failed to compute AOI WGS84 bounds: {ex.Message}");
                }

                TilesetWalker walker;
                try
                {
                    walker = new TilesetWalker(api, aoi, maxLod);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Failed to initialize TilesetWalker: {ex.Message}");
                    return;
                }

                var plan = walker.PlanDownloads(root);
                // Append traversal stats
                foreach (var line in walker.Stats.ToInfoLines()) info.Add(line);

                // If empty plan only because of AOI pruning, attempt a relaxed AOI second pass
                if (plan.Count == 0 && walker.Stats.EmptyPlan && walker.Stats.EmptyPlanReason == "All nodes pruned by AOI")
                {
                    info.Add("Second pass: relaxing AOI by 500 meters to avoid over-pruning.");
                    var relaxedWalker = new TilesetWalker(api, aoi, maxLod, 500.0);
                    var relaxedPlan = relaxedWalker.PlanDownloads(root);
                    foreach (var line in relaxedWalker.Stats.ToInfoLines()) info.Add("Relaxed " + line);
                    if (relaxedPlan.Count > 0)
                    {
                        plan = relaxedPlan;
                        walker = relaxedWalker; // adopt for further stats if needed
                    }
                    else
                    {
                        info.Add("Relaxed pass still produced no GLBs.");
                    }
                }

                long capBytes = (long)(maxGb * 1024 * 1024 * 1024);
                var downloader = new TileDownloader(api, capBytes);
                var localGlbs = plan.Count == 0 ? new List<string>() : downloader.Ensure(plan, download, out long totalBytes, out var skippedForCap);
                if (plan.Count > 0)
                {
                    long totalBytes = 0; int skippedCap = 0; // placeholders overwritten inside Ensure
                }
                if (plan.Count > 0)
                {
                    // We lost totalBytes above due to scope; recompute quick sum from files (cache sizes) for info.
                    long bytesSum = 0;
                    foreach (var f in localGlbs) { try { bytesSum += new FileInfo(f).Length; } catch { } }
                    info.Add(string.Format("Tiles planned: {0}, files obtained: {1}, bytes (approx): {2:n0}", plan.Count, localGlbs.Count, bytesSum));
                }
                else
                {
                    info.Add("No tiles planned (even after any relaxed attempt).");
                }

                usedFiles.AddRange(localGlbs);

                var meshes = localGlbs.Count == 0 ? new List<Mesh>() : Importer.ImportMeshesOriented(localGlbs, out var ghMaterials, out var importNotes);
                List<Grasshopper.Kernel.Types.GH_Material> mats = new List<Grasshopper.Kernel.Types.GH_Material>();
                List<string> importMessages = new List<string>();
                if (localGlbs.Count > 0)
                {
                    // Importer already produced notes & materials inside call; adapt variable names
                    meshes = Importer.ImportMeshesOriented(localGlbs, out mats, out importMessages);
                    info.AddRange(importMessages);
                }

                da.SetDataList(0, meshes);
                da.SetDataList(1, info);
                da.SetDataList(2, usedFiles);
                da.SetDataList(3, mats);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("EarthAnchor") || ex.Message.Contains("ActiveDoc"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                da.SetDataList(1, info.Concat(new[] { "Error: " + ex.Message }));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                da.SetDataList(1, info.Concat(new[] { ex.ToString() }));
            }
        }
    }
}
