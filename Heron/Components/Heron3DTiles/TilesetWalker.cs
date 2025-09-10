using System;
using System.Collections.Generic;
// Removed direct using of OSGeo.OSR to avoid name collision with the Heron GH component named CoordinateTransformation.
// Use an alias instead.
using OSR = OSGeo.OSR;

namespace Heron.Components.Heron3DTiles
{
    /// <summary>
    /// Area-pruned, max-LOD traversal with spatial + size heuristics and accurate ECEF pruning.
    /// </summary>
    public class TilesetWalker
    {
        private readonly GoogleTilesApi _api;
        private readonly (double minLon, double minLat, double maxLon, double maxLat) _aoiWgs84;
        private readonly int _maxLod;

        // Traversal budgets
        private const int TilePlanBudget = 20000;
        private const int JsonFetchBudget = 4000;
        private const int NodeVisitBudget = 80000;
        private const double LeafSizeRelaxFactor = 1.15;

        // AOI metrics (meters) for size heuristic
        private readonly double _aoiWidthMeters;
        private readonly double _aoiHeightMeters;
        private readonly double _targetLeafWidthMeters;
        private readonly double _targetLeafHeightMeters;

        // Lazy GDAL (OSR) transforms (EPSG:4978 geocentric -> EPSG:4326 geographic)
        private static OSR.SpatialReference _sGeocentric;
        private static OSR.SpatialReference _sGeographic;
        private static OSR.CoordinateTransformation _ecefToWgs;

        public TilesetWalker(GoogleTilesApi api,
            (double minLon, double minLat, double maxLon, double maxLat) aoiWgs84,
            int maxLod)
        {
            _api = api;
            _aoiWgs84 = aoiWgs84;
            _maxLod = Math.Max(0, maxLod);

            // Approximate AOI size in meters (using average latitude for lon scaling)
            double midLatRad = DegToRad((aoiWgs84.minLat + aoiWgs84.maxLat) * 0.5);
            double dLon = aoiWgs84.maxLon - aoiWgs84.minLon;
            double dLat = aoiWgs84.maxLat - aoiWgs84.minLat;
            const double metersPerDegLat = 111320.0;
            double metersPerDegLon = metersPerDegLat * Math.Cos(midLatRad);
            _aoiWidthMeters = Math.Max(0, dLon * metersPerDegLon);
            _aoiHeightMeters = Math.Max(0, dLat * metersPerDegLat);
            double denom = Math.Pow(2.0, _maxLod <= 0 ? 1 : _maxLod);
            _targetLeafWidthMeters = (_aoiWidthMeters / denom);
            _targetLeafHeightMeters = (_aoiHeightMeters / denom);
        }

        public List<PlannedTile> PlanDownloads(Tileset root)
        {
            if (root?.Root == null)
                throw new Exception("Tileset has no root.");

            var planned = new List<PlannedTile>();
            var visitedJson = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int jsonFetches = 0;
            int nodeVisits = 0;
            var stack = new Stack<Tuple<TileNode, int, string>>();
            stack.Push(Tuple.Create(root.Root, 0, root.Refine));

            while (stack.Count > 0)
            {
                if (planned.Count >= TilePlanBudget) break;
                if (jsonFetches >= JsonFetchBudget) break;
                if (nodeVisits >= NodeVisitBudget) break;

                var current = stack.Pop();
                var node = current.Item1;
                int depth = current.Item2;
                string parentRefine = current.Item3;
                if (node == null) continue;
                nodeVisits++;

                // Spatial pruning
                if (!IntersectsAoi(node.BoundingVolume)) continue;

                var nodeRefine = node.Refine ?? parentRefine ?? "REPLACE";
                bool hasChildren = node.Children != null && node.Children.Count > 0;
                bool reachedLod = depth >= _maxLod;
                var uri = node.Content?.EffectiveUri;
                bool hasContent = !string.IsNullOrEmpty(uri);
                bool treatAsLeaf = reachedLod || !hasChildren;

                // Region size heuristic to early-stop descent
                if (!treatAsLeaf && TryRegionSizeMeters(node.BoundingVolume, out double regionWidthM, out double regionHeightM))
                {
                    if (regionWidthM <= _targetLeafWidthMeters * LeafSizeRelaxFactor &&
                        regionHeightM <= _targetLeafHeightMeters * LeafSizeRelaxFactor)
                    {
                        treatAsLeaf = true;
                    }
                }

                if (treatAsLeaf)
                {
                    if (hasContent)
                    {
                        if (IsJsonUri(uri))
                        {
                            if (depth < _maxLod && jsonFetches < JsonFetchBudget && !visitedJson.Contains(uri))
                            {
                                var subTs = SafeFetchChildTileset(uri, visitedJson, ref jsonFetches);
                                if (subTs?.Root != null)
                                    stack.Push(Tuple.Create(subTs.Root, depth + 1, subTs.Refine ?? nodeRefine));
                            }
                        }
                        else if (IsGlbUri(uri))
                        {
                            planned.Add(new PlannedTile { ContentUri = uri, Depth = depth, BV = node.BoundingVolume, Refine = nodeRefine });
                        }
                    }
                    continue;
                }

                // Descend children
                if (hasChildren)
                {
                    foreach (var child in node.Children)
                    {
                        var childUri = child.Content?.EffectiveUri;
                        if (!string.IsNullOrEmpty(childUri) && IsJsonUri(childUri))
                        {
                            if (depth + 1 <= _maxLod && jsonFetches < JsonFetchBudget && !visitedJson.Contains(childUri))
                            {
                                var subTs = SafeFetchChildTileset(childUri, visitedJson, ref jsonFetches);
                                if (subTs?.Root != null)
                                    stack.Push(Tuple.Create(subTs.Root, depth + 1, subTs.Refine ?? nodeRefine));
                            }
                            continue;
                        }
                        stack.Push(Tuple.Create(child, depth + 1, nodeRefine));
                    }
                }

                // refine == ADD include parent GLB
                if (nodeRefine.Equals("ADD", StringComparison.OrdinalIgnoreCase) && hasContent && IsGlbUri(uri))
                {
                    planned.Add(new PlannedTile { ContentUri = uri, Depth = depth, BV = node.BoundingVolume, Refine = nodeRefine });
                }
            }

            return planned;
        }

        private Tileset SafeFetchChildTileset(string jsonUri, HashSet<string> visitedJson, ref int jsonFetches)
        {
            try
            {
                visitedJson.Add(jsonUri);
                jsonFetches++;
                return _api.GetChildTileset(jsonUri);
            }
            catch { return null; }
        }

        #region Helpers
        private static bool IsJsonUri(string uri)
        {
            try { var u = StripQuery(uri); if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = new Uri(u).AbsolutePath; return u.EndsWith(".json", StringComparison.OrdinalIgnoreCase); }
            catch { return uri.IndexOf(".json", StringComparison.OrdinalIgnoreCase) >= 0; }
        }
        private static bool IsGlbUri(string uri)
        {
            try { var u = StripQuery(uri); if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = new Uri(u).AbsolutePath; return u.EndsWith(".glb", StringComparison.OrdinalIgnoreCase); }
            catch { return uri.IndexOf(".glb", StringComparison.OrdinalIgnoreCase) >= 0; }
        }
        private static string StripQuery(string uri) { int q = uri.IndexOf('?'); return q >= 0 ? uri.Substring(0, q) : uri; }
        private static double DegToRad(double d) { return d * Math.PI / 180.0; }

        private void EnsureEcefToWgs()
        {
            if (_ecefToWgs != null) return;
            _sGeocentric = new OSR.SpatialReference("");
            _sGeocentric.ImportFromEPSG(4978);
            _sGeographic = new OSR.SpatialReference("");
            _sGeographic.ImportFromEPSG(4326);
            _ecefToWgs = new OSR.CoordinateTransformation(_sGeocentric, _sGeographic);
        }

        private void EcefToLonLat(double x, double y, double z, out double lonDeg, out double latDeg)
        {
            EnsureEcefToWgs();
            double[] xyz = { x, y, z };
            _ecefToWgs.TransformPoint(xyz); // modifies in place: lon, lat, h
            lonDeg = xyz[0];
            latDeg = xyz[1];
        }

        private bool TryRegionSizeMeters(BoundingVolume bv, out double widthM, out double heightM)
        {
            widthM = heightM = 0;
            if (bv?.Region == null || bv.Region.Length < 4) return false;
            double west = GeoUtils.RadToDeg(bv.Region[0]);
            double south = GeoUtils.RadToDeg(bv.Region[1]);
            double east = GeoUtils.RadToDeg(bv.Region[2]);
            double north = GeoUtils.RadToDeg(bv.Region[3]);
            double midLatRad = DegToRad((south + north) * 0.5);
            const double metersPerDegLat = 111320.0;
            double metersPerDegLon = metersPerDegLat * Math.Cos(midLatRad);
            double dLon = Math.Max(0, east - west);
            double dLat = Math.Max(0, north - south);
            widthM = dLon * metersPerDegLon;
            heightM = dLat * metersPerDegLat;
            return true;
        }

        private bool IntersectsAoi(BoundingVolume bv)
        {
            if (bv == null) return false;
            // region first
            if (bv.Region != null && bv.Region.Length >= 4)
            {
                double west = GeoUtils.RadToDeg(bv.Region[0]);
                double south = GeoUtils.RadToDeg(bv.Region[1]);
                double east = GeoUtils.RadToDeg(bv.Region[2]);
                double north = GeoUtils.RadToDeg(bv.Region[3]);
                return LonLatOverlap(west, south, east, north);
            }
            // box
            if (bv.Box != null && (bv.Box.Length == 12 || bv.Box.Length == 16))
            {
                return BoxEcefOverlap(bv.Box);
            }
            // sphere
            if (bv.Sphere != null && bv.Sphere.Length >= 4)
            {
                return SphereEcefOverlap(bv.Sphere);
            }
            // fallback conservative keep
            return true;
        }

        private bool LonLatOverlap(double west, double south, double east, double north)
        {
            bool lonOverlap = !(east < _aoiWgs84.minLon || west > _aoiWgs84.maxLon);
            bool latOverlap = !(north < _aoiWgs84.minLat || south > _aoiWgs84.maxLat);
            return lonOverlap && latOverlap;
        }

        private bool BoxEcefOverlap(double[] box)
        {
            try
            {
                double cx = box[0], cy = box[1], cz = box[2];
                double axx = box[3], axy = box[4], axz = box[5];
                double ayx = box[6], ayy = box[7], ayz = box[8];
                double azx = box[9], azy = box[10], aaz = box[11];
                double minLon = double.MaxValue, maxLon = double.MinValue;
                double minLat = double.MaxValue, maxLat = double.MinValue;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    double x = cx + sx * axx + sy * ayx + sz * azx;
                    double y = cy + sx * axy + sy * ayy + sz * azy;
                    double z = cz + sx * axz + sy * ayz + sz * aaz;
                    EcefToLonLat(x, y, z, out double lon, out double lat);
                    if (lon < minLon) minLon = lon;
                    if (lon > maxLon) maxLon = lon;
                    if (lat < minLat) minLat = lat;
                    if (lat > maxLat) maxLat = lat;
                }
                return LonLatOverlap(minLon, minLat, maxLon, maxLat);
            }
            catch { return false; }
        }

        private bool SphereEcefOverlap(double[] sphere)
        {
            try
            {
                double x = sphere[0], y = sphere[1], z = sphere[2], r = sphere[3];
                EcefToLonLat(x, y, z, out double lonDeg, out double latDeg);
                // WGS84 ellipsoid parameters
                const double a = 6378137.0; const double f = 1.0 / 298.257223563; double e2 = f * (2 - f);
                double latRad = DegToRad(latDeg); double sinLat = Math.Sin(latRad); double cosLat = Math.Cos(latRad);
                double denom = Math.Sqrt(1 - e2 * sinLat * sinLat); double N = a / denom; double M = a * (1 - e2) / (denom * denom * denom);
                double dLatRad = r / M; double dLonRad = r / (N * cosLat);
                double dLatDeg = dLatRad * 180.0 / Math.PI; double dLonDeg = dLonRad * 180.0 / Math.PI;
                double west = lonDeg - dLonDeg; double east = lonDeg + dLonDeg; double south = latDeg - dLatDeg; double north = latDeg + dLatDeg;
                return LonLatOverlap(west, south, east, north);
            }
            catch { return false; }
        }
        #endregion
    }
}