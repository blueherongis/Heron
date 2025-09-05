using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Heron.Components.Heron3DTiles
{
        /// <summary>
        /// Area-pruned, max-LOD traversal (no SSE). Honors refine where possible.
        /// </summary>
        public class TilesetWalker
        {
            private readonly GoogleTilesApi _api;
            private readonly (double minLon, double minLat, double maxLon, double maxLat) _aoiWgs84;
            private readonly int _maxLod;

            public TilesetWalker(GoogleTilesApi api,
                (double minLon, double minLat, double maxLon, double maxLat) aoiWgs84,
                int maxLod)
            {
                _api = api;
                _aoiWgs84 = aoiWgs84;
                _maxLod = Math.Max(0, maxLod);
            }

            public List<PlannedTile> PlanDownloads(Tileset root)
            {
                if (root?.Root == null) throw new Exception("Tileset has no root.");
                var list = new List<PlannedTile>();
                WalkNode(root.Root, depth: 0, parentRefine: root.Refine, list);
                return list;
            }

            private void WalkNode(TileNode node, int depth, string parentRefine, List<PlannedTile> outList)
            {
                if (node == null) return;

                // Spatial pruning: keep only tiles whose boundingVolume intersects AOI
                if (!IntersectsAoi(node.BoundingVolume))
                    return;

                var nodeRefine = node.Refine ?? parentRefine ?? "REPLACE";

                bool hasChildren = node.Children != null && node.Children.Count > 0;
                bool reachedLod = depth >= _maxLod;

                // If leaf or reached max LOD: plan this tile (if it has content)
                if (!hasChildren || reachedLod)
                {
                    if (node.Content?.EffectiveUri != null)
                    {
                        outList.Add(new PlannedTile
                        {
                            ContentUri = node.Content.EffectiveUri,
                            Depth = depth,
                            BV = node.BoundingVolume,
                            Refine = nodeRefine
                        });
                    }
                    return;
                }

                // If has children and not at LOD limit: descend
                // Children may themselves be inline nodes or separate tileset JSONs (implicit tiling/subtrees).
                foreach (var child in node.Children)
                {
                    // child may not have content but may be tileset.json node: if content is json -> fetch and inline
                    var c = child.Content?.EffectiveUri;
                    if (c != null && c.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        // Sub-tileset: fetch and inline root children
                        var subTs = _api.GetChildTileset(c);
                        if (subTs?.Root != null)
                            WalkNode(subTs.Root, depth + 1, subTs.Refine ?? nodeRefine, outList);
                        continue;
                    }

                    // Normal child tile (explicit)
                    WalkNode(child, depth + 1, nodeRefine, outList);
                }

                // If refine == ADD and this node has content, also include parent content
                if ((nodeRefine?.Equals("ADD", StringComparison.OrdinalIgnoreCase) ?? false) && node.Content?.EffectiveUri != null)
                {
                    outList.Add(new PlannedTile
                    {
                        ContentUri = node.Content.EffectiveUri,
                        Depth = depth,
                        BV = node.BoundingVolume,
                        Refine = nodeRefine
                    });
                }
            }

            private bool IntersectsAoi(BoundingVolume bv)
            {
                if (bv == null) return false;

                // Prefer region (west,south,east,north in radians)
                if (bv.Region != null && bv.Region.Length >= 6)
                {
                    // Convert to degrees
                    double west = GeoUtils.RadToDeg(bv.Region[0]);
                    double south = GeoUtils.RadToDeg(bv.Region[1]);
                    double east = GeoUtils.RadToDeg(bv.Region[2]);
                    double north = GeoUtils.RadToDeg(bv.Region[3]);

                    // Simple AABB overlap in lon/lat
                    bool lonOverlap = !(east < _aoiWgs84.minLon || west > _aoiWgs84.maxLon);
                    bool latOverlap = !(north < _aoiWgs84.minLat || south > _aoiWgs84.maxLat);
                    return lonOverlap && latOverlap;
                }

                // Fallback: if only sphere/box present (ECEF), conservatively keep
                // (You can extend by projecting sphere/box into lon/lat AABB.)
                return true;
            }
        }

}
