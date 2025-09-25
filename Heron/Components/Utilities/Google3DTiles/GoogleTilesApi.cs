using System;
using System.Text;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace Heron.Utilities.Google3DTiles
{
    public class GoogleTilesApi
    {
        private readonly string _apiKey;
        private readonly string _cacheFolder;
        private static readonly HttpClient _http = new HttpClient();
        private string _session; // captured from first root/child link

        public GoogleTilesApi(string apiKey, string cacheFolder)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _cacheFolder = cacheFolder ?? throw new ArgumentNullException(nameof(cacheFolder));
        }

        public Tileset GetRootTileset()
        {
            // Root tileset is always JSON; pass explicit key here, session is captured from follow-up requests
            var rootUrl = $"https://tile.googleapis.com/v1/3dtiles/root.json?key={_apiKey}";
            var json = GetString(rootUrl);
            var ts = JsonConvert.DeserializeObject<Tileset>(json) ?? throw new Exception("Failed to parse root tileset.");
            return ts;
        }

        public Tileset GetChildTileset(string relativeJsonUri)
        {
            // child.content.uri can be relative ('/v1/3dtiles/...json') — BuildUrl handles host + key + session
            var url = BuildUrl(relativeJsonUri);
            var json = GetString(url);
            var ts = JsonConvert.DeserializeObject<Tileset>(json) ?? throw new Exception("Failed to parse child tileset.");
            TryCaptureSessionFromUri(relativeJsonUri);
            return ts;
        }

        public string EnsureGlb(string relativeGlbUri, bool download, out long bytes, out bool fromCache)
        {
            // IMPORTANT: Only GLB content should reach here; TilesetWalker avoids queuing .json
            var url = BuildUrl(relativeGlbUri);
            var hashName = Sha1(url) + ".glb"; // cache key based on full request (includes key/session)
            var local = Path.Combine(_cacheFolder, hashName);
            fromCache = File.Exists(local);
            bytes = 0;

            if (!download && fromCache) return local;
            if (!download && !fromCache) throw new Exception("Tile not in cache and Download=false.");

            if (fromCache)
            {
                bytes = new FileInfo(local).Length;
                return local;
            }

            // Fetch tile content and validate it is a GLB
            using (var resp = _http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"HTTP {(int)resp.StatusCode} when fetching tile content.");

                var data = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                // Validate GLB magic: 'glTF' (0x67 0x6C 0x54 0x46)
                bool looksGlb = data != null && data.Length >= 4 &&
                                data[0] == 0x67 && data[1] == 0x6C && data[2] == 0x54 && data[3] == 0x46;

                if (!looksGlb)
                {
                    // Helpful error to catch JSON replies or redirects without session
                    if (data != null && data.Length > 0 && (data[0] == (byte)'{' || data[0] == (byte)'['))
                    {
                        throw new Exception($"Expected GLB but received JSON for URI '{relativeGlbUri}'. " +
                                            $"This usually means the content URI points to a tileset (.json) " +
                                            $"or the request missed a required session parameter.");
                    }
                    throw new Exception($"Downloaded content is not a GLB (bad magic) for URI '{relativeGlbUri}'.");
                }

                File.WriteAllBytes(local, data);
                bytes = data.LongLength;
            }

            // Capture session token from the original relative URI if present
            TryCaptureSessionFromUri(relativeGlbUri);
            return local;
        }

        public long HeadContentLength(string relativeGlbUri)
        {
            try
            {
                var url = BuildUrl(relativeGlbUri);
                using (var req = new HttpRequestMessage(HttpMethod.Head, url))
                using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return -1;
                    if (resp.Content.Headers.ContentLength.HasValue)
                        return resp.Content.Headers.ContentLength.Value;
                }
            }
            catch { /* ignore */ }
            return -1;
        }

        private string BuildUrl(string relative)
        {
            // relative like: /v1/3dtiles/....glb?session=abc... or .../tileset.json?session=...
            string url = relative;
            if (relative.StartsWith("/")) url = "https://tile.googleapis.com" + relative;

            var delimiter = url.Contains("?") ? "&" : "?";
            url = $"{url}{delimiter}key={_apiKey}";

            // If we have a cached session but it's not on the URL, append it
            if (!string.IsNullOrEmpty(_session) && url.IndexOf("session=", StringComparison.OrdinalIgnoreCase) < 0)
                url += $"&session={_session}";

            return url;
        }

        private string GetString(string url)
        {
            var s = _http.GetStringAsync(url).GetAwaiter().GetResult();
            // Session can be present in returned URLs we later follow; also parse here for safety
            TryCaptureSessionFromUri(url);
            return s;
        }

        private void TryCaptureSessionFromUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return;

            // Ensure we have a query to parse
            string query = null;
            try
            {
                var u = uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(uri)
                    : new Uri("https://dummy.local" + (uri.StartsWith("/") ? uri : "/" + uri));
                query = u.Query; // includes leading '?'
            }
            catch
            {
                // Fallback: try to locate query manually
                var qIndex = uri.IndexOf('?');
                if (qIndex >= 0) query = uri.Substring(qIndex);
            }

            if (string.IsNullOrEmpty(query)) return;

            var ses = GetQueryParam(query, "session");
            if (!string.IsNullOrEmpty(ses)) _session = ses;
        }

        // Minimal querystring parser (avoids System.Web dependency)
        private static string GetQueryParam(string queryWithQuestion, string key)
        {
            if (string.IsNullOrEmpty(queryWithQuestion) || string.IsNullOrEmpty(key)) return null;
            var q = queryWithQuestion[0] == '?' ? queryWithQuestion.Substring(1) : queryWithQuestion;
            var parts = q.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                var kv = parts[i].Split(new[] { '=' }, 2);
                var k = Uri.UnescapeDataString(kv[0] ?? "");
                if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                return v;
            }
            return null;
        }

        private static string Sha1(string s)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
