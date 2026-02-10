using Grasshopper.Kernel;
using OSGeo.OGR;
using OSGeo.OSR;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render.ChangeQueue;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using og = OSGeo.OSR;

namespace Heron.Utilities.Google3DTiles
{
    public static partial class GeoUtils
    {
        public static OSGeo.OSR.CoordinateTransformation _ecefToLatLonHeightTransform = null;
        public static OSGeo.OSR.CoordinateTransformation _latLonHeightToEcefTransform = null;
        public static OSGeo.OSR.CoordinateTransformation _ecefToGeoidTransform = null;
        public static OSGeo.OSR.CoordinateTransformation _heronSrsToEcefTransform = null;
        public static OSGeo.OSR.CoordinateTransformation _geoidToHeronSrsTransform = null;
        public static OSGeo.OSR.SpatialReference _wgsEcefSrs = null;
        public static OSGeo.OSR.SpatialReference _wgsLatLonHeightSrs = null;
        public static OSGeo.OSR.SpatialReference _wgsGeoidSrs = null;
        public static OSGeo.OSR.SpatialReference _heronSrs = null;
        public static Transform _modelToHeronSrsTransform = new Transform();
        public static Transform _heronSrsToModelTransform = new Transform();

        public static bool SetSpatialReferences()
        {
            // Ensure GDAL is configured
            Heron.GdalConfiguration.ConfigureOgr();

            _wgsEcefSrs = new SpatialReference("");
            _wgsEcefSrs.ImportFromEPSG(4978); // WGS 84 ECEF
            _wgsLatLonHeightSrs = new SpatialReference("");
            _wgsLatLonHeightSrs.ImportFromEPSG(4979); // WGS 84 Geodetic (Lat/Lon/Height)

            /// Google 3D Tiles seem to use heights relative to mean sea level (geoid) rather than an ellipsoid
            /// Employ a custom PROJ string to use in the worldwide EGM2008 geoid height model from OSGeo as the vertical datum.
            /// The geoid gtx file is downloaded if not found in the target directory per the DownloadGeoidFile Target in Heron.csproj.
            /// This avoids the large gtx file from being checked into source control.
            /// Geoid source: https://github.com/OSGeo/proj-datumgrid/blob/master/world/egm08_25.gtx (142mb file)
            /// See also: https://www.unavco.org/software/geodetic-utilities/geoid-height-calculator/geoid-height-calculator.html for geoid vs ellipsoid

            var geoidgridsLocation = Path.Combine(HeronLocation.GetHeronFolder(), "gdal", "share", "egm08_25.gtx");
            _wgsGeoidSrs = new SpatialReference("");
            _wgsGeoidSrs.SetFromUserInput("+proj=longlat +datum=WGS84 +no_defs +geoidgrids=\"" + geoidgridsLocation + "\"");

            ///Set transform from input spatial reference to Heron spatial reference
            _heronSrs = new OSGeo.OSR.SpatialReference("");
            _heronSrs.SetFromUserInput(HeronSRS.Instance.SRS);

            ///Apply EAP to HeronSRS
            _modelToHeronSrsTransform = Heron.Convert.GetModelToUserSRSTransform(_heronSrs);
            _heronSrsToModelTransform = Heron.Convert.GetUserSRSToModelTransform(_heronSrs);

            SetTransformEcefToLatLonHeight();
            SetTransformLatLonHeightToEcef();
            SetTransformEcefToGeoid();
            SetTransformHeronSrsToEcef();
            SetTransformGeoidToHeronSrs();

            return true;

        }
        public static OSGeo.OSR.CoordinateTransformation SetTransformEcefToLatLonHeight()
        {
            _ecefToLatLonHeightTransform = new OSGeo.OSR.CoordinateTransformation(_wgsEcefSrs, _wgsLatLonHeightSrs);
            return _ecefToLatLonHeightTransform;
        }

        public static OSGeo.OSR.CoordinateTransformation SetTransformLatLonHeightToEcef()
        {
            _latLonHeightToEcefTransform = new OSGeo.OSR.CoordinateTransformation(_wgsLatLonHeightSrs, _wgsEcefSrs);
            return _latLonHeightToEcefTransform;
        }

        public static OSGeo.OSR.CoordinateTransformation SetTransformEcefToGeoid()
        {
            _ecefToGeoidTransform = new OSGeo.OSR.CoordinateTransformation(_wgsEcefSrs, _wgsGeoidSrs);
            return _ecefToGeoidTransform;
        }

        public static OSGeo.OSR.CoordinateTransformation SetTransformHeronSrsToEcef()
        {
            _heronSrsToEcefTransform = new OSGeo.OSR.CoordinateTransformation(_heronSrs, _wgsEcefSrs);
            return _heronSrsToEcefTransform;
        }

        public static OSGeo.OSR.CoordinateTransformation SetTransformGeoidToHeronSrs()
        {
            _geoidToHeronSrsTransform = new OSGeo.OSR.CoordinateTransformation(_wgsGeoidSrs, _heronSrs);
            return _geoidToHeronSrsTransform;
        }


        // Build min/max lon/lat via Heron for Info and Manifest
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

        public static List<Point3d> AoiToEcefGdal(Polyline aoiModel)
        {
            // Convert to ECEF once
            var ecefPoints = new List<Point3d>();
            foreach (var modelPoint in aoiModel)
            {
                try
                {
                    var ecef = ModelToEcefPoint(modelPoint); // lon, lat, h
                    ecefPoints.Add(ecef);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to convert WGS84 coordinates to ECEF at point lon={modelPoint.X}, lat={modelPoint.Y}, h={modelPoint.Z}.", ex);
                }
            }

            return ecefPoints;
        }

        /// <summary>
        /// Creates a rectangular polyline representing the axis-aligned bounding box of the specified boundary curve in Rhino coordinates.
        /// </summary>
        /// <remarks>The returned polyline is closed and lies in the same plane as the minimum Z value of
        /// the bounding box.</remarks>
        /// <param name="boundary">The curve for which to compute the bounding polyline. Must not be null.</param>
        /// <returns>A <see cref="Polyline"/> outlining the axis-aligned bounding box of the <paramref name="boundary"/>. 
        /// Returns <see langword="null"/> if the boundary's bounding box is invalid.</returns>
        public static Polyline GetAoiBoundingPolyline(Curve boundary)
        {
            BoundingBox bbox = boundary.GetBoundingBox(false);

            if (!bbox.IsValid)
                return null;

            Point3d[] corners = bbox.GetCorners();

            Point3d[] rectanglePoints = new Point3d[]
            {
                corners[0], // MinX, MinY, MinZ
                corners[1], // MaxX, MinY, MinZ
                corners[2], // MaxX, MaxY, MinZ
                corners[3], // MinX, MaxY, MinZ
                corners[0]  // Close the loop back to the start
            };

            Polyline boundingPolyline = new Polyline(rectanglePoints);
            return boundingPolyline;
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
        public static (double lonDeg, double latDeg, double h) EcefToWgs84Gdal(Point3d ecef)
        {
            //if (_ecefToLatLonHeightTransform == null) SetSpatialReferences();

            double[] point = new double[] { ecef.X, ecef.Y, ecef.Z };
            _ecefToLatLonHeightTransform.TransformPoint(point);

            // Note: GDAL typically returns coordinates in the order defined by the SRS authority
            // for EPSG:4979 this is Latitude, then Longitude, then Height.
            double lat = point[1]; // Latitude in degrees
            double lon = point[0]; // Longitude in degrees
            double height = point[2]; // Height in meters

            return (lon, lat, height);
        }

        /// <summary>
        /// Helper: convert WGS84 (lon°, lat°, h_m) to ECEF (X,Y,Z meters).
        /// </summary>
        public static Point3d Wgs84ToEcefGdal(double lonDeg, double latDeg, double hMeters)
        {
            //if (_latLonHeightToEcefTransform == null) SetSpatialReferences();

            double[] point = new double[] { lonDeg, latDeg, hMeters };
            _latLonHeightToEcefTransform.TransformPoint(point);
            // Note: GDAL typically expects coordinates in the order defined by the SRS authority
            // for EPSG:4979 this is Latitude, then Longitude, then Height.
            double x = point[0]; // X in meters
            double y = point[1]; // Y in meters
            double z = point[2]; // Z in meters

            return new Point3d(x, y, z);
        }

        public static Point3d EcefToModelPoint(Point3d ecef)
        {
            //if (_ecefToGeoidTransform == null || _geoidToHeronSrsTransform == null) SetSpatialReferences();

            double[] wgsPoint = new double[] { ecef.X, ecef.Y, ecef.Z };
            _ecefToGeoidTransform.TransformPoint(wgsPoint);
            _geoidToHeronSrsTransform.TransformPoint(wgsPoint);

            var modelPoint = new Point3d(wgsPoint[0], wgsPoint[1], wgsPoint[2]);
            modelPoint.Transform(_heronSrsToModelTransform);

            return modelPoint;
        }

        public static Point3d ModelToEcefPoint(Point3d modelPoint)
        {
            //if (_heronSrsToEcefTransform == null || _modelToHeronSrsTransform == null) SetSpatialReferences();

            var wgsPoint = new Point3d(modelPoint.X, modelPoint.Y, modelPoint.Z);
            wgsPoint.Transform(_modelToHeronSrsTransform);

            double[] ecefPoint = new double[] { wgsPoint.X, wgsPoint.Y, wgsPoint.Z };
            _heronSrsToEcefTransform.TransformPoint(ecefPoint);

            var ecef = new Point3d(ecefPoint[0], ecefPoint[1], ecefPoint[2]);

            return ecef;
        }


    }
}
