using System;
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


using Rhino.Geometry;
using Rhino.DocObjects;
using OSGeo.OGR;

namespace Heron
{
    class Convert
    {

        //////////////////////////////////////////////////////
        ///Basic Rhino transforms
        ///Using Rhino's EarthAnchorPoint to Transform.  GetModelToEarthTransform() translates to WGS84.
        ///https://github.com/gHowl/gHowlComponents/blob/master/gHowl/gHowl/GEO/XYZtoGeoComponent.cs
        ///https://github.com/mcneel/rhinocommon/blob/master/dotnet/opennurbs/opennurbs_3dm_settings.cs  search for "model_to_earth"

        public static Point3d ToWGS(Point3d xyz)
        {
            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);

            xyz = xyz * Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);
            Point3d ptON = new Point3d(xyz.X, xyz.Y, xyz.Z);
            ptON = xf * ptON;

            ///TODO: Make translation of ptON here using SetCRS global variable (WGS84 -> CRS)

            return ptON;
        }

        public static Transform ToWGSxf()
        {
            EarthAnchorPoint eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);

            ///scale the transform to the model units
            Transform xfScaled = Transform.Multiply(xf, Transform.Scale(new Point3d(0.0, 0.0, 0.0), Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters)));

            return xfScaled;
        }

        public static Point3d ToXYZ(Point3d wgs)
        {

            ///TODO: make translation of wgs here using SetCRS (CRS -> WGS84)

            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);

            //http://www.grasshopper3d.com/forum/topics/matrix-datatype-in-rhinocommon
            //Thanks Andrew
            Transform Inversexf = new Transform();
            xf.TryGetInverse(out Inversexf);

            wgs = Inversexf * wgs / Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);
            return wgs;
        }

        public static Transform ToXYZxf()
        {
            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);

            //scale the transform to the model units
            Transform xfScaled = Transform.Multiply(xf, Transform.Scale(new Point3d(0.0, 0.0, 0.0), Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters)));

            //http://www.grasshopper3d.com/forum/topics/matrix-datatype-in-rhinocommon
            //Thanks Andrew
            Transform Inversexf = new Transform();
            xfScaled.TryGetInverse(out Inversexf);
            return Inversexf;
        }
        //////////////////////////////////////////////////////



        //////////////////////////////////////////////////////
        ///Old transforms used in RESTRaster and RESTVector
        ///These conversions should be replaced with ones that are GDAL based and more flexible with a global SetCRS parameter
        ///Conversion from WSG84 to Google/Bing from
        ///http://alastaira.wordpress.com/2011/01/23/the-google-maps-bing-maps-spherical-mercator-projection/

        public static double ConvertLon(double lon, int spatRef)
        {
            double clon = lon;
            if (spatRef == 3857)
            {
                double y = Math.Log(Math.Tan((90 + lon) * Math.PI / 360)) / (Math.PI / 180);
                y = y * 20037508.34 / 180;
                clon = y;
            }
            return clon;
        }

        public static double ConvertLat(double lat, int spatRef)
        {
            double clat = lat;
            if (spatRef == 3857)
            {
                double x = lat * 20037508.34 / 180;
                clat = x;
            }
            return clat;
        }

        public static Point3d ConvertXY(double x, double y, int spatRef)
        {
            double lon = x;
            double lat = y;

            if (spatRef == 3857)
            {
                lon = (x / 20037508.34) * 180;
                lat = (y / 20037508.34) * 180;
                lat = 180 / Math.PI * (2 * Math.Atan(Math.Exp(lat * Math.PI / 180)) - Math.PI / 2);
            }

            Point3d coord = new Point3d();
            coord.X = lon;
            coord.Y = lat;

            return Heron.Convert.ToXYZ(coord);
        }

        //////////////////////////////////////////////////////


        //////////////////////////////////////////////////////
        ///Converting GDAL geometry types to Rhino/GH geometry types

        public static Point3d OgrPointToPoint3d(OSGeo.OGR.Geometry ogrPoint)
        {
            Point3d pt3d = new Point3d(ogrPoint.GetX(0), ogrPoint.GetY(0), ogrPoint.GetZ(0));
            return Convert.ToXYZ(pt3d);
        }

        public static List<Point3d> OgrMultiPointToPoint3d(OSGeo.OGR.Geometry ogrMultiPoint)
        {
            List<Point3d> ptList = new List<Point3d>();
            OSGeo.OGR.Geometry sub_geom;
            for (int i = 0; i < ogrMultiPoint.GetGeometryCount(); i++)
            {
                sub_geom = ogrMultiPoint.GetGeometryRef(i);
                for (int ptnum = 0; ptnum < sub_geom.GetPointCount(); ptnum++)
                {
                    ptList.Add(Convert.ToXYZ(new Point3d(sub_geom.GetX(0), sub_geom.GetY(0), sub_geom.GetZ(0))));
                }
            }
            return ptList;

        }

        public static Curve OgrLinestringToCurve(OSGeo.OGR.Geometry linestring)
        {
            List<Point3d> ptList = new List<Point3d>();
            for (int i = 0; i < linestring.GetPointCount(); i++)
            {
                ptList.Add(Convert.ToXYZ(new Point3d(linestring.GetX(i), linestring.GetY(i), linestring.GetZ(i))));
            }
            Polyline pL = new Polyline(ptList);

            return new Polyline(ptList).ToNurbsCurve();
        }

        public static List<Curve> OgrMultiLinestring(OSGeo.OGR.Geometry multilinestring)
        {
            List<Curve> cList = new List<Curve>();
            OSGeo.OGR.Geometry sub_geom;

            for (int i = 0; i < multilinestring.GetGeometryCount(); i++)
            {
                sub_geom = multilinestring.GetGeometryRef(i);
                cList.Add(Convert.OgrLinestringToCurve(sub_geom));
                sub_geom.Dispose();
            }
            return cList;
        }


        public static Mesh OgrPolygonToMesh(OSGeo.OGR.Geometry polygon)
        {
            double tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            List<Curve> pList = new List<Curve>();
            OSGeo.OGR.Geometry sub_geom;

            for (int i = 0; i < polygon.GetGeometryCount(); i++)
            {
                sub_geom = polygon.GetGeometryRef(i);
                Curve crv = Convert.OgrLinestringToCurve(sub_geom);
                //possible cause of viewport issue, try not forcing a close.  Other possibility would be trying to convert to (back to) polyline
                //crv.MakeClosed(tol);
                pList.Add(crv);
                sub_geom.Dispose();
            }

            //need to catch if not closed polylines
            Mesh mPatch = new Mesh();
            if (pList[0] != null)
            {
                Polyline pL = null;
                pList[0].TryGetPolyline(out pL);
                pList.RemoveAt(0);
                mPatch = Rhino.Geometry.Mesh.CreatePatch(pL, tol, null, pList, null, null, true, 1);
            }

            return mPatch;
        }

        public static Mesh OgrMultiPolyToMesh(OSGeo.OGR.Geometry multipoly)
        {
            double tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            OSGeo.OGR.Geometry sub_geom;
            List<Mesh> mList = new List<Mesh>();

            for (int i = 0; i < multipoly.GetGeometryCount(); i++)
            {
                sub_geom = multipoly.GetGeometryRef(i);
                Mesh mP = Convert.OgrPolygonToMesh(sub_geom);
                mP.UnifyNormals();
                mList.Add(mP);
                sub_geom.Dispose();
            }
            Mesh m = new Mesh();
            m.Append(mList);
            m.RebuildNormals();
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
                mm.RebuildNormals();
                mm.UnifyNormals();
                return mm;

            }
            return m;
        }
        //////////////////////////////////////////////////////


        //////////////////////////////////////////////////////
        ///TODO: Converting Rhino/GH geometry type to GDAL geometry types
        ///
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
        public static List<int> DegToNum (double lat_deg, double lon_deg, int zoom)
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
        public static List<List<int>> GetTileRange(BoundingBox bnds, int zoom)
        {
            Point3d bndsMin = Convert.ToWGS(bnds.Min);
            Point3d bndsMax = Convert.ToWGS(bnds.Max);
            double xm = bndsMin.X;
            double xmx = bndsMax.X;
            double ym = bndsMin.Y;
            double ymx = bndsMax.Y;
            List<int> starting = Convert.DegToNum(ymx, xm, zoom);
            List<int> ending = Convert.DegToNum(ym, xmx, zoom);
            List<int> x_range = new List<int> { starting[0], ending[0] };
            List<int> y_range = new List<int> { starting[1], ending[1] };
            return new List<List<int>> { x_range, y_range };
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
            tile_bound.Add(Convert.ToXYZ(new Point3d(xm, ym,0)));
            tile_bound.Add(Convert.ToXYZ(new Point3d(xmx, ym,0)));
            tile_bound.Add(Convert.ToXYZ(new Point3d(xmx, ymx,0)));
            tile_bound.Add(Convert.ToXYZ(new Point3d(xm, ymx,0)));
            tile_bound.Add(Convert.ToXYZ(new Point3d(xm, ym,0)));
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

        ///Get list of mapping service Endpoints
        public static string GetEnpoints()
        {
            string jsonString = "";
            using (System.Net.WebClient wc = new System.Net.WebClient())
            {
                string URI = "https://raw.githubusercontent.com/blueherongis/Heron/master/HeronServiceEndpoints.json";
                jsonString = wc.DownloadString(URI);
            }

            return jsonString;
        }

        ///Check if cached images exist in cache folder
        public static bool CheckCacheImagesExist(List<string> fileLocs)
        {
            foreach (string fileLoc in fileLocs)
            {
                if (!File.Exists(fileLoc))
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
            //doesn't work for png, need to find a common ID between jpg and png
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
