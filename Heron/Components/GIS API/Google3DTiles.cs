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
            // Order requested: Boundary, LOD (Zoom), Folder, API Key, Download
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
            p.AddIntegerParameter("Zoom", "lod", "0 to 20 20 = max Level of Detail.", GH_ParamAccess.item, 4);
            p.AddTextParameter("Cache Folder", "Fp", "Folder path to store tile cache (.glb files).", GH_ParamAccess.item, defaultCacheFolder);
            p.AddTextParameter("API Key", "K", "Google Maps Platform API key.", GH_ParamAccess.item);
            p.AddBooleanParameter("Run", "R", "If true, run and Download. If false, do nothing.", GH_ParamAccess.item, false);
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
            var doc = RhinoDoc.ActiveDoc;
            double modelTol = doc != null ? doc.ModelAbsoluteTolerance : 0.01;

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

            // Early bounding box validity check like other components
            var rawBBox = boundary.GetBoundingBox(true);
            if (!rawBBox.IsValid || rawBBox.Diagonal.Length <= modelTol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary bounding box invalid or too small.");
                SetOutputs(da, new List<string> { "Error: Invalid / tiny boundary." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
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

            // Create AOI polyline *before* computing manifest metrics to ensure consistency
            var aoi = boundary.ToPolyline(0, 0, 0.1, 0.1).ToPolyline();
            if (aoi == null || !aoi.IsClosed)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be a closed planar curve.");
                SetOutputs(da, new List<string> { "Error: Boundary must be a closed planar curve." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }
            var aoiBounds = aoi.BoundingBox;

            // WGS84 AOI bounds
            double minLon = 0, minLat = 0, maxLon = 0, maxLat = 0;
            try
            {
                var wgs = GeoUtils.AoiToWgs(aoi);
                minLon = wgs.minLon; minLat = wgs.minLat; maxLon = wgs.maxLon; maxLat = wgs.maxLat;
            }
            catch (Exception exWgsBounds)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Failed WGS84 bounds: " + exWgsBounds.Message);
            }

            string wgsVertexHash = ComputeWgsVertexHash(aoi);
            string boundaryHash = ComputeBoundaryHash(aoi.ToNurbsCurve(), maxLod); // model-space hash retained
            string manifestPath = Path.Combine(cacheFolder, ManifestPrefix + boundaryHash + ".json");

            // Cache clearing option
            if (download && clearCacheOption)
            {
                try
                {
                    Message = "Clearing cache...";
                    Grasshopper.Instances.RedrawCanvas();
                    foreach (var f in Directory.EnumerateFiles(cacheFolder, "*.glb")) { try { File.Delete(f); } catch { } }
                    foreach (var mf in Directory.EnumerateFiles(cacheFolder, ManifestPrefix + "*.json")) { try { File.Delete(mf); } catch { } }
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Failed to clear cache: " + ex.Message);
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
            if (eap == null || !eap.EarthLocationIsSet())
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "EarthAnchorPoint not set.");
                SetOutputs(da, new List<string> { "Error: EarthAnchorPoint not set." }, new List<string>(), new List<Rhino.Geometry.Mesh>(), new List<object>());
                return;
            }

            var info = new List<string>();
            var usedFiles = new List<string>();

            // Try manifest first (works both when download=false and download=true) to avoid traversal when valid
            ManifestData manifestData;
            Message = "Verifying manifest...";
            Grasshopper.Instances.RedrawCanvas();
            info.Add("Verifying manifest for boundary hash: " + boundaryHash);
            double matchTolModel = Math.Max(modelTol * 2.0, 0.01);
            double matchTolDeg = 1e-5; // ~1 meter
            bool manifestValid = TryLoadManifest(manifestPath, out manifestData) && NewBoundaryMatches(manifestData, aoiBounds, matchTolModel, minLon, minLat, maxLon, maxLat, matchTolDeg, wgsVertexHash, maxLod);
            if (manifestValid)
            {
                info.Add("Manifest found: " + Path.GetFileName(manifestPath));
                var manifestTilePaths = manifestData.Tiles.Select(t => Path.Combine(cacheFolder, t.File)).ToList();
                bool allExist = manifestTilePaths.All(File.Exists);
                if (!allExist)
                {
                    info.Add("Manifest tiles missing - proceeding with traversal.");
                }
                else if (!download)
                {
                    info.Add("All cached tiles present. Loading from cache (Download=false).");
                    LoadTilesFromList(da, manifestTilePaths, info, true, boundaryHash);
                    return;
                }
                else
                {
                    info.Add("Manifest matches boundary; skipping download.");
                    LoadTilesFromList(da, manifestTilePaths, info, true, boundaryHash);
                    return;
                }
            }
            else if (!download)
            {
                info.Add("No valid manifest for this AOI (model+geodetic). Enable Download.");
                SetOutputs(da, info, new List<string>(), new List<Mesh>(), new List<object>());
                Message = "No cached manifest";
                Grasshopper.Instances.RedrawCanvas();
                return;
            }

            try
            {
                // Download mode (or manifest invalid)
                Message = "Connecting to API...";
                Grasshopper.Instances.RedrawCanvas();

                var api = new GoogleTilesApi(apiKey, cacheFolder);
                var root = api.GetRootTileset();
                info.Add("Fetched root tileset.");

                try
                {
                    var wgs = GeoUtils.AoiToWgs(aoi);
                    info.Add(string.Format("AOI WGS84: [{0:F6},{1:F6}]–[{2:F6},{3:F6}]", wgs.minLon, wgs.minLat, wgs.maxLon, wgs.maxLat));
                    info.Add(string.Format("AOI Model: [{0:F2},{1:F2}]–[{2:F2},{3:F2}]", aoiBounds.Min.X, aoiBounds.Min.Y, aoiBounds.Max.X, aoiBounds.Max.Y));
                }
                catch (Exception exWgs2) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Failed AOI WGS84 bounds: " + exWgs2.Message); }

                Message = "Planning downloads...";
                Grasshopper.Instances.RedrawCanvas();

                TilesetWalker walker;
                try { walker = new TilesetWalker(api, aoi, maxLod); }
                catch (Exception exWalk)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Failed to initialize TilesetWalker: " + exWalk.Message);
                    SetOutputs(da, info.Concat(new[] { "Error: Failed to initialize TilesetWalker." }).ToList(), usedFiles, new List<Mesh>(), new List<object>());
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
                    totalTiles = plan.Count; downloadedTiles = 0; downloadedBytes = 0; isDownloading = true;
                    Message = $"Downloading (0/{totalTiles})"; Grasshopper.Instances.RedrawCanvas();
                    var downloader = new TileDownloader(api, capBytes);
                    localGlbs = downloader.Ensure(plan, download, out totalBytes, out skippedForCap);
                    downloadedTiles = localGlbs.Count; downloadedBytes = totalBytes;
                    double totalMB = totalBytes / (1024.0 * 1024.0);
                    Message = $"Downloaded {downloadedTiles}/{totalTiles} ({totalMB:F1} MB)"; Grasshopper.Instances.RedrawCanvas();
                    info.Add(string.Format("Tiles planned: {0}, downloaded/used: {1}, bytes: {2:n0}", plan.Count, localGlbs.Count, totalBytes));
                    if (skippedForCap > 0) info.Add(string.Format("Skipped {0} tiles due to size cap ({1} GB).", skippedForCap, maxSizeGbOption));
                }
                else info.Add("No tiles planned (even after any relaxed attempt).");

                usedFiles.AddRange(localGlbs);

                if (localGlbs.Count > 0) { Message = "Importing meshes..."; Grasshopper.Instances.RedrawCanvas(); }

                var meshes = new List<Mesh>();
                List<Grasshopper.Kernel.Types.GH_Material> mats = new List<Grasshopper.Kernel.Types.GH_Material>();
                if (localGlbs.Count > 0)
                {
                    var importMeshes = TileImporter.ImportMeshesOriented(localGlbs, out mats, out var importNotes);
                    meshes = importMeshes ?? new List<Mesh>();
                    info.AddRange(importNotes);
                }

                isDownloading = false;
                if (meshes.Count > 0)
                {
                    double totalMB = downloadedBytes / (1024.0 * 1024.0);
                    Message = $"Complete: {meshes.Count} meshes";
                    info.Add($"Complete: {meshes.Count} meshes ({totalMB:F1} MB)");
                }
                else { Message = "Complete: No meshes"; info.Add("Complete: No meshes imported."); }
                System.Threading.Thread.Sleep(100); Grasshopper.Instances.RedrawCanvas();

                if (usedFiles.Count > 0)
                {
                    try
                    {
                        var manifest = new ManifestData
                        {
                            Version = 2,
                            BoundaryHash = boundaryHash,
                            LOD = maxLod,
                            Min = new double[] { aoiBounds.Min.X, aoiBounds.Min.Y, aoiBounds.Min.Z },
                            Max = new double[] { aoiBounds.Max.X, aoiBounds.Max.Y, aoiBounds.Max.Z },
                            MinLon = minLon, MinLat = minLat, MaxLon = maxLon, MaxLat = maxLat,
                            WgsHash = wgsVertexHash,
                            GeneratedUtc = DateTime.UtcNow,
                            Tiles = usedFiles.Select(p => new ManifestTile { File = Path.GetFileName(p), Bytes = SafeFileSize(p) }).ToList()
                        };
                        File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                        info.Add("Wrote manifest: " + Path.GetFileName(manifestPath));
                    }
                    catch (Exception mex) { info.Add("Failed to write manifest: " + mex.Message); }
                }

                SetOutputs(da, info, usedFiles, meshes, mats.Cast<object>().ToList());
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("EarthAnchor") || ex.Message.Contains("ActiveDoc"))
            {
                isDownloading = false; Message = "Error: " + ex.Message; Grasshopper.Instances.RedrawCanvas(); AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                SetOutputs(da, info.Concat(new[] { "Error: " + ex.Message }).ToList(), new List<string>(), new List<Mesh>(), new List<object>());
            }
            catch (Exception ex)
            {
                isDownloading = false; Message = "Error: " + ex.Message; Grasshopper.Instances.RedrawCanvas(); AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                SetOutputs(da, info.Concat(new[] { ex.ToString() }).ToList(), new List<string>(), new List<Mesh>(), new List<object>());
            }
        }

        private void LoadTilesFromList(IGH_DataAccess da, List<string> tilePaths, List<string> info, bool fromManifest, string boundaryHash)
        {
            Message = fromManifest ? "Loading cached tiles..." : "Loading tiles..."; Grasshopper.Instances.RedrawCanvas();
            info.Add((fromManifest ? "Using manifest" : "Using cache") + " boundary hash: " + boundaryHash);
            info.Add("Tile files: " + tilePaths.Count);
            List<Grasshopper.Kernel.Types.GH_Material> mats;
            var meshes = TileImporter.ImportMeshesOriented(tilePaths, out mats, out var importNotes) ?? new List<Mesh>();
            info.AddRange(importNotes);
            Message = $"Complete: {meshes.Count} meshes (cached)"; info.Add($"Loaded {meshes.Count} meshes from cache.");
            System.Threading.Thread.Sleep(60); Grasshopper.Instances.RedrawCanvas();
            SetOutputs(da, info, tilePaths, meshes, mats.Cast<object>().ToList());
        }

        private long SafeFileSize(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

        private string ComputeBoundaryHash(Curve c, int lod)
        {
            Polyline pl; if (!c.TryGetPolyline(out pl)) { pl = c.ToPolyline(0, 0, 0.1, 0.1).ToPolyline(); }
            if (pl == null || pl.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var pt in pl) { sb.Append(Math.Round(pt.X, 5)).Append(',').Append(Math.Round(pt.Y, 5)).Append(',').Append(Math.Round(pt.Z, 5)).Append(';'); }
            sb.Append("LOD=").Append(lod);
            using (var md5 = MD5.Create()) { var bytes = Encoding.UTF8.GetBytes(sb.ToString()); var hash = md5.ComputeHash(bytes); return string.Concat(hash.Select(b => b.ToString("x2"))); }
        }

        private string ComputeWgsVertexHash(Polyline pl)
        {
            if (pl == null || pl.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < pl.Count; i++)
            {
                var w = Heron.Convert.XYZToWGS(pl[i]); // X=Lon, Y=Lat assumed
                sb.Append(Math.Round(w.X, 6)).Append(',').Append(Math.Round(w.Y, 6)).Append(';');
            }
            using (var md5 = MD5.Create()) { var bytes = Encoding.UTF8.GetBytes(sb.ToString()); var hash = md5.ComputeHash(bytes); return string.Concat(hash.Select(b => b.ToString("x2"))); }
        }

        private bool TryLoadManifest(string path, out ManifestData data)
        {
            data = null; if (!File.Exists(path)) return false; try { data = JsonConvert.DeserializeObject<ManifestData>(File.ReadAllText(path)); if (data == null) return false; if (data.Min == null || data.Max == null || data.Tiles == null) return false; return true; } catch { return false; }
        }

        // New validation including geodetic info. Falls back to legacy model-space only if Version < 2
        private bool NewBoundaryMatches(ManifestData data, BoundingBox modelCurrent, double modelTol, double minLon, double minLat, double maxLon, double maxLat, double degTol, string wgsHash, int lod)
        {
            if (data.Version >= 2)
            {
                if (data.LOD != lod) return false;
                bool wgsOk = Math.Abs(data.MinLon - minLon) <= degTol && Math.Abs(data.MinLat - minLat) <= degTol && Math.Abs(data.MaxLon - maxLon) <= degTol && Math.Abs(data.MaxLat - maxLat) <= degTol;
                bool hashOk = string.Equals(data.WgsHash, wgsHash, StringComparison.OrdinalIgnoreCase);
                if (!wgsOk || !hashOk) return false;
            }
            // Always verify model bbox (legacy compatibility)
            bool minOk = Math.Abs(data.Min[0] - modelCurrent.Min.X) <= modelTol && Math.Abs(data.Min[1] - modelCurrent.Min.Y) <= modelTol;
            bool maxOk = Math.Abs(data.Max[0] - modelCurrent.Max.X) <= modelTol && Math.Abs(data.Max[1] - modelCurrent.Max.Y) <= modelTol;
            return minOk && maxOk;
        }

        private void SetOutputs(IGH_DataAccess da, List<string> info, List<string> files, List<Mesh> meshes, List<object> mats)
        { da.SetDataList(0, info); da.SetDataList(1, files); da.SetDataList(2, meshes); da.SetDataList(3, mats); }

        // Manifest classes
        private class ManifestData
        {
            public int Version { get; set; }
            public string BoundaryHash { get; set; }
            public int LOD { get; set; } // Added in version 2
            public double[] Min { get; set; }
            public double[] Max { get; set; }
            public double MinLon { get; set; } // Added in version 2
            public double MinLat { get; set; }
            public double MaxLon { get; set; }
            public double MaxLat { get; set; }
            public string WgsHash { get; set; } // Added in version 2
            public DateTime GeneratedUtc { get; set; }
            public List<ManifestTile> Tiles { get; set; }
        }
        private class ManifestTile { public string File { get; set; } public long Bytes { get; set; } }
    }
}
