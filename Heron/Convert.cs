﻿using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using OSGeo.OGR;
using OSGeo.GDAL;
using OSGeo.OSR;


namespace Heron
{
    class Convert
    {
        private static RhinoDoc ActiveDoc => RhinoDoc.ActiveDoc;
        private static EarthAnchorPoint EarthAnchorPoint => ActiveDoc.EarthAnchorPoint;
        //////////////////////////////////////////////////////
        ///Basic Rhino transforms
        ///Using Rhino's EarthAnchorPoint to Transform.  GetModelToEarthTransform() translates to WGS84.
        ///https://github.com/gHowl/gHowlComponents/blob/master/gHowl/gHowl/GEO/XYZtoGeoComponent.cs
        ///https://github.com/mcneel/rhinocommon/blob/master/dotnet/opennurbs/opennurbs_3dm_settings.cs  search for "model_to_earth"
        ///
        ///This is the real sausce for Rhino projection method for decimal degress to xyz:
        ///https://github.com/mcneel/opennurbs/blob/e15c463638f10a74c3f503c1dab0c59eb68fb781/opennurbs_3dm_settings.cpp#L3354
        ///May be able to swap out the semimajor/semiminor axis radii used by default Rhino WGS84 to use other geographic coordinate systems
        ///

        public static Point3d XYZToWGS(Point3d xyz)
        {
            var point = new Point3d(xyz);
            point.Transform(XYZToWGSTransform());
            return point;
        }

        public static Transform XYZToWGSTransform()
        {
            return EarthAnchorPoint.GetModelToEarthTransform(ActiveDoc.ModelUnitSystem);
        }

        public static Point3d WGSToXYZ(Point3d wgs)
        {
            var transformedPoint = new Point3d(wgs);
            transformedPoint.Transform(WGSToXYZTransform());
            return transformedPoint;
        }

        public static Transform WGSToXYZTransform()
        {
            var XYZToWGS = XYZToWGSTransform();
            if (XYZToWGS.TryGetInverse(out Transform transform))
            {
                return transform;
            }
            return Transform.Unset;
        }

        public static Transform GetUserSRSToModelTransform(OSGeo.OSR.SpatialReference userSRS)
        {
            ///TODO: Check what units the userSRS is in and coordinate with the scaling function.  Currently only accounts for a userSRS in meters.
            ///TODO: translate or scale GCS (decimal degrees) to something like a Projectected Coordinate System.  Need to go dd to xy

            ///transform rhino EAP from rhinoSRS to userSRS
            double eapLat = EarthAnchorPoint.EarthBasepointLatitude;
            double eapLon = EarthAnchorPoint.EarthBasepointLongitude;
            double eapElev = EarthAnchorPoint.EarthBasepointElevation;
            Plane eapPlane = EarthAnchorPoint.GetEarthAnchorPlane(out Vector3d eapNorth);

            OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
            rhinoSRS.SetWellKnownGeogCS("WGS84");

            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(rhinoSRS, userSRS);
            //OSGeo.OGR.Geometry userAnchorPointDD = Heron.Convert.Point3dToOgrPoint(new Point3d(eapLon, eapLat, eapElev));
            OSGeo.OGR.Geometry userAnchorPointDD = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
            userAnchorPointDD.AddPoint(eapLon, eapLat, eapElev);
            Transform t = new Transform(1.0);

            userAnchorPointDD.Transform(coordTransform);

            Point3d userAnchorPointPT = Heron.Convert.OgrPointToPoint3d(userAnchorPointDD, t);

            ///setup userAnchorPoint plane for move and rotation
            double eapLatNorth = EarthAnchorPoint.EarthBasepointLatitude + 0.5;
            double eapLonEast = EarthAnchorPoint.EarthBasepointLongitude + 0.5;

            //OSGeo.OGR.Geometry userAnchorPointDDNorth = Heron.Convert.Point3dToOgrPoint(new Point3d(eapLon, eapLatNorth, eapElev));
            OSGeo.OGR.Geometry userAnchorPointDDNorth = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
            userAnchorPointDDNorth.AddPoint(eapLon, eapLatNorth, eapElev);

            //OSGeo.OGR.Geometry userAnchorPointDDEast = Heron.Convert.Point3dToOgrPoint(new Point3d(eapLonEast, eapLat, eapElev));
            OSGeo.OGR.Geometry userAnchorPointDDEast = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
            userAnchorPointDDEast.AddPoint(eapLonEast, eapLat, eapElev);

            userAnchorPointDDNorth.Transform(coordTransform);
            userAnchorPointDDEast.Transform(coordTransform);
            Point3d userAnchorPointPTNorth = Heron.Convert.OgrPointToPoint3d(userAnchorPointDDNorth, t);
            Point3d userAnchorPointPTEast = Heron.Convert.OgrPointToPoint3d(userAnchorPointDDEast, t);
            Vector3d userAnchorNorthVec = userAnchorPointPTNorth - userAnchorPointPT;
            Vector3d userAnchorEastVec = userAnchorPointPTEast - userAnchorPointPT;

            Plane userEapPlane = new Plane(userAnchorPointPT, userAnchorEastVec, userAnchorNorthVec);

            ///shift (move and rotate) from userSRS EAP to 0,0 based on SRS north direction
            Transform scale = Transform.Scale(new Point3d(0.0, 0.0, 0.0), (1 / Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters)));

            if (userSRS.GetLinearUnitsName().ToUpper().Contains("FEET") || userSRS.GetLinearUnitsName().ToUpper().Contains("FOOT"))
            {
                scale = Transform.Scale(new Point3d(0.0, 0.0, 0.0), (1 / Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Feet)));
            }

            ///if SRS is geographic (ie WGS84) use Rhino's internal projection
            ///this is still buggy as it doesn't work with other geographic systems like NAD27
            if ((userSRS.IsProjected() == 0) && (userSRS.IsLocal() == 0))
            {
                userEapPlane.Transform(WGSToXYZTransform());
                scale = WGSToXYZTransform();
            }

            Transform shift = Transform.ChangeBasis(eapPlane, userEapPlane);

            Transform shiftScale = Transform.Multiply(scale, shift);

            return shiftScale;
        }

        public static Transform GetModelToUserSRSTransform(OSGeo.OSR.SpatialReference userSRS)
        {
            var xyzToUserSRS = GetUserSRSToModelTransform(userSRS);
            if (xyzToUserSRS.TryGetInverse(out Transform transform))
            {
                return transform;
            }
            return Transform.Unset;
        }

        //////////////////////////////////////////////////////



        //////////////////////////////////////////////////////
        ///Converting GDAL geometry types to Rhino/GH geometry types
        ///Rhino transform for each method is required in order to scale points first, then mesh or make polyline or whatever.
        ///Without doing this, we get floating point distortions in the geometry, especially meshes.

        public static Point3d OgrPointToPoint3d(OSGeo.OGR.Geometry ogrPoint, Transform transform)
        {
            Point3d pt3d = new Point3d(ogrPoint.GetX(0), ogrPoint.GetY(0), ogrPoint.GetZ(0));
            pt3d.Transform(transform);

            return pt3d;
        }

        public static List<Point3d> OgrMultiPointToPoint3d(OSGeo.OGR.Geometry ogrMultiPoint, Transform transform)
        {
            List<Point3d> ptList = new List<Point3d>();
            OSGeo.OGR.Geometry sub_geom;
            for (int i = 0; i < ogrMultiPoint.GetGeometryCount(); i++)
            {
                sub_geom = ogrMultiPoint.GetGeometryRef(i);
                for (int ptnum = 0; ptnum < sub_geom.GetPointCount(); ptnum++)
                {
                    ptList.Add(Heron.Convert.OgrPointToPoint3d(sub_geom, transform));
                }
            }
            return ptList;

        }

        public static Curve OgrLinestringToCurve(OSGeo.OGR.Geometry linestring, Transform transform)
        {
            List<Point3d> ptList = new List<Point3d>();
            for (int i = 0; i < linestring.GetPointCount(); i++)
            {
                Point3d pt = new Point3d(linestring.GetX(i), linestring.GetY(i), linestring.GetZ(i));
                pt.Transform(transform);
                ptList.Add(pt);
            }
            Polyline pL = new Polyline(ptList);

            return pL.ToNurbsCurve();
        }

        public static Curve OgrRingToCurve(OSGeo.OGR.Geometry ring, Transform transform)
        {
            double tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            List<Point3d> ptList = new List<Point3d>();
            for (int i = 0; i < ring.GetPointCount(); i++)
            {
                Point3d pt = new Point3d(ring.GetX(i), ring.GetY(i), ring.GetZ(i));
                pt.Transform(transform);
                ptList.Add(pt);
            }
            //ptList.Add(ptList[0]);
            Polyline pL = new Polyline(ptList);
            Curve crv = pL.ToNurbsCurve();
            //crv.MakeClosed(tol);
            return crv;
        }

        public static List<Curve> OgrMultiLinestringToCurves(OSGeo.OGR.Geometry multilinestring, Transform transform)
        {
            List<Curve> cList = new List<Curve>();
            OSGeo.OGR.Geometry sub_geom;

            for (int i = 0; i < multilinestring.GetGeometryCount(); i++)
            {
                sub_geom = multilinestring.GetGeometryRef(i);
                cList.Add(Heron.Convert.OgrLinestringToCurve(sub_geom, transform));
                sub_geom.Dispose();
            }
            return cList;
        }

        public static Mesh OgrPolygonToMesh(OSGeo.OGR.Geometry polygon, Transform transform)
        {
            double tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            List<Curve> pList = new List<Curve>();
            OSGeo.OGR.Geometry sub_geom;

            for (int i = 0; i < polygon.GetGeometryCount(); i++)
            {
                sub_geom = polygon.GetGeometryRef(i);
                Curve crv = Heron.Convert.OgrRingToCurve(sub_geom, transform);
                //possible cause of viewport issue, try not forcing a close.  Other possibility would be trying to convert to (back to) polyline
                //crv.MakeClosed(tol);

                if (!crv.IsClosed && sub_geom.GetPointCount() > 2)
                {
                    Curve closingLine = new Line(crv.PointAtEnd, crv.PointAtStart).ToNurbsCurve();
                    Curve[] result = Curve.JoinCurves(new Curve[] { crv, closingLine });
                    crv = result[0];
                }

                pList.Add(crv);
                sub_geom.Dispose();
            }

            //need to catch if not closed polylines
            Mesh mPatch = new Mesh();

            if (pList[0] != null && pList[0].IsClosed)
            {
                Polyline pL = null;
                pList[0].TryGetPolyline(out pL);
                pList.RemoveAt(0);
                mPatch = Rhino.Geometry.Mesh.CreatePatch(pL, tol, null, pList, null, null, true, 1);

                ///Adds ngon capability
                ///https://discourse.mcneel.com/t/create-ngon-mesh-rhinocommon-c/51796/12
                mPatch.Ngons.AddPlanarNgons(tol);
                mPatch.FaceNormals.ComputeFaceNormals();
                mPatch.Normals.ComputeNormals();
                mPatch.Compact();
                mPatch.UnifyNormals();
            }

            return mPatch;
        }

        public static Extrusion OgrPolygonToExtrusion(OSGeo.OGR.Geometry polygon, Transform transform, double height, double min_height, bool underground)
        {
            List<Curve> pList = new List<Curve>();
            OSGeo.OGR.Geometry sub_geom;
            int direction = 1;
            if (underground) { direction = -1; }

            //pList = OgrMultiLinestringToCurves(polygon, transform);

            for (int i = 0; i < polygon.GetGeometryCount(); i++)
            {
                sub_geom = polygon.GetGeometryRef(i);
                Curve crv = Heron.Convert.OgrRingToCurve(sub_geom, transform);
                if (crv.ClosedCurveOrientation(Plane.WorldXY.ZAxis) == CurveOrientation.Clockwise) crv.Reverse();
                pList.Add(crv);
                sub_geom.Dispose();
            }

            pList[0].TryGetPlane(out var profilePlane);
            Transform profileTransform = Transform.PlaneToPlane(profilePlane, Plane.WorldXY);

            Extrusion extrusion = Extrusion.Create(pList[0], (height - min_height) * direction, true);

            if (extrusion == null) return null;

            if (pList.Count > 1)
            {
                pList.RemoveAt(0);
                foreach (Curve innerCurve in pList)
                {
                    Curve crv = innerCurve.DuplicateCurve();
                    crv.Transform(profileTransform);
                    extrusion.AddInnerProfile(crv);
                }
            }

            Vector3d moveDir = new Vector3d(0, 0, min_height);
            extrusion.Translate(moveDir);

            return extrusion;

        }

        public static Mesh OgrMultiPolyToMesh(OSGeo.OGR.Geometry multipoly, Transform transform)
        {
            double tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            OSGeo.OGR.Geometry sub_geom;
            List<Mesh> mList = new List<Mesh>();

            for (int i = 0; i < multipoly.GetGeometryCount(); i++)
            {
                sub_geom = multipoly.GetGeometryRef(i);
                Mesh mP = Heron.Convert.OgrPolygonToMesh(sub_geom, transform);
                mP.UnifyNormals();
                mList.Add(mP);
                sub_geom.Dispose();
            }
            Mesh m = new Mesh();
            m.Append(mList);

            //m.Ngons.AddPlanarNgons(tol);
            m.FaceNormals.ComputeFaceNormals();
            m.Normals.ComputeNormals();
            //m.RebuildNormals();
            m.Compact();
            m.UnifyNormals();


            if (m.DisjointMeshCount > 0)
            {
                Mesh[] mDis = m.SplitDisjointPieces();
                Mesh mm = new Mesh();
                foreach (Mesh mPiece in mDis)
                {
                    if (mPiece.SolidOrientation() < 0) mPiece.Flip(false, false, true);
                    mm.Append(mPiece);
                }
                //mm.Ngons.AddPlanarNgons(tol);
                mm.FaceNormals.ComputeFaceNormals();
                mm.Normals.ComputeNormals();
                //mm.RebuildNormals();
                mm.Compact();
                mm.UnifyNormals();

                return mm;

            }
            else
            {
                return m;
            }

        }

        public static List<Extrusion> OgrMultiPolyToExtrusions(OSGeo.OGR.Geometry multipoly, Transform transform, double height, double min_height, bool underground)
        {
            OSGeo.OGR.Geometry sub_geom;
            List<Extrusion> eList = new List<Extrusion>();

            for (int i = 0; i < multipoly.GetGeometryCount(); i++)
            {
                sub_geom = multipoly.GetGeometryRef(i);
                Extrusion mP = Heron.Convert.OgrPolygonToExtrusion(sub_geom, transform, height, min_height, underground);
                eList.Add(mP);
                sub_geom.Dispose();
            }

            return eList;
        }

        public static List<IGH_GeometricGoo> OgrGeomToGHGoo(OSGeo.OGR.Geometry geom, Transform transform)
        {
            List<IGH_GeometricGoo> gGoo = new List<IGH_GeometricGoo>();

            switch (geom.GetGeometryType())
            {
                case wkbGeometryType.wkbGeometryCollection:
                case wkbGeometryType.wkbGeometryCollection25D:
                case wkbGeometryType.wkbGeometryCollectionM:
                case wkbGeometryType.wkbGeometryCollectionZM:
                    OSGeo.OGR.Geometry sub_geom;
                    for (int gi = 0; gi < geom.GetGeometryCount(); gi++)
                    {
                        sub_geom = geom.GetGeometryRef(gi);
                        gGoo.AddRange(GetGoo(sub_geom, transform));
                        sub_geom.Dispose();
                    }
                    break;

                default:
                    gGoo = GetGoo(geom, transform);
                    break;
            }

            return gGoo;
        }

        public static List<IGH_GeometricGoo> GetGoo(OSGeo.OGR.Geometry geom, Transform transform)
        {
            List<IGH_GeometricGoo> gGoo = new List<IGH_GeometricGoo>();

            OSGeo.OGR.Geometry sub_geom;

            //find appropriate geometry type in feature and convert to Rhino geometry
            switch (geom.GetGeometryType())
            {
                case wkbGeometryType.wkbPoint25D:
                case wkbGeometryType.wkbPointM:
                case wkbGeometryType.wkbPointZM:
                case wkbGeometryType.wkbPoint:
                    gGoo.Add(new GH_Point(Heron.Convert.OgrPointToPoint3d(geom, transform)));
                    break;

                case wkbGeometryType.wkbMultiPoint25D:
                case wkbGeometryType.wkbMultiPointZM:
                case wkbGeometryType.wkbMultiPointM:
                case wkbGeometryType.wkbMultiPoint:
                    List<GH_Point> gH_Points = new List<GH_Point>();
                    foreach (Point3d p in Heron.Convert.OgrMultiPointToPoint3d(geom, transform)) gH_Points.Add(new GH_Point(p));
                    gGoo.AddRange(gH_Points);
                    break;

                case wkbGeometryType.wkbLinearRing:
                    gGoo.Add(new GH_Curve(Heron.Convert.OgrLinestringToCurve(geom, transform)));
                    break;

                case wkbGeometryType.wkbLineString25D:
                case wkbGeometryType.wkbLineStringM:
                case wkbGeometryType.wkbLineStringZM:
                case wkbGeometryType.wkbLineString:
                    gGoo.Add(new GH_Curve(Heron.Convert.OgrLinestringToCurve(geom, transform)));
                    break;

                case wkbGeometryType.wkbMultiLineString25D:
                case wkbGeometryType.wkbMultiLineStringZM:
                case wkbGeometryType.wkbMultiLineStringM:
                case wkbGeometryType.wkbMultiLineString:
                    List<GH_Curve> gH_Curves = new List<GH_Curve>();
                    foreach (Curve crv in Heron.Convert.OgrMultiLinestringToCurves(geom, transform)) gH_Curves.Add(new GH_Curve(crv));
                    gGoo.AddRange(gH_Curves);
                    break;

                case wkbGeometryType.wkbPolygonZM:
                case wkbGeometryType.wkbPolygonM:
                case wkbGeometryType.wkbPolygon25D:
                case wkbGeometryType.wkbPolygon:
                    gGoo.Add(new GH_Mesh(Heron.Convert.OgrPolygonToMesh(geom, transform)));
                    break;

                case wkbGeometryType.wkbMultiPolygonZM:
                case wkbGeometryType.wkbMultiPolygon25D:
                case wkbGeometryType.wkbMultiPolygonM:
                case wkbGeometryType.wkbMultiPolygon:
                case wkbGeometryType.wkbSurface:
                case wkbGeometryType.wkbSurfaceZ:
                case wkbGeometryType.wkbSurfaceZM:
                case wkbGeometryType.wkbSurfaceM:
                case wkbGeometryType.wkbPolyhedralSurface:
                case wkbGeometryType.wkbPolyhedralSurfaceM:
                case wkbGeometryType.wkbPolyhedralSurfaceZ:
                case wkbGeometryType.wkbPolyhedralSurfaceZM:
                case wkbGeometryType.wkbTINZ:
                case wkbGeometryType.wkbTINM:
                case wkbGeometryType.wkbTINZM:
                case wkbGeometryType.wkbTIN:
                    Mesh[] mDis = Heron.Convert.OgrMultiPolyToMesh(geom, transform).SplitDisjointPieces();
                    foreach (var mPiece in mDis)
                    {
                        gGoo.Add(new GH_Mesh(mPiece));
                    }
                    break;

                default:

                    ///If Feature is of an unrecognized geometry type
                    ///Loop through geometry points

                    for (int gpc = 0; gpc < geom.GetPointCount(); gpc++)
                    {
                        double[] ogrPt = new double[3];
                        geom.GetPoint(gpc, ogrPt);
                        Point3d pt3D = new Point3d(ogrPt[0], ogrPt[1], ogrPt[2]);
                        pt3D.Transform(transform);
                        gGoo.Add(new GH_Point(pt3D));
                    }


                    for (int gi = 0; gi < geom.GetGeometryCount(); gi++)
                    {
                        sub_geom = geom.GetGeometryRef(gi);
                        List<Point3d> geom_list = new List<Point3d>();

                        for (int ptnum = 0; ptnum < sub_geom.GetPointCount(); ptnum++)
                        {
                            double[] pT = new double[3];
                            pT[0] = sub_geom.GetX(ptnum);
                            pT[1] = sub_geom.GetY(ptnum);
                            pT[2] = sub_geom.GetZ(ptnum);

                            Point3d pt3D = new Point3d();
                            pt3D.X = pT[0];
                            pt3D.Y = pT[1];
                            pt3D.Z = pT[2];

                            pt3D.Transform(transform);
                            gGoo.Add(new GH_Point(pt3D));
                        }
                        sub_geom.Dispose();
                    }
                    break;

            }

            return gGoo;
        }
        //////////////////////////////////////////////////////


        //////////////////////////////////////////////////////
        ///TODO: Converting Rhino/GH geometry type to GDAL geometry types

        public static OSGeo.OGR.Geometry Point3dToOgrPoint(Point3d pt3d, Transform transform)
        {
            pt3d.Transform(transform);
            OSGeo.OGR.Geometry ogrPoint = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint25D);
            ogrPoint.AddPoint(pt3d.X, pt3d.Y, pt3d.Z);

            return ogrPoint;
        }

        public static OSGeo.OGR.Geometry Point3dsToOgrMultiPoint(List<Point3d> points, Transform transform)
        {
            OSGeo.OGR.Geometry ogrPoints = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiPoint25D);
            foreach (Point3d point in points)
            {
                ogrPoints.AddGeometry(Heron.Convert.Point3dToOgrPoint(point, transform));
            }

            return ogrPoints;
        }

        public static OSGeo.OGR.Geometry CurveToOgrLinestring(Curve curve, Transform transform)
        {
            Polyline pL = new Polyline();
            curve.TryGetPolyline(out pL);
            OSGeo.OGR.Geometry linestring = new OSGeo.OGR.Geometry(wkbGeometryType.wkbLineString25D);

            foreach (Point3d pt in pL)
            {
                pt.Transform(transform);
                linestring.AddPoint(pt.X, pt.Y, pt.Z);
            }

            return linestring;
        }

        public static OSGeo.OGR.Geometry CurvesToOgrMultiLinestring(List<Curve> curves, Transform transform)
        {
            OSGeo.OGR.Geometry multilinestring = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiLineString25D);

            foreach (Curve curve in curves)
            {
                Polyline pL = new Polyline();
                curve.TryGetPolyline(out pL);
                multilinestring.AddGeometry(Heron.Convert.CurveToOgrLinestring(curve, transform));
            }

            return multilinestring;
        }

        public static OSGeo.OGR.Geometry CurveToOgrRing(Curve curve, Transform transform)
        {
            Polyline pL = new Polyline();
            curve.TryGetPolyline(out pL);
            OSGeo.OGR.Geometry ring = new OSGeo.OGR.Geometry(wkbGeometryType.wkbLinearRing);

            if (pL[0] != pL[pL.Count-1])
            {
                pL.Add(pL[0]);
            }

            foreach (Point3d pt in pL)
            {
                pt.Transform(transform);
                ring.AddPoint(pt.X, pt.Y, pt.Z);
            }

            return ring;
        }

        public static OSGeo.OGR.Geometry CurveToOgrPolygon(Curve curve, Transform transform)
        {
            OSGeo.OGR.Geometry polygon = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPolygon25D);
            Polyline pL = new Polyline();
            curve.TryGetPolyline(out pL);
            polygon.AddGeometry(Heron.Convert.CurveToOgrRing(curve, transform));

            return polygon;
        }

        public static OSGeo.OGR.Geometry CurvesToOgrPolygon(List<Curve> curves, Transform transform)
        {
            OSGeo.OGR.Geometry polygon = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPolygon25D);
            foreach (Curve curve in curves)
            {
                Polyline pL = new Polyline();
                curve.TryGetPolyline(out pL);
                polygon.AddGeometry(Heron.Convert.CurveToOgrRing(curve, transform));
            }

            return polygon;
        }

        public static OSGeo.OGR.Geometry MeshToMultiPolygon(Mesh mesh, Transform transform)
        {
            //OSGeo.OGR.Geometry ogrMultiPolygon = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiPolygon25D);
            OSGeo.OGR.Geometry ogrMultiPolygon = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiSurfaceZ);


            foreach (var face in mesh.GetNgonAndFacesEnumerable())
            {
                Polyline pL = new Polyline();
                foreach (var index in face.BoundaryVertexIndexList())
                {
                    pL.Add(mesh.Vertices.Point3dAt(System.Convert.ToInt32(index)));
                }

                ogrMultiPolygon.AddGeometry(Heron.Convert.CurveToOgrPolygon(pL.ToNurbsCurve(), transform));
            }
            return ogrMultiPolygon;
        }

        public static OSGeo.OGR.Geometry MeshesToMultiPolygon(List<Mesh> meshes, Transform transform)
        {
            //OSGeo.OGR.Geometry ogrMultiPolygon = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiPolygon25D);
            OSGeo.OGR.Geometry ogrMultiPolygon = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiSurfaceZ);

            Mesh m = new Mesh();
            foreach (var mesh in meshes)
            {
                m.Append(mesh);
                //ogrMultiPolygon.AddGeometry(Heron.Convert.MeshToMultiPolygon(mesh, transform));
            }
            ogrMultiPolygon.AddGeometry(Heron.Convert.MeshToMultiPolygon(m, transform));

            return ogrMultiPolygon;
        }


        //////////////////////////////////////////////////////




        public static string HttpToJson(string URL)
        {
            //need to update for https issues
            //GH forum issue: https://www.grasshopper3d.com/group/heron/forum/topics/discover-rest-data-sources?commentId=2985220%3AComment%3A1945956&xg_source=msg_com_gr_forum
            //solution found here: https://stackoverflow.com/questions/2859790/the-request-was-aborted-could-not-create-ssl-tls-secure-channel
            //add these two lines of code
            System.Net.ServicePointManager.Expect100Continue = true;
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

            //get json from rest service
            System.Net.HttpWebRequest req = System.Net.WebRequest.Create(URL) as System.Net.HttpWebRequest;
            string result = null;

            using (System.Net.HttpWebResponse resp = req.GetResponse() as System.Net.HttpWebResponse)
            {
                System.IO.StreamReader reader = new System.IO.StreamReader(resp.GetResponseStream());
                result = reader.ReadToEnd();
                reader.Close();
            }

            return result;
        }

        ////////////////////////////////////////
        ///For use with slippy maps
        ///from Download Slippy Tiles https://gist.github.com/devdattaT/dd218d1ecdf6100bcf15
        ///also https://wiki.openstreetmap.org/wiki/Slippy_map_tilenames#C.23


        //download all tiles within bbox
        public static List<int> DegToNum(double lat_deg, double lon_deg, int zoom)
        {
            double lat_rad = Rhino.RhinoMath.ToRadians(lat_deg);
            int n = (1 << zoom);
            int xtile = (int)Math.Floor((lon_deg + 180.0) / 360 * n);
            int ytile = (int)Math.Floor((1 - Math.Log(Math.Tan(lat_rad) + (1 / Math.Cos(lat_rad))) / Math.PI) / 2.0 * n);
            return new List<int> { xtile, ytile };
        }


        public static List<double> DegToNumPixel(double lat_deg, double lon_deg, int zoom)
        {
            double lat_rad = Rhino.RhinoMath.ToRadians(lat_deg);
            int n = (1 << zoom); //2^zoom
            double xtile = (lon_deg + 180.0) / 360 * n;
            double ytile = (1 - Math.Log(Math.Tan(lat_rad) + (1 / Math.Cos(lat_rad))) / Math.PI) / 2.0 * n;
            return new List<double> { xtile, ytile };
        }

        public static List<double> NumToDeg(int xtile, int ytile, int zoom)
        {
            double n = Math.Pow(2, zoom);
            double lon_deg = xtile / n * 360 - 180;
            double lat_rad = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * ytile / n)));
            double lat_deg = Rhino.RhinoMath.ToDegrees(lat_rad);
            return new List<double> { lat_deg, lon_deg };
        }

        public static List<double> NumToDegPixel(double xtile, double ytile, int zoom)
        {
            double n = Math.Pow(2, zoom);
            double lon_deg = xtile / n * 360 - 180;
            double lat_rad = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * ytile / n)));
            double lat_deg = Rhino.RhinoMath.ToDegrees(lat_rad);
            return new List<double> { lat_deg, lon_deg };
        }

        //get the range of tiles that intersect with the bounding box of the polygon
        public static (Interval XRange, Interval YRange) GetTileRange(BoundingBox bnds, int zoom)
        {
            Point3d bndsMin = Convert.XYZToWGS(bnds.Min);
            Point3d bndsMax = Convert.XYZToWGS(bnds.Max);
            double xm = bndsMin.X;
            double xmx = bndsMax.X;
            double ym = bndsMin.Y;
            double ymx = bndsMax.Y;
            List<int> starting = Convert.DegToNum(ymx, xm, zoom);
            List<int> ending = Convert.DegToNum(ym, xmx, zoom);
            var x_range = new Interval(starting[0], ending[0]);
            var y_range = new Interval(starting[1], ending[1]);
            return (x_range, y_range);
        }

        //get the tile as a polyline object
        public static Polyline GetTileAsPolygon(int z, int y, int x)
        {
            List<double> nw = Convert.NumToDeg(x, y, z);
            List<double> se = Convert.NumToDeg(x + 1, y + 1, z);
            double xm = nw[1];
            double xmx = se[1];
            double ym = se[0];
            double ymx = nw[0];
            Polyline tile_bound = new Polyline();
            tile_bound.Add(Convert.WGSToXYZ(new Point3d(xm, ym, 0)));
            tile_bound.Add(Convert.WGSToXYZ(new Point3d(xmx, ym, 0)));
            tile_bound.Add(Convert.WGSToXYZ(new Point3d(xmx, ymx, 0)));
            tile_bound.Add(Convert.WGSToXYZ(new Point3d(xm, ymx, 0)));
            tile_bound.Add(Convert.WGSToXYZ(new Point3d(xm, ym, 0)));
            return tile_bound;
        }

        //tell if the tile intersects with the given polyline
        public static bool DoesTileIntersect(int z, int y, int x, Curve polygon)
        {
            if (z < 10)
            {
                return true;
            }
            else
            {
                //get the four corners
                Polyline tile = GetTileAsPolygon(x, y, z);
                return (Rhino.Geometry.Intersect.Intersection.CurveCurve(polygon, tile.ToNurbsCurve(), Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance).Count > 0);
            }
        }

        //convert the generic URL to get the specific URL of the tile
        public static string GetZoomURL(int x, int y, int z, string url)
        {
            string u = url.Replace("{x}", x.ToString());
            u = u.Replace("{y}", y.ToString());
            u = u.Replace("{z}", z.ToString());
            return u;
        }

        public static string GetOSMURL(int timeout, string searchTerm, string left, string bottom, string right, string top, string url)
        {
            string search = "(node" + searchTerm + "; way" + searchTerm + "; relation" + searchTerm + ";);(._;>;);";
            string u = url.Replace("{timeout}", timeout.ToString());
            if (searchTerm.Length > 0)
            {
                u = u.Replace("{searchTerm}", search);
            }
            else { u = u.Replace("{searchTerm}", "(node;way;relation;);(._;>;);"); }
            u = u.Replace("{left}", left);
            u = u.Replace("{bottom}", bottom);
            u = u.Replace("{right}", right);
            u = u.Replace("{top}", top);
            return u;
        }

        ///Get list of mapping service Endpoints
        public static string GetEnpoints()
        {
            string jsonString = string.Empty;
            using (System.Net.WebClient wc = new System.Net.WebClient())
            {
                string URI = "https://raw.githubusercontent.com/blueherongis/Heron/master/HeronServiceEndpoints.json";
                jsonString = wc.DownloadString(URI);
            }

            return jsonString;
        }

        ///Check if cached images exist in cache folder
        public static bool CheckCacheImagesExist(List<string> filePaths)
        {
            foreach (string filePath in filePaths)
            {
                if (!File.Exists(filePath))
                    return false;
            }
            return true;
        }

        //////////////////////////////////////////////////////

    }

    public static class BitmapExtension
    {
        public static void AddCommentsToJPG(this Bitmap bitmap, string comment)
        {
            //add tile range meta data to image comments
            //doesn't work for png, need to find a common ID between jpg and png
            //https://stackoverflow.com/questions/18820525/how-to-get-and-set-propertyitems-for-an-image/25162782#25162782
            var newItem = (System.Drawing.Imaging.PropertyItem)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(System.Drawing.Imaging.PropertyItem));
            newItem.Id = 40092;
            newItem.Type = 1;
            newItem.Value = Encoding.Unicode.GetBytes(comment);
            newItem.Len = newItem.Value.Length;
            bitmap.SetPropertyItem(newItem);
        }

        public static string GetCommentsFromJPG(this Bitmap bitmap)
        {
            //doesn't work for png
            System.Drawing.Imaging.PropertyItem prop = bitmap.GetPropertyItem(40092);
            string comment = Encoding.Unicode.GetString(prop.Value);
            return comment;
        }

        public static void AddCommentsToPNG(this Bitmap bitmap, string comment)
        {
            //add tile range meta data to image comments
            //ID:40094 doesn't seem to work for png and 40092 only works for JPG
            //https://stackoverflow.com/questions/18820525/how-to-get-and-set-propertyitems-for-an-image/25162782#25162782
            var newItem = (System.Drawing.Imaging.PropertyItem)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(System.Drawing.Imaging.PropertyItem));
            newItem.Id = 40094;
            newItem.Type = 1;
            newItem.Value = Encoding.Unicode.GetBytes(comment);
            newItem.Len = newItem.Value.Length;
            bitmap.SetPropertyItem(newItem);
        }

        public static string GetCommentsFromPNG(this Bitmap bitmap)
        {
            //doesn't work for png
            System.Drawing.Imaging.PropertyItem prop = bitmap.GetPropertyItem(40094);
            string comment = Encoding.Unicode.GetString(prop.Value);
            return comment;
        }
    }

}
