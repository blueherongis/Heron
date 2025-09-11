using og = OSGeo.OSR;
using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Heron.Components.Heron3DTiles
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

        private static (double lat, double lon) InterpolateGeodesic(double lat1, double lon1, double lat2, double lon2,
            double t)
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
        /// Approximate AOI rectangle to WGS84 lon/lat AABB by sampling the poly corners.
        /// </summary>
        public static (double minLon, double minLat, double maxLon, double maxLat) ApproximateBoundaryToWgs84(
            Polyline aoiModel, Transform modelToEarth)
        {
            double minLon = double.PositiveInfinity, minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity, maxLat = double.NegativeInfinity;

            foreach (var pt in aoiModel)
            {
                var ecef = modelToEarth * pt; // now in Earth-centered frame
                // Convert ECEF to WGS84 (lon/lat in degrees) – quick solver
                var (lon, lat, _) = EcefToWgs84(ecef);
                minLon = Math.Min(minLon, lon);
                minLat = Math.Min(minLat, lat);
                maxLon = Math.Max(maxLon, lon);
                maxLat = Math.Max(maxLat, lat);
            }

            return (minLon, minLat, maxLon, maxLat);
        }

        // WGS84 constants
        const double a = 6378137.0; // semi-major
        const double f = 1.0 / 298.257223563;
        const double b = a * (1 - f); // semi-minor
        const double e2 = 1 - (b * b) / (a * a);

        public static (double lonDeg, double latDeg, double h) EcefToWgs84(Point3d ecef)
        {
            // Iterative solution
            double x = ecef.X, y = ecef.Y, z = ecef.Z;
            double lon = Math.Atan2(y, x);
            double p = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, p * (1 - e2));
            double N;

            for (int i = 0; i < 5; i++)
            {
                N = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
                double h = p / Math.Cos(lat) - N;
                lat = Math.Atan2(z, p * (1 - e2 * (N / (N + h))));
            }

            N = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
            double hfinal = p / Math.Cos(lat) - N;

            return (RadToDeg(lon), RadToDeg(lat), hfinal);
        }

        public static double RadToDeg(double r) => r * (180.0 / Math.PI);


        /// <summary>
        /// (A) Compute ECEF AABB using Rhino's model->ECEF transform (EarthAnchorPoint).
        /// Assumes EarthAnchorPoint.GetModelToEarthTransform returns WGS84 ECEF meters.
        /// </summary>
        public static (Point3d min, Point3d max) ModelPolylineToEcefAabb_Rhino(Polyline pl)
        {
            var activeDoc = Rhino.RhinoDoc.ActiveDoc;
            if (activeDoc?.EarthAnchorPoint == null)
            {
                throw new InvalidOperationException("Active document or EarthAnchorPoint unavailable.");
            }

            Transform? t;
            try
            {
                t = activeDoc.EarthAnchorPoint.GetModelToEarthTransform(activeDoc.ModelUnitSystem);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to get model to earth transform. This may indicate EarthAnchorPoint is not properly initialized.", ex);
            }

            if (t == null) 
            {
                throw new InvalidOperationException("Failed to get model to earth transform - transform is null.");
            }

            bool first = true;
            Point3d min = Point3d.Unset, max = Point3d.Unset;

            foreach (var p in pl)
            {
                var ecef = p;
                ecef.Transform(t.Value);
                if (first)
                {
                    min = ecef;
                    max = ecef;
                    first = false;
                }
                else
                {
                    if (ecef.X < min.X) min.X = ecef.X;
                    if (ecef.Y < min.Y) min.Y = ecef.Y;
                    if (ecef.Z < min.Z) min.Z = ecef.Z;
                    if (ecef.X > max.X) max.X = ecef.X;
                    if (ecef.Y > max.Y) max.Y = ecef.Y;
                    if (ecef.Z > max.Z) max.Z = ecef.Z;
                }
            }

            return (min, max);
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

        /// <summary>
        /// (B) Compute ECEF AABB using GDAL: model -> WGS84 degrees -> ECEF (EPSG:4978).
        /// Uses OGR.Geometry.Transform for maximum compatibility with older GDAL C# bindings.
        /// </summary>
        public static (Point3d min, Point3d max) ModelPolylineToEcefAabb_Gdal(Polyline pl)
        {
            var srs4326 = new og.SpatialReference("");
            srs4326.ImportFromEPSG(4326); // WGS84 Geographic (lon,lat,h)
            var srs4978 = new og.SpatialReference("");
            srs4978.ImportFromEPSG(4978); // WGS84 Geocentric (ECEF)

            var ct = new OSGeo.OSR.CoordinateTransformation(srs4326, srs4978);

            var activeDoc = Rhino.RhinoDoc.ActiveDoc;
            if (activeDoc == null)
            {
                throw new InvalidOperationException("No active Rhino document found.");
            }

            double unitScaleToMeters = Rhino.RhinoMath.UnitScale(
                activeDoc.ModelUnitSystem,
                Rhino.UnitSystem.Meters);

            bool first = true;
            Point3d min = Point3d.Unset, max = Point3d.Unset;

            // Reuse a single geometry instance for efficiency
            var ogrPoint = new OSGeo.OGR.Geometry(OSGeo.OGR.wkbGeometryType.wkbPoint25D);
            ogrPoint.AssignSpatialReference(srs4326);

            foreach (var p in pl)
            {
                Point3d w;
                try
                {
                    w = Heron.Convert.XYZToWGS(p); // w.X = lon°, w.Y = lat°, w.Z = elevation (model units)
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Failed to convert model coordinates to WGS84. Check EarthAnchorPoint configuration.", ex);
                }
                
                double hMeters = w.Z * unitScaleToMeters;

                // Reset point coordinates (faster than creating a new geometry every loop)
                ogrPoint.Empty();
                ogrPoint.AddPoint(w.X, w.Y, hMeters);

                // Transform to ECEF (EPSG:4978)
                ogrPoint.Transform(ct);

                var ecef = new Point3d(ogrPoint.GetX(0), ogrPoint.GetY(0), ogrPoint.GetZ(0));

                if (first)
                {
                    min = ecef;
                    max = ecef;
                    first = false;
                }
                else
                {
                    if (ecef.X < min.X) min.X = ecef.X;
                    if (ecef.Y < min.Y) min.Y = ecef.Y;
                    if (ecef.Z < min.Z) min.Z = ecef.Z;
                    if (ecef.X > max.X) max.X = ecef.X;
                    if (ecef.Y > max.Y) max.Y = ecef.Y;
                    if (ecef.Z > max.Z) max.Z = ecef.Z;
                }
            }

            ogrPoint.Dispose();
            return (min, max);
        }

        /// <summary>
        /// Optional: derive a bounding sphere from an ECEF AABB (useful for 3D Tiles).
        /// center = midpoint of box; radius = max distance corner->center.
        /// </summary>
        public static (Point3d center, double radius) AabbToBoundingSphere(Point3d min, Point3d max)
        {
            var center = new Point3d(
                0.5 * (min.X + max.X),
                0.5 * (min.Y + max.Y),
                0.5 * (min.Z + max.Z));

            double r2 = 0.0;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Point3d(
                    (i & 1) == 0 ? min.X : max.X,
                    (i & 2) == 0 ? min.Y : max.Y,
                    (i & 4) == 0 ? min.Z : max.Z);
                double d2 = center.DistanceToSquared(corner);
                if (d2 > r2) r2 = d2;
            }

            return (center, Math.Sqrt(r2));
        }

        /// <summary>
        /// Test if a polygon intersects a rectangle centered at origin with half-widths hx, hy
        /// </summary>
        public static bool PolygonIntersectsRectangle(IList<Point2d> polygon, double hx, double hy)
        {
            if (polygon == null || polygon.Count == 0) return false;

            // Quick test: any polygon vertex inside rectangle?
            foreach (var pt in polygon)
            {
                if (Math.Abs(pt.X) <= hx && Math.Abs(pt.Y) <= hy) return true;
            }

            // Test: any rectangle corner inside polygon?
            var corners = new Point2d[]
            {
                new Point2d(-hx, -hy), new Point2d(hx, -hy),
                new Point2d(hx, hy), new Point2d(-hx, hy)
            };

            foreach (var corner in corners)
            {
                if (PointInPolygon(corner, polygon)) return true;
            }

            // Test: any edge intersections?
            return HasEdgeIntersections(polygon, corners);
        }

        private static bool PointInPolygon(Point2d point, IList<Point2d> polygon)
        {
            // Ray casting algorithm
            bool inside = false;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                if (((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                    (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static bool HasEdgeIntersections(IList<Point2d> polyA, Point2d[] polyB)
        {
            // Test all edge pairs for intersection
            int nA = polyA.Count, nB = polyB.Length;
            for (int i = 0; i < nA; i++)
            {
                var a1 = polyA[i];
                var a2 = polyA[(i + 1) % nA];

                for (int j = 0; j < nB; j++)
                {
                    var b1 = polyB[j];
                    var b2 = polyB[(j + 1) % nB];

                    if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                }
            }

            return false;
        }

        private static bool SegmentsIntersect(Point2d a1, Point2d a2, Point2d b1, Point2d b2)
        {
            var da = a2 - a1;
            var db = b2 - b1;
            var dc = b1 - a1;

            double denom = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(denom) < 1e-10) return false; // Parallel

            double t = (dc.X * db.Y - dc.Y * db.X) / denom;
            double u = (dc.X * da.Y - dc.Y * da.X) / denom;

            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }
    }
}
