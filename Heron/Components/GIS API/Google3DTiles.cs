using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Grasshopper.Kernel;
using Heron.Utilities.Google3DTiles;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using SysEnv = System.Environment;
using GH_IO.Serialization;
using Rhino.Input; // For RhinoGet
using Rhino.Commands; // For Result
using Newtonsoft.Json; // Manifest serialization

namespace Heron
{
    public class Google3DTiles : HeronComponent
    {
        // Progress tracking
        private bool isDownloading = false;
        private int totalTiles = 0;
        private int downloadedTiles = 0;
        private long downloadedBytes = 0;

        // Component options (previously dynamic inputs)
        private bool clearCacheOption = false;
        private double maxSizeGbOption = 1.0; // soft cap in GB

        private const string ManifestPrefix = "manifest_"; // manifest_{hash}.json

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
            // Order requested: Boundary, LOD, Folder, API Key, Download
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
            p.AddIntegerParameter("Max LOD", "LOD", "Maximum traversal depth (0 = root only).", GH_ParamAccess.item, 4);
            p.AddTextParameter("Cache Folder", "Folder", "Folder to store tile cache (.glb files).", GH_ParamAccess.item, defaultCacheFolder);
            p.AddTextParameter("API Key", "Key", "Google Maps Platform API key.", GH_ParamAccess.item);
            p.AddBooleanParameter("Download", "D", "If true, run. If false, do nothing.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            // Order requested: Info, Files, Meshes, Materials
            p.AddTextParameter("Info", "I", "Status messages / diagnostics.", GH_ParamAccess.list);
            p.AddTextParameter("Files", "F", "Tile .glb files used (cache paths).", GH_ParamAccess.list);
            p.AddMeshParameter("Meshes", "M", "Imported meshes, oriented to EarthAnchorPoint.", GH_ParamAccess.list);
            p.AddGenericParameter("Materials", "Mat", "Materials for each tile.", GH_ParamAccess.list);
        }

        // Context menu for options
        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);

            Menu_AppendItem(menu,
                $"Clear Cache Before Download: {(clearCacheOption ? "On" : "Off")}",
                (s, e) => { clearCacheOption = !clearCacheOption; ExpireSolution(true); },
                true,
                clearCacheOption);

            var maxSizeRoot = Menu_AppendItem(menu, $"Max Size (GB): {maxSizeGbOption:0.##}");
            double[] presets = { 0.25, 0.5, 1, 2, 4, 8 };
            foreach (var preset in presets)
            {
                Menu_AppendItem(maxSizeRoot.DropDown,
                    preset.ToString("0.##"),
                    (s, e) => { maxSizeGbOption = preset; ExpireSolution(true); },
                    true,
                    Math.Abs(maxSizeGbOption - preset) < 1e-6);
            }
            Menu_AppendItem(maxSizeRoot.DropDown, "Custom...", (s, e) =>
            {
                double current = maxSizeGbOption;
                var rc = RhinoGet.GetNumber("Enter max size in GB", false, ref current, 0.01, 2048.0);
                if (rc == Result.Success)
                {
                    maxSizeGbOption = current;
                    ExpireSolution(true);
                }
            });
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean("ClearCacheOption", clearCacheOption);
            writer.SetDouble("MaxSizeGbOption", maxSizeGbOption);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            if (reader.ItemExists("ClearCacheOption")) clearCacheOption = reader.GetBoolean("ClearCacheOption");
            if (reader.ItemExists("MaxSizeGbOption")) maxSizeGbOption = reader.GetDouble("MaxSizeGbOption");
            return base.Read(reader);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Curve boundary = null;
            int maxLod = 4;
            string cacheFolder = null;
            string apiKey = null;
            bool download = false;

            // Inputs: 0 B, 1 LOD, 2 Folder, 3 Key, 4 Download
            if (!da.GetData(0, ref boundary) || boundary == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary is required.");
                SetOutputs(da, new List<string> { "Error: Boundary is required." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            da.GetData(1, ref maxLod);
            da.GetData(2, ref cacheFolder);
            if (!da.GetData(3, ref apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "API Key is required.");
                SetOutputs(da, new List<string> { "Error: API Key is required." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            da.GetData(4, ref download);

            if (!boundary.TryGetPlane(out var boundaryPlane))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be planar.");
                SetOutputs(da, new List<string> { "Error: Boundary must be planar." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            if (maxLod < 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max LOD must be >= 0.");
                SetOutputs(da, new List<string> { "Error: Max LOD must be >= 0." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            if (string.IsNullOrWhiteSpace(cacheFolder))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Cache Folder is required.");
                SetOutputs(da, new List<string> { "Error: Cache Folder is required." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            if (maxSizeGbOption < 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max Size (GB) option must be >= 0.");
                SetOutputs(da, new List<string> { "Error: Max Size (GB) option must be >= 0." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }

            if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

            string boundaryHash = ComputeBoundaryHash(boundary);
            BoundingBox boundaryBox = boundary.GetBoundingBox(true);
            string manifestPath = Path.Combine(cacheFolder, ManifestPrefix + boundaryHash + ".json");

            // Cache clearing option
            if (download && clearCacheOption)
            {
                try
                {
                    Message = "Clearing cache...";
                    Grasshopper.Instances.RedrawCanvas();
                    foreach (var f in Directory.EnumerateFiles(cacheFolder, "*.glb"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                    foreach (var mf in Directory.EnumerateFiles(cacheFolder, ManifestPrefix + "*.json"))
                    {
                        try { File.Delete(mf); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Failed to clear cache: {ex.Message}");
                }
            }
            else if (!download && clearCacheOption)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clear Cache option ignored because Download is false.");
            }

            var activeDoc = RhinoDoc.ActiveDoc;
            if (activeDoc == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No active Rhino document.");
                SetOutputs(da, new List<string> { "Error: No active Rhino document." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            var eap = activeDoc.EarthAnchorPoint;
            if (eap == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "EarthAnchorPoint is null.");
                SetOutputs(da, new List<string> { "Error: EarthAnchorPoint is null." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            if (!eap.EarthLocationIsSet())
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "EarthAnchorPoint location not set.");
                SetOutputs(da, new List<string> { "Error: EarthAnchorPoint location not set." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }

            var aoi = boundary.ToPolyline(0, 0, 0.1, 0.1).ToPolyline();
            if (aoi == null || !aoi.IsClosed)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be a closed planar curve.");
                SetOutputs(da, new List<string> { "Error: Boundary must be a closed planar curve." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }

            var info = new List<string>();
            var usedFiles = new List<string>();

            // Try manifest first (works both when download=false and download=true) to avoid traversal when valid
            ManifestData manifestData;
            bool manifestValid = TryLoadManifest(manifestPath, out manifestData) && BoundaryMatches(manifestData, boundaryBox, 0.01);
            if (manifestValid)
            {
                // Ensure all listed tiles exist
                var manifestTilePaths = manifestData.Tiles.Select(t => Path.Combine(cacheFolder, t.File)).ToList();
                bool allExist = manifestTilePaths.All(File.Exists);
                if (!allExist)
                {
                    info.Add("Manifest found but some tile files missing - will proceed with normal process.");
                }
                else if (!download)
                {
                    // Cached-only and manifest matches: load
                    info.Add("Loaded tiles from manifest (cached mode).");
                    info.Add("Tile count: " + manifestTilePaths.Count);
                    LoadTilesFromList(da, manifestTilePaths, info);
                    return;
                }
                else if (download)
                {
                    // Download requested but manifest matches -> reuse local
                    info.Add("Manifest matches boundary - reusing cached tiles (skipped traversal + download).");
                    info.Add("Tile count: " + manifestTilePaths.Count);
                    LoadTilesFromList(da, manifestTilePaths, info);
                    return;
                }
            }
            else
            {
                if (!download)
                {
                    info.Add("No valid manifest for this boundary. Enable Download to fetch tiles.");
                    SetOutputs(da, info, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                    return;
                }
            }

            try
            {
                // Download mode (or manifest invalid)
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
                catch (Exception exWgs)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Failed to compute AOI WGS84 bounds: {exWgs.Message}");
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
                    SetOutputs(da, info.Concat(new[] { "Error: Failed to initialize TilesetWalker." }).ToList(), usedFiles, new List<Rhino.Geometry.Mesh>(), new List<object>());
                    return;
                }

                var plan = walker.PlanDownloads(root);
                foreach (var line in walker.Stats.ToInfoLines()) info.Add(line);

                long capBytes = (long)(maxSizeGbOption * 1024 * 1024 * 1024);
                List<string> localGlbs = new List<string>();
                long totalBytes = 0;
                int skippedForCap = 0;

                if (plan.Count > 0)
                {
                    totalTiles = plan.Count;
                    downloadedTiles = 0;
                    downloadedBytes = 0;
                    isDownloading = true;

                    Message = $"Downloading tiles (0/{totalTiles})...";
                    Grasshopper.Instances.RedrawCanvas();

                    var downloader = new TileDownloader(api, capBytes);
                    localGlbs = downloader.Ensure(plan, download, out totalBytes, out skippedForCap);

                    downloadedTiles = localGlbs.Count;
                    downloadedBytes = totalBytes;

                    double totalMB = totalBytes / (1024.0 * 1024.0);
                    Message = $"Downloaded {downloadedTiles}/{totalTiles} tiles ({totalMB:F1} MB)";
                    Grasshopper.Instances.RedrawCanvas();

                    info.Add(string.Format("Tiles planned: {0}, downloaded/used: {1}, bytes: {2:n0}", plan.Count, localGlbs.Count, totalBytes));
                    if (skippedForCap > 0) info.Add(string.Format("Skipped {0} tiles due to size cap ({1} GB).", skippedForCap, maxSizeGbOption));
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

                var meshes = new List<Rhino.Geometry.Mesh>();
                List<Grasshopper.Kernel.Types.GH_Material> mats = new List<Grasshopper.Kernel.Types.GH_Material>();
                if (localGlbs.Count > 0)
                {
                    var importMeshes = TileImporter.ImportMeshesOriented(localGlbs, out mats, out var importNotes);
                    meshes = importMeshes ?? new List<Rhino.Geometry.Mesh>();
                    info.AddRange(importNotes);
                }

                isDownloading = false;
                if (meshes.Count > 0)
                {
                    double totalMB = downloadedBytes / (1024.0 * 1024.0);
                    Message = $"Complete: {meshes.Count} meshes ({totalMB:F1} MB)";
                }
                else
                {
                    Message = "Complete: No meshes imported";
                }
                System.Threading.Thread.Sleep(100);
                Grasshopper.Instances.RedrawCanvas();

                // Write manifest for this boundary (even if 0 tiles? only if some tiles)
                if (usedFiles.Count > 0)
                {
                    try
                    {
                        var manifest = new ManifestData
                        {
                            Version = 1,
                            BoundaryHash = boundaryHash,
                            Min = new double[] { boundaryBox.Min.X, boundaryBox.Min.Y, boundaryBox.Min.Z },
                            Max = new double[] { boundaryBox.Max.X, boundaryBox.Max.Y, boundaryBox.Max.Z },
                            GeneratedUtc = DateTime.UtcNow,
                            Tiles = usedFiles.Select(p => new ManifestTile
                            {
                                File = Path.GetFileName(p),
                                Bytes = SafeFileSize(p)
                            }).ToList()
                        };
                        File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                        info.Add("Wrote manifest: " + Path.GetFileName(manifestPath));
                    }
                    catch (Exception mex)
                    {
                        info.Add("Failed to write manifest: " + mex.Message);
                    }
                }

                SetOutputs(da, info, usedFiles, meshes, mats.Cast<object>().ToList());
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("EarthAnchor") || ex.Message.Contains("ActiveDoc"))
            {
                isDownloading = false;
                Message = "Error: " + ex.Message;
                Grasshopper.Instances.RedrawCanvas();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                SetOutputs(da, info.Concat(new[] { "Error: " + ex.Message }).ToList(), new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
            }
            catch (Exception ex)
            {
                isDownloading = false;
                Message = "Error: " + ex.Message;
                Grasshopper.Instances.RedrawCanvas();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                SetOutputs(da, info.Concat(new[] { ex.ToString() }).ToList(), new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
            }
        }

        private void LoadTilesFromList(IGH_DataAccess da, List<string> tilePaths, List<string> info)
        {
            List<Grasshopper.Kernel.Types.GH_Material> mats;
            var meshes = TileImporter.ImportMeshesOriented(tilePaths, out mats, out var importNotes) ?? new List<Rhino.Geometry.Mesh>();
            info.AddRange(importNotes);
            SetOutputs(da, info, tilePaths, meshes, mats.Cast<object>().ToList());
            Message = $"Complete: {meshes.Count} meshes (cached)";
            System.Threading.Thread.Sleep(50);
            Grasshopper.Instances.RedrawCanvas();
        }

        private long SafeFileSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        private string ComputeBoundaryHash(Curve c)
        {
            Polyline pl;
            if (!c.TryGetPolyline(out pl))
            {
                pl = c.ToPolyline(0, 0, 0.1, 0.1).ToPolyline();
            }
            if (pl == null || pl.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var pt in pl)
            {
                sb.Append(Math.Round(pt.X, 5)).Append(',');
                sb.Append(Math.Round(pt.Y, 5)).Append(',');
                sb.Append(Math.Round(pt.Z, 5)).Append(';');
            }
            using (var md5 = MD5.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                var hash = md5.ComputeHash(bytes);
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        private bool TryLoadManifest(string path, out ManifestData data)
        {
            data = null;
            if (!File.Exists(path)) return false;
            try
            {
                data = JsonConvert.DeserializeObject<ManifestData>(File.ReadAllText(path));
                if (data == null) return false;
                if (data.Min == null || data.Max == null || data.Tiles == null) return false;
                return true;
            }
            catch { return false; }
        }

        private bool BoundaryMatches(ManifestData data, BoundingBox current, double tol)
        {
            if (data.Min.Length < 3 || data.Max.Length < 3) return false;
            bool minOk = Math.Abs(data.Min[0] - current.Min.X) <= tol && Math.Abs(data.Min[1] - current.Min.Y) <= tol;
            bool maxOk = Math.Abs(data.Max[0] - current.Max.X) <= tol && Math.Abs(data.Max[1] - current.Max.Y) <= tol;
            return minOk && maxOk; // ignore Z
        }

        private void SetOutputs(IGH_DataAccess da, List<string> info, List<string> files, List<Rhino.Geometry.Mesh> meshes, List<object> mats)
        {
            // Outputs: 0 Info, 1 Files, 2 Meshes, 3 Materials
            da.SetDataList(0, info);
            da.SetDataList(1, files);
            da.SetDataList(2, meshes);
            da.SetDataList(3, mats);
        }

        // Manifest classes
        private class ManifestData
        {
            public int Version { get; set; }
            public string BoundaryHash { get; set; }
            public double[] Min { get; set; }
            public double[] Max { get; set; }
            public DateTime GeneratedUtc { get; set; }
            public List<ManifestTile> Tiles { get; set; }
        }
        private class ManifestTile
        {
            public string File { get; set; }
            public long Bytes { get; set; }
        }
    }
}
