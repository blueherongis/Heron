using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Heron.Components.Heron3DTiles
{
    // Minimal models for 3D Tiles / Google tileset
    public class Tileset
    {
        [JsonProperty("root")] public TileNode Root { get; set; }
        [JsonProperty("geometricError")] public double GeometricError { get; set; }
        [JsonProperty("refine")] public string Refine { get; set; }
    }

    public class TileNode
    {
        [JsonProperty("boundingVolume")] public BoundingVolume BoundingVolume { get; set; }
        [JsonProperty("geometricError")] public double GeometricError { get; set; }
        [JsonProperty("refine")] public string Refine { get; set; } // "ADD" or "REPLACE"
        [JsonProperty("content")] public TileContent Content { get; set; }
        [JsonProperty("children")] public List<TileNode> Children { get; set; }
    }

    public class TileContent
    {
        // Google uses "uri" frequently; spec also allows "url"
        [JsonProperty("uri")] public string Uri { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        public string EffectiveUri => !string.IsNullOrEmpty(Uri) ? Uri : Url;
    }

    public class BoundingVolume
    {
        // 3D Tiles allows: "region" (radians, min/max heights), "box" (ECEF OBB), "sphere" (ECEF)
        // We'll prioritize region (best for spatial pruning).
        [JsonProperty("region")] public double[] Region { get; set; }   // [west,south,east,north,minH,maxH] in radians
        [JsonProperty("box")] public double[] Box { get; set; }         // 12/16 numbers (center + axes)
        [JsonProperty("sphere")] public double[] Sphere { get; set; }   // [x,y,z,r] ECEF
    }

    public class PlannedTile
    {
        public string ContentUri;   // relative (Google)
        public int Depth;
        public BoundingVolume BV;
        public string Refine;       // ADD/REPLACE/null
    }

}
