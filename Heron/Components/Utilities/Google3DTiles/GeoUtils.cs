using og = OSGeo.OSR;
using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Heron.Utilities.Google3DTiles
{
    /// <summary>
    /// Simple 3D vector struct for ECEF calculations
    /// </summary>
    public readonly struct Vec3d
    {
        public readonly double X, Y, Z;

        public Vec3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3d operator +(in Vec3d a, in Vec3d b) => new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3d operator -(in Vec3d a, in Vec3d b) => new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3d operator *(in Vec3d a, double s) => new Vec3d(a.X * s, a.Y * s, a.Z * s);
        public static double Dot(in Vec3d a, in Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vec3d Normalized()
        {
            var L = Length();
            return L > 0 ? this * (1.0 / L) : this;
        }
    }

    public static partial class GeoUtils
    {
        // WGS84 constants
        const double a = 6378137.0; // semi-major
        const double f = 1.0 / 298.257223563;
        const double b = a * (1 - f); // semi-minor
        const double e2 = 1 - (b * b) / (a * a);
        public static double RadToDeg(double r) => r * (180.0 / Math.PI);


        // Build min/max lon/lat via Heron
        public static (double minLon, double minLat, double maxLon, double maxLat) AoiToWgs(Rhino.Geometry.Polyline pl)
        {
            double minLon = double.PositiveInfinity, minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity, maxLat = double.NegativeInfinity;

            foreach (var p in pl)
            {
                Point3d w;
                try
                {
                    w = Heron.Convert.XYZToWGS(p); // Heron EAP → WGS84
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Failed to convert model coordinates to WGS84. This typically indicates that RhinoDoc.ActiveDoc or EarthAnchorPoint is null, " +
                        "disposed, or improperly initialized. Ensure Rhino document is open and EarthAnchorPoint is set using Heron's SetEAP component.", ex);
                }
                
                minLon = Math.Min(minLon, w.X);
                maxLon = Math.Max(maxLon, w.X);
                minLat = Math.Min(minLat, w.Y);
                maxLat = Math.Max(maxLat, w.Y);
            }

            return (minLon, minLat, maxLon, maxLat);
        }

        /// <summary>
        /// Convert AOI polyline to densified ECEF polygon for accurate tile intersection tests
        /// </summary>
        public static List<Point3d> AoiToEcefDensified(Polyline aoiModel, double maxChordMeters = 50.0)
        {
            // Validate inputs
            if (aoiModel == null || aoiModel.Count == 0)
            {
                throw new ArgumentException("AOI polyline cannot be null or empty.");
            }

            // Check if Rhino document and EarthAnchorPoint are available
            var activeDoc = Rhino.RhinoDoc.ActiveDoc;
            if (activeDoc == null)
            {
                throw new InvalidOperationException(
                    "No active Rhino document found. Ensure Rhino is running and a document is open.");
            }

            var eap = activeDoc.EarthAnchorPoint;
            if (eap == null)
            {
                throw new InvalidOperationException(
                    "EarthAnchorPoint is null. This may indicate the document is disposed or corrupted.");
            }

            if (!eap.EarthLocationIsSet())
            {
                throw new InvalidOperationException(
                    "EarthAnchorPoint location has not been set. Use Heron's SetEAP component to set the Earth Anchor Point before using 3D Tiles components.");
            }

            var wgsPoints = new List<Point3d>();

            // Convert to WGS84 first with proper error handling
            foreach (var pt in aoiModel)
            {
                Point3d wgs;
                try
                {
                    wgs = Heron.Convert.XYZToWGS(pt);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to convert model coordinates to WGS84 at point {pt}. " +
                        "This may indicate memory corruption or threading issues with RhinoDoc access.", ex);
                }
                wgsPoints.Add(wgs);
            }

            // Densify edges along geodesics
            var densified = DensifyGeodesic(wgsPoints, maxChordMeters);

            // Convert to ECEF once
            var ecefPoints = new List<Point3d>();
            foreach (var wgs in densified)
            {
                try
                {
                    // FIX: Heron convention is w.X=lon, w.Y=lat, so pass correctly to Wgs84ToEcef(lon, lat, h)
                    var ecef = Wgs84ToEcef(wgs.X, wgs.Y, wgs.Z); // lon, lat, h
                    ecefPoints.Add(new Point3d(ecef.X, ecef.Y, ecef.Z));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to convert WGS84 coordinates to ECEF at point lon={wgs.X}, lat={wgs.Y}, h={wgs.Z}.", ex);
                }
            }

            return ecefPoints;
        }


        /// <summary>
        /// Densifies a closed polygonal ring expressed in WGS84 (Heron convention: X=longitude°, Y=latitude°, Z=height)
        /// by inserting intermediate vertices along each edge so that the great-circle (spherical) chord length
        /// between consecutive points does not exceed the specified maximum.
        /// </summary>
        /// <param name="wgsPoints">
        /// Ordered list of WGS84 points (lon/lat in degrees). The last point need not repeat the first;
        /// the method treats the sequence as closed by connecting the final point back to the first.
        /// </param>
        /// <param name="maxChordMeters">
        /// Maximum allowed chord length (approximate great-circle distance) in meters between consecutive
        /// output vertices. Edges longer than this are subdivided uniformly by spherical interpolation.
        /// </param>
        /// <returns>
        /// A new list of WGS84 points (lon/lat in degrees, height currently set to 0 for inserted points) with
        /// additional vertices inserted along great-circle arcs. Original vertices are preserved in order.
        /// </returns>
        /// <remarks>
        /// Implementation details:
        /// 1. Uses a Haversine-based spherical distance (ApproximateDistance) to estimate edge length.
        /// 2. Number of segments per edge = ceil(distance / maxChordMeters); if distance <= maxChordMeters no subdivision.
        /// 3. Intermediate points computed via spherical linear interpolation (InterpolateGeodesic) to follow the great-circle.
        /// 4. Height is not interpolated; inserted points receive Z=0 (caller may post-process heights if needed).
        /// 5. Degenerate consecutive vertices (same lon/lat within 1e-10) are skipped without subdivision.
        /// Accuracy / Limitations:
        /// - Assumes spherical Earth for densification; adequate for tile intersection / spatial filtering where
        ///   sub-meter ellipsoidal fidelity is unnecessary.
        /// - For very large distances (multi-thousand km) or polar wrapping, spherical vs. ellipsoidal differences grow.
        /// - Input list must contain at least one point; no validation of geographic bounds is performed here.
        /// </remarks>
        private static List<Point3d> DensifyGeodesic(List<Point3d> wgsPoints, double maxChordMeters)
        {
            var result = new List<Point3d>();

            for (int i = 0; i < wgsPoints.Count; i++)
            {
                var p1 = wgsPoints[i];
                var p2 = wgsPoints[(i + 1) % wgsPoints.Count];

                result.Add(p1);

                // Skip if same point
                if (Math.Abs(p1.X - p2.X) < 1e-10 && Math.Abs(p1.Y - p2.Y) < 1e-10) continue;

                // Calculate geodesic distance and interpolate if needed
                double distance = ApproximateDistance(p1.Y, p1.X, p2.Y, p2.X); // lat1,lon1,lat2,lon2
                int segments = Math.Max(1, (int)Math.Ceiling(distance / maxChordMeters));

                for (int j = 1; j < segments; j++)
                {
                    double t = (double)j / segments;
                    var interp = InterpolateGeodesic(p1.Y, p1.X, p2.Y, p2.X, t);
                    result.Add(new Point3d(interp.lon, interp.lat, 0)); // X=lon, Y=lat for Heron convention
                }
            }

            return result;
        }

        /// <summary>
        /// Computes an approximate great-circle distance between two geographic positions
        /// specified in decimal degrees using the Haversine formula on a spherical Earth model.
        /// </summary>
        /// <param name="lat1">Start point latitude in decimal degrees (−90 to +90).</param>
        /// <param name="lon1">Start point longitude in decimal degrees (−180 to +180).</param>
        /// <param name="lat2">End point latitude in decimal degrees (−90 to +90).</param>
        /// <param name="lon2">End point longitude in decimal degrees (−180 to +180).</param>
        /// <returns>
        /// Great-circle distance between the two positions in meters, assuming a spherical Earth
        /// radius of 6,371,000 m.
        /// </returns>
        /// <remarks>
        /// This method uses a fixed mean Earth radius and does not account for ellipsoidal
        /// flattening (WGS84). For higher accuracy over long distances or in precision-sensitive
        /// workflows, consider an ellipsoidal algorithm (e.g., Vincenty or Karney).
        /// Adequate for edge densification and approximate spatial filtering where sub-meter
        /// accuracy is not required.
        /// </remarks>
        private static double ApproximateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            // Haversine formula for great circle distance
            const double R = 6371000; // Earth radius in meters
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        /// <summary>
        /// Computes an intermediate geodetic position along the great-circle (approximate geodesic)
        /// between two latitude/longitude positions using spherical linear interpolation (slerp).
        /// </summary>
        /// <param name="lat1">Start point latitude in decimal degrees (−90 to +90).</param>
        /// <param name="lon1">Start point longitude in decimal degrees (−180 to +180).</param>
        /// <param name="lat2">End point latitude in decimal degrees (−90 to +90).</param>
        /// <param name="lon2">End point longitude in decimal degrees (−180 to +180).</param>
        /// <param name="t">
        /// Interpolation factor in [0,1]:
        /// 0 returns (lat1, lon1), 1 returns (lat2, lon2), values in between follow the great-circle arc.
        /// </param>
        /// <returns>
        /// A tuple (lat, lon) in decimal degrees representing the interpolated position.
        /// </returns>
        /// <remarks>
        /// This treats the Earth as a unit sphere for the interpolation step (sufficient for edge
        /// densification where sub-meter ellipsoidal accuracy is not critical). The shortest great-circle
        /// path is chosen. For very small angular separations a direct return of the start point is used
        /// to avoid numerical instability. Height is not interpolated here (caller assigns as needed).
        /// </remarks>
        private static (double lat, double lon) InterpolateGeodesic(double lat1, double lon1, double lat2, double lon2, double t)
        {
            // Spherical linear interpolation (slerp) on unit sphere
            double rlat1 = lat1 * Math.PI / 180, rlon1 = lon1 * Math.PI / 180;
            double rlat2 = lat2 * Math.PI / 180, rlon2 = lon2 * Math.PI / 180;

            // Convert to 3D unit vectors
            var p1 = new Vec3d(Math.Cos(rlat1) * Math.Cos(rlon1),
                Math.Cos(rlat1) * Math.Sin(rlon1),
                Math.Sin(rlat1));
            var p2 = new Vec3d(Math.Cos(rlat2) * Math.Cos(rlon2),
                Math.Cos(rlat2) * Math.Sin(rlon2),
                Math.Sin(rlat2));

            // Slerp
            double dot = Math.Max(-1, Math.Min(1, Vec3d.Dot(p1, p2)));
            double theta = Math.Acos(dot);

            if (Math.Abs(theta) < 1e-10) return (lat1, lon1); // Points too close

            double sinTheta = Math.Sin(theta);
            double w1 = Math.Sin((1 - t) * theta) / sinTheta;
            double w2 = Math.Sin(t * theta) / sinTheta;

            var result = new Vec3d(w1 * p1.X + w2 * p2.X, w1 * p1.Y + w2 * p2.Y, w1 * p1.Z + w2 * p2.Z);

            // Back to lat/lon
            double lat = Math.Asin(Math.Max(-1, Math.Min(1, result.Z)));
            double lon = Math.Atan2(result.Y, result.X);

            return (lat * 180 / Math.PI, lon * 180 / Math.PI);
        }

        /// <summary>
        /// Compute ECEF AABB and bounding sphere for a set of ECEF points
        /// </summary>
        public static void ComputeEcefBounds(IList<Point3d> ecefPoints, out Point3d min, out Point3d max,
            out Point3d center, out double radius)
        {
            min = new Point3d(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new Point3d(double.MinValue, double.MinValue, double.MinValue);
            var sum = Point3d.Origin;

            foreach (var p in ecefPoints)
            {
                if (p.X < min.X) min = new Point3d(p.X, min.Y, min.Z);
                if (p.Y < min.Y) min = new Point3d(min.X, p.Y, min.Z);
                if (p.Z < min.Z) min = new Point3d(min.X, min.Y, p.Z);
                if (p.X > max.X) max = new Point3d(p.X, max.Y, max.Z);
                if (p.Y > max.Y) max = new Point3d(max.X, p.Y, max.Z);
                if (p.Z > max.Z) max = new Point3d(max.X, max.Y, p.Z);
                sum = sum + p;
            }

            center = sum / Math.Max(1, ecefPoints.Count);
            radius = 0.0;
            foreach (var p in ecefPoints)
            {
                var dist = center.DistanceTo(p);
                if (dist > radius) radius = dist;
            }
        }

        /// <summary>
        /// Test if two ECEF AABBs are disjoint (don't overlap)
        /// </summary>
        public static bool IsAabbDisjoint(Point3d aMin, Point3d aMax, Point3d bMin, Point3d bMax)
        {
            return (aMax.X < bMin.X) || (bMax.X < aMin.X) ||
                   (aMax.Y < bMin.Y) || (bMax.Y < aMin.Y) ||
                   (aMax.Z < bMin.Z) || (bMax.Z < aMin.Z);
        }


        /// <summary>
        /// Converts Earth-Centered, Earth-Fixed (ECEF) Cartesian coordinates (meters) to
        /// geodetic WGS84 longitude (deg), latitude (deg) and ellipsoidal height (meters).
        /// </summary>
        /// <param name="ecef">
        /// Input point expressed in the global ECEF frame:
        /// X axis → intersection of equator and prime meridian,
        /// Y axis → 90° East along equator,
        /// Z axis → North pole.
        /// Units: meters.
        /// </param>
        /// <returns>
        /// A tuple (lonDeg, latDeg, h):
        ///   lonDeg: longitude in degrees in range [-180, 180)
        ///   latDeg: geodetic latitude in degrees (−90 to +90)
        ///   h: ellipsoidal height above the WGS84 reference ellipsoid in meters.
        /// </returns>
        /// <remarks>
        /// Implementation notes:
        /// 1. Longitude is obtained directly via atan2(y, x).
        /// 2. An initial latitude estimate is formed using a simplified relation that
        ///    ignores height (Bowring-style initial guess).
        /// 3. Latitude is then refined with a short fixed-count iteration (5 passes),
        ///    sufficient for centimeter-level accuracy for typical Earth-scale magnitudes.
        /// 4. Each iteration recomputes the prime vertical radius of curvature (N) and
        ///    updates the latitude using the relationship between geodetic and geocentric latitudes.
        /// 5. Final height is derived from the difference between the point’s distance
        ///    from the spin axis (p) and the ellipsoidal normal projection.
        /// Assumptions:
        ///   - Input coordinates are finite double values.
        ///   - WGS84 constants (a, e2) defined in this class.
        /// Accuracy:
        ///   - Typically better than ~1e-8 radians for latitude with the fixed 5 iterations.
        ///   - Height accuracy within millimeters to centimeters for normal terrestrial ranges.
        /// </remarks>
        public static (double lonDeg, double latDeg, double h) EcefToWgs84(Point3d ecef)
        {
            // Extract Cartesian components (meters)
            double x = ecef.X, y = ecef.Y, z = ecef.Z;

            // Longitude (radians). atan2 handles all quadrants; no iteration required.
            double lon = Math.Atan2(y, x);

            // Distance from Z axis (projection onto equatorial plane)
            double p = Math.Sqrt(x * x + y * y);

            // Initial geodetic latitude guess (radians) accounting for ellipsoidal flattening
            double lat = Math.Atan2(z, p * (1 - e2));

            double N; // Prime vertical radius of curvature

            // Iterate to refine latitude (fixed small count for performance & stability)
            for (int i = 0; i < 5; i++)
            {
                double sinLat = Math.Sin(lat);
                N = a / Math.Sqrt(1 - e2 * sinLat * sinLat);

                // Height estimate using current latitude
                double h = p / Math.Cos(lat) - N;

                // Update latitude using relation that accounts for ellipsoidal shape
                lat = Math.Atan2(z, p * (1 - e2 * (N / (N + h))));
            }

            // Recompute N with final latitude
            double sinFinalLat = Math.Sin(lat);
            N = a / Math.Sqrt(1 - e2 * sinFinalLat * sinFinalLat);

            // Final height above ellipsoid
            double hfinal = p / Math.Cos(lat) - N;

            // Convert radians → degrees for longitude & latitude
            return (RadToDeg(lon), RadToDeg(lat), hfinal);
        }


        /// <summary>
        /// Helper: convert WGS84 (lon°, lat°, h_m) to ECEF (X,Y,Z meters).
        /// </summary>
        public static Point3d Wgs84ToEcef(double lonDeg, double latDeg, double hMeters)
        {
            double lon = lonDeg * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;
            double sinLat = Math.Sin(lat);
            double cosLat = Math.Cos(lat);
            double sinLon = Math.Sin(lon);
            double cosLon = Math.Cos(lon);
            double N = a / Math.Sqrt(1 - e2 * sinLat * sinLat);
            double x = (N + hMeters) * cosLat * cosLon;
            double y = (N + hMeters) * cosLat * sinLon;
            double z = (N * (1 - e2) + hMeters) * sinLat;
            return new Point3d(x, y, z);
        }

    }
}
