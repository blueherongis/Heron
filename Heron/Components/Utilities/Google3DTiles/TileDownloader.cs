using System;
using System.Collections.Generic;

namespace Heron.Utilities.Google3DTiles
{
    public class TileDownloadResult
    {
        public string FilePath { get; set; }
        public long Bytes { get; set; }
        public TileCacheMetadata CacheMetadata { get; set; }
    }

    public class TileDownloader
    {
        private readonly GoogleTilesApi _api;
        private readonly long _capBytes;

        public TileDownloader(GoogleTilesApi api, long capBytes)
        {
            _api = api;
            _capBytes = capBytes <= 0 ? long.MaxValue : capBytes;
        }

        public List<TileDownloadResult> Ensure(List<PlannedTile> plan, bool download, out long totalBytes, out int skippedForCap)
        {
            var results = new List<TileDownloadResult>();
            totalBytes = 0;
            skippedForCap = 0;

            int failedCount = 0;
            Exception firstError = null;
            string firstErrorUri = null;

            foreach (var t in plan)
            {
                var uri = t.ContentUri;
                if (string.IsNullOrWhiteSpace(uri)) continue;

                bool fromCache = false;
                long bytes = 0;
                TileCacheMetadata cacheMetadata = null;

                try
                {
                    // Pre-cap check BEFORE downloading
                    // 1. If tile already cached we know precise size after EnsureGlb (fast path) but we can also HEAD first.
                    // 2. If not cached attempt HEAD to estimate size; if estimate available and would exceed cap, stop (partial download).

                    // Try HEAD first for uncached tiles when downloading (cheap) to avoid overshoot.
                    long estSize = -1;
                    if (download)
                    {
                        estSize = _api.HeadContentLength(uri);
                        if (estSize > 0)
                        {
                            if (totalBytes + estSize > _capBytes)
                            {
                                // Would exceed cap; do not download, mark partial and break (desired behavior: stop here)
                                skippedForCap++;
                                break;
                            }
                        }
                    }

                    // Now actually ensure the file (may download or pull from cache)
                    var f = _api.EnsureGlb(uri, download, out bytes, out fromCache, out cacheMetadata);

                    // If HEAD failed (estSize==-1) and we now know the real size after download; enforce cap.
                    if (totalBytes + bytes > _capBytes)
                    {
                        // Exceeds cap after obtaining tile.
                        // Remove file if we just downloaded it (avoid counting partial tile). If from cache we simply don't include it.
                        skippedForCap++;
                        try
                        {
                            if (!fromCache && System.IO.File.Exists(f))
                            {
                                // Delete the freshly downloaded file so that reruns can attempt again after raising cap.
                                System.IO.File.Delete(f);
                            }
                        }
                        catch { }
                        break; // Stop further downloads.
                    }

                    results.Add(new TileDownloadResult
                    {
                        FilePath = f,
                        Bytes = bytes,
                        CacheMetadata = cacheMetadata
                    });
                    totalBytes += bytes;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    if (firstError == null)
                    {
                        firstError = ex;
                        firstErrorUri = uri;
                    }
                    continue;
                }
            }

            // If nothing obtained AND we did not skip for cap -> real failure; else allow empty (cap too small / partial allowed)
            if (results.Count == 0 && skippedForCap == 0)
            {
                if (firstError != null)
                {
                    throw new Exception(
                        "Failed to obtain any tile content (" + (plan != null ? plan.Count : 0) + " planned). " +
                        "This often indicates malformed content URIs, missing required session token, or API access issues. " +
                        "Sample URI: '" + (firstErrorUri ?? "<null>") + "'. Error: " + firstError.Message
                    );
                }
                else
                {
                    throw new Exception("No tiles available: plan was empty or all tiles were invalid.");
                }
            }

            return results;
        }
    }
}
