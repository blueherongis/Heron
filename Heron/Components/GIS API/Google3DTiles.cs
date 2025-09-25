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
    public class Google3DTiles : HeronComponent
    {
        // Add private fields for progress tracking
        private bool isDownloading = false;
        private int totalTiles = 0;
        private int downloadedTiles = 0;
        private long downloadedBytes = 0;

        public Google3DTiles()
          : base("Google 3D Tiles (Photorealistic)",
                 "G3DTiles",
                 "Download + cache Google Photorealistic 3D Tiles for a boundary and import as meshes aligned to EarthAnchorPoint.",
                 "3D Tiles")
        { }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.shp;
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
            p.AddBooleanParameter("Download", "D", "If true, run. If false, do nothing.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("Clear Cache", "Clear", "If true, clear cache folder first. Ignored when Download is false.", GH_ParamAccess.item, false);
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
            bool download = false;
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

            if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);
            
            // Only clear cache if download is true AND clear is true
            if (download && clear)
            {
                try 
                { 
                    Message = "Clearing cache...";
                    Grasshopper.Instances.RedrawCanvas();
                    foreach (var f in Directory.EnumerateFiles(cacheFolder)) File.Delete(f); 
                }
                catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Failed to clear cache: {ex.Message}"); }
            }
            else if (!download && clear)
            {
                // Inform user that clear cache is ignored when download is false
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clear Cache is ignored when Download is false.");
            }

            // Early validation of EAP only when needed
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
                // If download is false, skip root traversal and work only with cached files
                if (!download)
                {
                    Message = "Checking cached tiles...";
                    Grasshopper.Instances.RedrawCanvas();

                    // Get all .glb files from cache folder
                    var cachedFiles = Directory.EnumerateFiles(cacheFolder, "*.glb").ToList();
                    
                    if (cachedFiles.Count == 0)
                    {
                        Message = "No cached tiles found. Enable Download to fetch tiles.";
                        info.Add("No cached tiles found in: " + cacheFolder);
                        info.Add("Set Download to true to download tiles from Google 3D Tiles API.");
                        
                        da.SetDataList(0, new List<Mesh>());
                        da.SetDataList(1, info);
                        da.SetDataList(2, new List<string>());
                        da.SetDataList(3, new List<object>());
                        return;
                    }

                    // Calculate total size of cached files
                    long cachedTotalBytes = 0;
                    foreach (var file in cachedFiles)
                    {
                        try
                        {
                            cachedTotalBytes += new FileInfo(file).Length;
                        }
                        catch { } // Skip files that can't be accessed
                    }

                    double cachedTotalMB = cachedTotalBytes / (1024.0 * 1024.0);
                    Message = $"Found {cachedFiles.Count} cached tiles ({cachedTotalMB:F1} MB)";
                    Grasshopper.Instances.RedrawCanvas();
                    
                    info.Add($"Found {cachedFiles.Count} cached tiles in: {cacheFolder}");
                    info.Add($"Total cached size: {cachedTotalMB:F1} MB");
                    usedFiles.AddRange(cachedFiles);

                    // Import meshes from cached files
                    Message = "Importing meshes from cached tiles...";
                    Grasshopper.Instances.RedrawCanvas();

                    var cachedMeshes = new List<Mesh>();
                    List<Grasshopper.Kernel.Types.GH_Material> cachedMats = new List<Grasshopper.Kernel.Types.GH_Material>();
                    
                    var importMeshes = TileImporter.ImportMeshesOriented(cachedFiles, out cachedMats, out var importNotes);
                    cachedMeshes = importMeshes ?? new List<Mesh>();
                    info.AddRange(importNotes);

                    // Completion message
                    Message = $"Complete: {cachedMeshes.Count} meshes from {cachedFiles.Count} cached tiles ({cachedTotalMB:F1} MB)";
                    System.Threading.Thread.Sleep(100);
                    Grasshopper.Instances.RedrawCanvas();

                    da.SetDataList(0, cachedMeshes);
                    da.SetDataList(1, info);
                    da.SetDataList(2, usedFiles);
                    da.SetDataList(3, cachedMats);
                    return;
                }

                // Download mode - full processing with root traversal
                Message = "Connecting to Google 3D Tiles API...";
                Grasshopper.Instances.RedrawCanvas();

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

                Message = "Planning tile downloads...";
                Grasshopper.Instances.RedrawCanvas();

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

                long capBytes = (long)(maxGb * 1024 * 1024 * 1024);
                List<string> localGlbs = new List<string>();
                long totalBytes = 0;
                int skippedForCap = 0;
                
                if (plan.Count > 0)
                {
                    // Initialize progress tracking
                    totalTiles = plan.Count;
                    downloadedTiles = 0;
                    downloadedBytes = 0;
                    isDownloading = true;

                    Message = $"Downloading tiles (0/{totalTiles})...";
                    Grasshopper.Instances.RedrawCanvas();

                    var downloader = new TileDownloader(api, capBytes);
                    localGlbs = downloader.Ensure(plan, download, out totalBytes, out skippedForCap);
                    
                    // Update progress after download completion
                    downloadedTiles = localGlbs.Count;
                    downloadedBytes = totalBytes;
                    
                    // Convert bytes to MB for display
                    double totalMB = totalBytes / (1024.0 * 1024.0);
                    Message = $"Downloaded {downloadedTiles}/{totalTiles} tiles ({totalMB:F1} MB)";
                    Grasshopper.Instances.RedrawCanvas();
                    
                    info.Add(string.Format("Tiles planned: {0}, downloaded/used: {1}, bytes: {2:n0}", plan.Count, localGlbs.Count, totalBytes));
                    if (skippedForCap > 0) info.Add(string.Format("Skipped {0} tiles due to size cap ({1} GB).", skippedForCap, maxGb));
                }
                else
                {
                    info.Add("No tiles planned (even after any relaxed attempt).");
                }

                usedFiles.AddRange(localGlbs);

                if (localGlbs.Count > 0)
                {
                    Message = "Importing meshes...";
                    Grasshopper.Instances.RedrawCanvas();
                }

                var meshes = new List<Mesh>();
                List<Grasshopper.Kernel.Types.GH_Material> mats = new List<Grasshopper.Kernel.Types.GH_Material>();
                if (localGlbs.Count > 0)
                {
                    var importMeshes = TileImporter.ImportMeshesOriented(localGlbs, out mats, out var importNotes);
                    meshes = importMeshes ?? new List<Mesh>();
                    info.AddRange(importNotes);
                }

                // Completion message
                isDownloading = false;
                if (meshes.Count > 0)
                {
                    double totalMB = totalBytes / (1024.0 * 1024.0);
                    Message = $"Complete: {meshes.Count} meshes from {downloadedTiles} downloaded tiles ({totalMB:F1} MB)";
                }
                else
                {
                    Message = "Complete: No meshes imported";
                }

                // Final canvas redraw
                System.Threading.Thread.Sleep(100);
                Grasshopper.Instances.RedrawCanvas();

                da.SetDataList(0, meshes);
                da.SetDataList(1, info);
                da.SetDataList(2, usedFiles);
                da.SetDataList(3, mats);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("EarthAnchor") || ex.Message.Contains("ActiveDoc"))
            {
                isDownloading = false;
                Message = "Error: " + ex.Message;
                Grasshopper.Instances.RedrawCanvas();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                da.SetDataList(1, info.Concat(new[] { "Error: " + ex.Message }));
            }
            catch (Exception ex)
            {
                isDownloading = false;
                Message = "Error: " + ex.Message;
                Grasshopper.Instances.RedrawCanvas();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                da.SetDataList(1, info.Concat(new[] { ex.ToString() }));
            }
        }

        // Helper method to format file size similar to RESTOSM
        private string FormatFileSize(long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return mb.ToString("F1") + " MB";
        }
    }
}
