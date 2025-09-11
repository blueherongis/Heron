using System;
using System.Collections.Generic;
// Removed direct using of OSGeo.OSR to avoid name collision with the Heron GH component named CoordinateTransformation.
// Use an alias instead.
using OSR = OSGeo.OSR;
using Rhino.Geometry;

namespace Heron.Components.Heron3DTiles
{
    /// <summary>
    /// Stats describing a tileset traversal.
    /// </summary>
    public class TilesetTraversalStats
    {
        public int PlannedGlbs;
        public int JsonFetches;
        public int NodeVisits;
        public int PrunedByAoi;
        public int LeafHeuristicStops;
        public int ExpandedJsonAtMaxDepth;
        public int MaxDepthSeen;
        public bool HitTilePlanBudget;
        public bool HitJsonFetchBudget;
        public bool HitNodeVisitBudget;
        public bool EmptyPlan;
        public string EmptyPlanReason;
        public double RelaxAoiMeters;
        public bool RelaxedMode => RelaxAoiMeters > 0;

        public IEnumerable<string> ToInfoLines()
        {
            yield return string.Format("Traversal: GLBs={0}, JsonFetches={1}, NodeVisits={2}, PrunedAoi={3}", PlannedGlbs, JsonFetches, NodeVisits, PrunedByAoi);
            yield return string.Format("HeuristicLeaves={0}, JsonAtMaxExpanded={1}, MaxDepthSeen={2}", LeafHeuristicStops, ExpandedJsonAtMaxDepth, MaxDepthSeen);
            if (HitTilePlanBudget || HitJsonFetchBudget || HitNodeVisitBudget)
            {
                yield return string.Format("Budgets hit: plan={0}, json={1}, visits={2}", HitTilePlanBudget, HitJsonFetchBudget, HitNodeVisitBudget);
            }
            if (RelaxedMode)
            {
                yield return string.Format("AOI relaxed by {0} m for pruning.", RelaxAoiMeters);
            }
            if (EmptyPlan && !string.IsNullOrEmpty(EmptyPlanReason))
            {
                yield return "Empty plan reason: " + EmptyPlanReason;
            }
        }
    }

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
        private readonly double _relaxAoiMeters;
        private readonly Point3d _aoiEcefMinExpanded, _aoiEcefMaxExpanded;
        private readonly double _aoiEcefRadiusExpanded;

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

        // Stats
        public TilesetTraversalStats Stats { get; private set; } = new TilesetTraversalStats();

        public TilesetWalker(GoogleTilesApi api, Polyline aoiModel, int maxLod, double relaxAoiMeters = 0.0)
        {
            _api = api;
            _maxLod = Math.Max(0, maxLod);
            _relaxAoiMeters = relaxAoiMeters < 0 ? 0 : relaxAoiMeters;

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

            // Expanded bounds if relaxing
            if (_relaxAoiMeters > 0)
            {
                _aoiEcefMinExpanded = new Point3d(_aoiEcefMin.X - _relaxAoiMeters, _aoiEcefMin.Y - _relaxAoiMeters, _aoiEcefMin.Z - _relaxAoiMeters);
                _aoiEcefMaxExpanded = new Point3d(_aoiEcefMax.X + _relaxAoiMeters, _aoiEcefMax.Y + _relaxAoiMeters, _aoiEcefMax.Z + _relaxAoiMeters);
                _aoiEcefRadiusExpanded = _aoiEcefRadius + _relaxAoiMeters;
            }
            else
            {
                _aoiEcefMinExpanded = _aoiEcefMin;
                _aoiEcefMaxExpanded = _aoiEcefMax;
                _aoiEcefRadiusExpanded = _aoiEcefRadius;
            }

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
            var stats = new TilesetTraversalStats();
            stats.RelaxAoiMeters = _relaxAoiMeters;
            Stats = stats; // reset

            if (root?.Root == null)
            {
                stats.EmptyPlan = true;
                stats.EmptyPlanReason = "Tileset root missing";
                throw new Exception("Tileset has no root.");
            }

            var planned = new List<PlannedTile>();
            var visitedJson = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var seenGlb = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase); // prevent duplicate GLB entries
            int jsonFetches = 0;
            int nodeVisits = 0;
            var stack = new Stack<System.Tuple<TileNode, int, string>>();
            stack.Push(System.Tuple.Create(root.Root, 0, root.Refine));

            while (stack.Count > 0)
            {
                if (planned.Count >= TilePlanBudget) { stats.HitTilePlanBudget = true; break; }
                if (jsonFetches >= JsonFetchBudget) { stats.HitJsonFetchBudget = true; break; }
                if (nodeVisits >= NodeVisitBudget) { stats.HitNodeVisitBudget = true; break; }

                var current = stack.Pop();
                var node = current.Item1;
                int depth = current.Item2;
                string parentRefine = current.Item3;
                if (node == null) continue;
                nodeVisits++;
                if (depth > stats.MaxDepthSeen) stats.MaxDepthSeen = depth;

                // Spatial pruning
                if (!IntersectsAoi(node.BoundingVolume)) { stats.PrunedByAoi++; continue; }

                var nodeRefine = node.Refine ?? parentRefine ?? "REPLACE";
                bool hasChildren = node.Children != null && node.Children.Count > 0;
                bool reachedLod = depth >= _maxLod;
                var uri = node.Content?.EffectiveUri;
                bool hasContent = !string.IsNullOrEmpty(uri);
                bool isJsonContent = hasContent && IsJsonUri(uri);
                bool isGlbContent = hasContent && IsGlbUri(uri);

                // Leaf logic: only treat as leaf if no children OR reached max LOD and geometry is directly available (GLB)
                bool treatAsLeaf = (!hasChildren) || (reachedLod && isGlbContent);

                // Region size heuristic to early-stop descent (avoid if only JSON available)
                if (!treatAsLeaf && TryRegionSizeMeters(node.BoundingVolume, out double regionWidthM, out double regionHeightM))
                {
                    if (regionWidthM <= _targetLeafWidthMeters * LeafSizeRelaxFactor &&
                        regionHeightM <= _targetLeafHeightMeters * LeafSizeRelaxFactor &&
                        (isGlbContent || !hasChildren))
                    {
                        treatAsLeaf = true;
                        stats.LeafHeuristicStops++;
                    }
                }

                if (treatAsLeaf)
                {
                    if (hasContent)
                    {
                        if (isJsonContent)
                        {
                            // JSON wrapper at or below max depth -> expand without increasing depth (JSON depth does not count)
                            if (!visitedJson.Contains(uri) && jsonFetches < JsonFetchBudget)
                            {
                                visitedJson.Add(uri);
                                jsonFetches++;
                                stats.ExpandedJsonAtMaxDepth++;
                                Tileset subTs = null;
                                try { subTs = _api.GetChildTileset(uri); }
                                catch { }
                                if (subTs?.Root != null)
                                {
                                    stack.Push(System.Tuple.Create(subTs.Root, depth, subTs.Refine ?? nodeRefine));
                                }
                            }
                        }
                        else if (isGlbContent)
                        {
                            if (seenGlb.Add(uri))
                            {
                                planned.Add(new PlannedTile { ContentUri = uri, Depth = depth, BV = node.BoundingVolume, Refine = nodeRefine });
                            }
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
                        bool childHasContent = !string.IsNullOrEmpty(childUri);
                        bool childIsJson = childHasContent && IsJsonUri(childUri);
                        if (childIsJson)
                        {
                            // Fetch child tileset (JSON wrapper) without depth increment (JSON depth ignored)
                            if (jsonFetches < JsonFetchBudget && !visitedJson.Contains(childUri))
                            {
                                var subTs = SafeFetchChildTileset(childUri, visitedJson, ref jsonFetches);
                                if (subTs?.Root != null)
                                    stack.Push(System.Tuple.Create(subTs.Root, depth, subTs.Refine ?? nodeRefine));
                            }
                            continue;
                        }
                        // Non-JSON child: normal depth increment
                        stack.Push(System.Tuple.Create(child, depth + 1, nodeRefine));
                    }
                }

                // refine == ADD include parent GLB
                if (nodeRefine.Equals("ADD", System.StringComparison.OrdinalIgnoreCase) && isGlbContent)
                {
                    if (seenGlb.Add(uri))
                    {
                        planned.Add(new PlannedTile { ContentUri = uri, Depth = depth, BV = node.BoundingVolume, Refine = nodeRefine });
                    }
                }
            }

            stats.PlannedGlbs = planned.Count;
            stats.JsonFetches = jsonFetches;
            stats.NodeVisits = nodeVisits;
            if (planned.Count == 0)
            {
                stats.EmptyPlan = true;
                if (stats.PrunedByAoi > 0 && nodeVisits > 0)
                    stats.EmptyPlanReason = "All nodes pruned by AOI";
                else if (stats.HitJsonFetchBudget)
                    stats.EmptyPlanReason = "JSON fetch budget hit before reaching GLBs";
                else if (stats.HitNodeVisitBudget)
                    stats.EmptyPlanReason = "Node visit budget hit";
                else if (stats.HitTilePlanBudget)
                    stats.EmptyPlanReason = "Tile plan budget hit";
                else
                    stats.EmptyPlanReason = "Traversal produced no GLB content (possible deep JSON wrappers beyond budgets)";
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
            var center = new Point3d(box[0], box[1], box[2]);
            var hx = new Vector3d(box[3], box[4], box[5]);
            var hy = new Vector3d(box[6], box[7], box[8]);
            var hz = new Vector3d(box[9], box[10], box[11]);

            // 1. AABB check (fast coarse check)
            var obbExtents = new Vector3d(
                Math.Abs(hx.X) + Math.Abs(hy.X) + Math.Abs(hz.X),
                Math.Abs(hx.Y) + Math.Abs(hy.Y) + Math.Abs(hz.Y),
                Math.Abs(hx.Z) + Math.Abs(hy.Z) + Math.Abs(hz.Z));
            
            var obbMin = center - obbExtents;
            var obbMax = center + obbExtents;
            
            if (GeoUtils.IsAabbDisjoint(_aoiEcefMinExpanded, _aoiEcefMaxExpanded, obbMin, obbMax)) return false;

            // 2. Sphere check (another fast coarse check)
            var dist = center.DistanceTo(_aoiEcefCenter);
            var obbRadius = Math.Sqrt(hx.Length * hx.Length + hy.Length * hy.Length + hz.Length * hz.Length);
            if (dist > _aoiEcefRadiusExpanded + obbRadius) return false;

            // If both coarse checks pass, assume intersection. This is more robust than
            // the complex 2D projection, which is prone to floating point errors at high LOD.
            return true;
        }

        private bool IntersectsSphereEcef(double[] sphere)
        {
            if (sphere.Length < 4) return false;
            var sphereCenter = new Point3d(sphere[0], sphere[1], sphere[2]);
            double sphereRadius = sphere[3];
            double dist = sphereCenter.DistanceTo(_aoiEcefCenter);
            return dist <= _aoiEcefRadiusExpanded + sphereRadius;
        }

        private bool IntersectsRegionEcef(double[] region)
        {
            if (region.Length < 6) return false;
            double west = GeoUtils.RadToDeg(region[0]);
            double south = GeoUtils.RadToDeg(region[1]);
            double east = GeoUtils.RadToDeg(region[2]);
            double north = GeoUtils.RadToDeg(region[3]);
            double minHeight = region[4];
            double maxHeight = region[5];
            var corners = new Point3d[8];
            corners[0] = GeoUtils.Wgs84ToEcef(west, south, minHeight);
            corners[1] = GeoUtils.Wgs84ToEcef(east, south, minHeight);
            corners[2] = GeoUtils.Wgs84ToEcef(east, north, minHeight);
            corners[3] = GeoUtils.Wgs84ToEcef(west, north, minHeight);
            corners[4] = GeoUtils.Wgs84ToEcef(west, south, maxHeight);
            corners[5] = GeoUtils.Wgs84ToEcef(east, south, maxHeight);
            corners[6] = GeoUtils.Wgs84ToEcef(east, north, maxHeight);
            corners[7] = GeoUtils.Wgs84ToEcef(west, north, maxHeight);
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
            return !GeoUtils.IsAabbDisjoint(_aoiEcefMinExpanded, _aoiEcefMaxExpanded, regionMin, regionMax);
        }

        #endregion

        #region Helpers
        private static bool IsJsonUri(string uri)
        {
            try { var u = StripQuery(uri); if (u.StartsWith("http", System.StringComparison.OrdinalIgnoreCase)) u = new System.Uri(u).AbsolutePath; return u.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase); }
            catch { return uri.IndexOf(".json", System.StringComparison.OrdinalIgnoreCase) >= 0; }
        }
        private static bool IsGlbUri(string uri)
        {
            try { var u = StripQuery(uri); if (u.StartsWith("http", System.StringComparison.OrdinalIgnoreCase)) u = new System.Uri(u).AbsolutePath; return u.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase); }
            catch { return uri.IndexOf(".glb", System.StringComparison.OrdinalIgnoreCase) >= 0; }
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