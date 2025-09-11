using System;
using System.Collections.Generic;
// Removed direct using of OSGeo.OSR to avoid name collision with the Heron GH component named CoordinateTransformation.
// Use an alias instead.
using OSR = OSGeo.OSR;
using Rhino.Geometry;

namespace Heron.Components.Heron3DTiles
{
    /// <summary>
    /// Area-pruned, max-LOD traversal with spatial + size heuristics and accurate ECEF pruning.
    /// </summary>
    public class TilesetWalker
    {
        private readonly GoogleTilesApi _api;
        private readonly List<Point3d> _aoiEcef;
        private readonly Point3d _aoiEcefMin, _aoiEcefMax, _aoiEcefCenter;
        private readonly double _aoiEcefRadius;
        private readonly int _maxLod;

        // Traversal budgets
        private const int TilePlanBudget = 20000;
        private const int JsonFetchBudget = 4000;
        private const int NodeVisitBudget = 80000;
        private const double LeafSizeRelaxFactor = 1.15;

        // AOI metrics (meters) for size heuristic - computed from ECEF bounds
        private readonly double _aoiWidthMeters;
        private readonly double _aoiHeightMeters;
        private readonly double _targetLeafWidthMeters;
        private readonly double _targetLeafHeightMeters;

        public TilesetWalker(GoogleTilesApi api, Polyline aoiModel, int maxLod)
        {
            _api = api;
            _maxLod = Math.Max(0, maxLod);
            
            // Convert AOI to densified ECEF polygon once
            try
            {
                _aoiEcef = GeoUtils.AoiToEcefDensified(aoiModel);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to convert AOI to ECEF coordinates. This may be due to missing or invalid EarthAnchorPoint. " +
                    "Ensure Rhino document is open and EarthAnchorPoint is properly set using Heron's SetEAP component.", ex);
            }
            
            // Precompute ECEF bounds for fast culling
            GeoUtils.ComputeEcefBounds(_aoiEcef, out _aoiEcefMin, out _aoiEcefMax, out _aoiEcefCenter, out _aoiEcefRadius);
            
            // Approximate AOI size in meters for size heuristics
            var diagonal = _aoiEcefMax - _aoiEcefMin;
            _aoiWidthMeters = Math.Max(diagonal.X, diagonal.Y); // Rough approximation
            _aoiHeightMeters = Math.Max(diagonal.X, diagonal.Y);
            
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

                // Spatial pruning - now using ECEF-first approach
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

        #region ECEF-First Intersection Tests
        
        private bool IntersectsAoi(BoundingVolume bv)
        {
            if (bv == null) return false;
            
            // Handle different BV types, all staying in ECEF
            if (bv.Box != null && bv.Box.Length >= 12)
            {
                return IntersectsObbEcef(bv.Box);
            }
            if (bv.Sphere != null && bv.Sphere.Length >= 4)
            {
                return IntersectsSphereEcef(bv.Sphere);
            }
            if (bv.Region != null && bv.Region.Length >= 6)
            {
                return IntersectsRegionEcef(bv.Region);
            }
            
            return true; // Conservative fallback
        }

        private bool IntersectsObbEcef(double[] box)
        {
            // Parse OBB: center + 3 half-axis vectors
            var center = new Point3d(box[0], box[1], box[2]);
            var hx = new Vector3d(box[3], box[4], box[5]);
            var hy = new Vector3d(box[6], box[7], box[8]);
            var hz = new Vector3d(box[9], box[10], box[11]);
            
            // Quick AABB rejection
            var obbExtents = new Vector3d(
                Math.Abs(hx.X) + Math.Abs(hy.X) + Math.Abs(hz.X),
                Math.Abs(hx.Y) + Math.Abs(hy.Y) + Math.Abs(hz.Y),
                Math.Abs(hx.Z) + Math.Abs(hy.Z) + Math.Abs(hz.Z));
            
            var obbMin = center - obbExtents;
            var obbMax = center + obbExtents;
            
            if (GeoUtils.IsAabbDisjoint(_aoiEcefMin, _aoiEcefMax, obbMin, obbMax)) return false;
            
            // Quick sphere rejection
            var dist = center.DistanceTo(_aoiEcefCenter);
            var obbRadius = Math.Sqrt(hx.Length * hx.Length + hy.Length * hy.Length + hz.Length * hz.Length);
            if (dist > _aoiEcefRadius + obbRadius) return false;
            
            // Precise 2D test in OBB local frame
            return Intersects2DInObbFrame(center, hx, hy, hz);
        }

        private bool Intersects2DInObbFrame(Point3d center, Vector3d hx, Vector3d hy, Vector3d hz)
        {
            // Normalize to get unit axes and half-lengths
            double lenX = hx.Length, lenY = hy.Length, lenZ = hz.Length;
            if (lenX == 0 || lenY == 0 || lenZ == 0) return true; // Degenerate OBB
            
            var axisX = hx / lenX;
            var axisY = hy / lenY;
            var axisZ = hz / lenZ;
            
            // Transform AOI points to OBB local coordinates, project to XY
            var localPoints = new List<Point2d>(_aoiEcef.Count);
            foreach (var ecefPt in _aoiEcef)
            {
                var relative = ecefPt - center;
                double localX = relative * axisX; // Dot product
                double localY = relative * axisY;
                // Drop Z for 2D test
                localPoints.Add(new Point2d(localX, localY));
            }
            
            // Test polygon vs rectangle intersection in 2D
            return GeoUtils.PolygonIntersectsRectangle(localPoints, lenX, lenY);
        }

        private bool IntersectsSphereEcef(double[] sphere)
        {
            if (sphere.Length < 4) return false;
            
            var sphereCenter = new Point3d(sphere[0], sphere[1], sphere[2]);
            double sphereRadius = sphere[3];
            
            // Quick sphere-sphere test
            double dist = sphereCenter.DistanceTo(_aoiEcefCenter);
            return dist <= _aoiEcefRadius + sphereRadius;
        }

        private bool IntersectsRegionEcef(double[] region)
        {
            if (region.Length < 6) return false;
            
            // Convert region bounds to ECEF box approximation
            double west = GeoUtils.RadToDeg(region[0]);
            double south = GeoUtils.RadToDeg(region[1]);
            double east = GeoUtils.RadToDeg(region[2]);
            double north = GeoUtils.RadToDeg(region[3]);
            double minHeight = region[4];
            double maxHeight = region[5];
            
            // Create ECEF box from region corners
            var corners = new Point3d[8];
            corners[0] = GeoUtils.Wgs84ToEcef(west, south, minHeight);
            corners[1] = GeoUtils.Wgs84ToEcef(east, south, minHeight);
            corners[2] = GeoUtils.Wgs84ToEcef(east, north, minHeight);
            corners[3] = GeoUtils.Wgs84ToEcef(west, north, minHeight);
            corners[4] = GeoUtils.Wgs84ToEcef(west, south, maxHeight);
            corners[5] = GeoUtils.Wgs84ToEcef(east, south, maxHeight);
            corners[6] = GeoUtils.Wgs84ToEcef(east, north, maxHeight);
            corners[7] = GeoUtils.Wgs84ToEcef(west, north, maxHeight);
            
            // Get AABB of region in ECEF
            var regionMin = corners[0];
            var regionMax = corners[0];
            foreach (var corner in corners)
            {
                if (corner.X < regionMin.X) regionMin = new Point3d(corner.X, regionMin.Y, regionMin.Z);
                if (corner.Y < regionMin.Y) regionMin = new Point3d(regionMin.X, corner.Y, regionMin.Z);
                if (corner.Z < regionMin.Z) regionMin = new Point3d(regionMin.X, regionMin.Y, corner.Z);
                if (corner.X > regionMax.X) regionMax = new Point3d(corner.X, regionMax.Y, regionMax.Z);
                if (corner.Y > regionMax.Y) regionMax = new Point3d(regionMax.X, corner.Y, regionMax.Z);
                if (corner.Z > regionMax.Z) regionMax = new Point3d(regionMax.X, regionMax.Y, corner.Z);
            }
            
            return !GeoUtils.IsAabbDisjoint(_aoiEcefMin, _aoiEcefMax, regionMin, regionMax);
        }

        #endregion

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
        #endregion
    }
}