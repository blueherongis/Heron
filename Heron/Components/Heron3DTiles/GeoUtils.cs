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
    public static partial class GeoUtils
    {

        // Build min/max lon/lat via Heron
        public static (double minLon, double minLat, double maxLon, double maxLat) AoiToWgs(Rhino.Geometry.Polyline pl)
        {
            double minLon = double.PositiveInfinity, minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity, maxLat = double.NegativeInfinity;

            foreach (var p in pl)
            {
                var w = Heron.Convert.XYZToWGS(p);      // Heron EAP → WGS84
                minLon = Math.Min(minLon, w.X); maxLon = Math.Max(maxLon, w.X);
                minLat = Math.Min(minLat, w.Y); maxLat = Math.Max(maxLat, w.Y);
            }
            return (minLon, minLat, maxLon, maxLat);
        }


        /// <summary>
        /// Approximate AOI rectangle to WGS84 lon/lat AABB by sampling the poly corners.
        /// </summary>
        public static (double minLon, double minLat, double maxLon, double maxLat) ApproximateBoundaryToWgs84(Polyline aoiModel, Transform modelToEarth)
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
        const double a = 6378137.0;              // semi-major
        const double f = 1.0 / 298.257223563;
        const double b = a * (1 - f);           // semi-minor
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
            var t = Rhino.RhinoDoc.ActiveDoc?.EarthAnchorPoint.GetModelToEarthTransform(
                Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem);
            if (t == null) throw new InvalidOperationException("Active document or EarthAnchorPoint unavailable.");

            bool first = true;
            Point3d min = Point3d.Unset, max = Point3d.Unset;

            foreach (var p in pl)
            {
                var ecef = p;
                ecef.Transform(t);
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
            srs4326.ImportFromEPSG(4326);   // WGS84 Geographic (lon,lat,h)
            var srs4978 = new og.SpatialReference("");
            srs4978.ImportFromEPSG(4978);   // WGS84 Geocentric (ECEF)

            var ct = new OSGeo.OSR.CoordinateTransformation(srs4326, srs4978);

            double unitScaleToMeters = Rhino.RhinoMath.UnitScale(
                Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem,
                Rhino.UnitSystem.Meters);

            bool first = true;
            Point3d min = Point3d.Unset, max = Point3d.Unset;

            // Reuse a single geometry instance for efficiency
            var ogrPoint = new OSGeo.OGR.Geometry(OSGeo.OGR.wkbGeometryType.wkbPoint25D);
            ogrPoint.AssignSpatialReference(srs4326);

            foreach (var p in pl)
            {
                var w = Heron.Convert.XYZToWGS(p);   // w.X = lon°, w.Y = lat°, w.Z = elevation (model units)
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
    }
}
