using System;
using System.Collections.Generic;

namespace Heron.Components.Heron3DTiles
{
        public class TileDownloader
        {
            private readonly GoogleTilesApi _api;
            private readonly long _capBytes;

            public TileDownloader(GoogleTilesApi api, long capBytes)
            {
                _api = api;
                _capBytes = capBytes <= 0 ? long.MaxValue : capBytes;
            }

            public List<string> Ensure(List<PlannedTile> plan, bool download, out long totalBytes, out int skippedForCap)
            {
                var files = new List<string>();
                totalBytes = 0;
                skippedForCap = 0;

                foreach (var t in plan)
                {
                    // Try estimate via HEAD if we’re close to cap
                    if (download && totalBytes > 0.8 * _capBytes)
                    {
                        long est = _api.HeadContentLength(t.ContentUri);
                        if (est > 0 && totalBytes + est > _capBytes)
                        {
                            skippedForCap++;
                            continue;
                        }
                    }

                    bool fromCache;
                    long bytes;
                    try
                    {
                        var f = _api.EnsureGlb(t.ContentUri, download, out bytes, out fromCache);
                        files.Add(f);
                        totalBytes += bytes;
                        if (download && totalBytes > _capBytes)
                        {
                            // Cap exceeded after this tile — drop last and break
                            files.RemoveAt(files.Count - 1);
                            skippedForCap++;
                            totalBytes -= bytes;
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        // Skip tiles that fail (network/cache miss with download=false)
                        continue;
                    }
                }

                return files;
            }
        }
    

}
