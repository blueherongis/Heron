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

                int failedCount = 0;
                Exception firstError = null;
                string firstErrorUri = null;

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
                    catch (Exception ex)
                    {
                        // Record the first failure to report a meaningful error if everything fails
                        failedCount++;
                        if (firstError == null)
                        {
                            firstError = ex;
                            firstErrorUri = t.ContentUri;
                        }
                        continue;
                    }
                }

                // If nothing could be retrieved from cache or network, surface why
                if (files.Count == 0)
                {
                    if (firstError != null)
                    {
                        throw new Exception(
                            "Failed to obtain any tile content (" + plan.Count + " planned). " +
                            "This often indicates malformed content URIs, missing required session token, or API access issues. " +
                            "Sample URI: '" + (firstErrorUri ?? "<null>") + "'. Error: " + firstError.Message
                        );
                    }
                    else
                    {
                        throw new Exception("No tiles available: plan was empty or all tiles were skipped due to size cap.");
                    }
                }

                return files;
            }
        }
    

}
