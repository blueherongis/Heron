using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Heron.Components.Heron3DTiles
{
    public static class GeoUtils
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
    }
}
