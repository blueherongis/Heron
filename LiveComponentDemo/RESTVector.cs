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
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;
using GH_IO;
using GH_IO.Serialization;

using Newtonsoft.Json.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Serialization;

namespace Heron
{
    public class RESTVector : GH_Component
    {
        //Class Constructor
        public RESTVector() : base("Get REST Vector","RESTVector","Get vector data from ArcGIS REST Services","Heron","GIS REST")
        { 
        
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("REST URL", "URL", "ArcGIS REST Service website to query", GH_ParamAccess.item);
            pManager.AddBooleanParameter("run", "get", "Go ahead to download vector data from the Service", GH_ParamAccess.item, false);
            
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("fieldNames", "fieldNames", "List of data fields associated with vectors", GH_ParamAccess.list);
            pManager.AddTextParameter("fieldValues", "fieldValues", "Data values associated with vectors", GH_ParamAccess.tree);
            pManager.AddPointParameter("featurePoints", "featurePoints", "Points of vector data", GH_ParamAccess.tree);
            pManager.AddTextParameter("RESTQuery", "RESTQuery", "Full text of REST query", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>(0, boundary);

            string URL = "";
            DA.GetData<string>("REST URL", ref URL);

            bool run = false;
            DA.GetData<bool>("run", ref run);

            int SRef = 3857;

            GH_Structure<GH_String> mapquery = new GH_Structure<GH_String>();
            GH_Structure<GH_ObjectWrapper> jT = new GH_Structure<GH_ObjectWrapper>();
            List<JObject> j = new List<JObject>();

            GH_Structure<GH_Point> restpoints = new GH_Structure<GH_Point>();
            GH_Structure<GH_String> attpoints = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fieldnames = new GH_Structure<GH_String>();


            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path cpath = new GH_Path(i);
                Point3d min = ConvertToWSG(boundary[i].GetBoundingBox(true).Min);
                Point3d max = ConvertToWSG(boundary[i].GetBoundingBox(true).Max);

                string restquery = URL +
                  "query?where=&text=&objectIds=&time=&geometry=" + ConvertLat(min.X, SRef) + "%2C" + ConvertLon(min.Y, SRef) + "%2C" + ConvertLat(max.X, SRef) + "%2C" + ConvertLon(max.Y, SRef) +
                  "&geometryType=esriGeometryEnvelope&inSR=" + SRef +
                  "&spatialRel=esriSpatialRelIntersects&relationParam=&outFields=*&returnGeometry=true&maxAllowableOffset=&geometryPrecision=" +
                  "&outSR=" + SRef +
                  "&returnIdsOnly=false&returnCountOnly=false&orderByFields=&groupByFieldsForStatistics=&outStatistics=&returnZ=false&returnM=false&gdbVersion=&returnDistinctValues=false&f=json";

                mapquery.Append(new GH_String(restquery), cpath);

                System.Net.HttpWebRequest req = System.Net.WebRequest.Create(restquery) as System.Net.HttpWebRequest;
                string result = null;

                if (run == true)
                {
                    using (System.Net.HttpWebResponse resp = req.GetResponse() as System.Net.HttpWebResponse)
                    {
                        System.IO.StreamReader reader = new System.IO.StreamReader(resp.GetResponseStream());
                        result = reader.ReadToEnd();
                        reader.Close();
                    }
                }

                jT.Append(new GH_ObjectWrapper(JsonConvert.DeserializeObject<JObject>(result)), cpath);
                j.Add(JsonConvert.DeserializeObject<JObject>(result));

                JArray e = (JArray)j[i]["features"];

                for (int m = 0; m < e.Count; m++)
                {
                    JObject aa = (JObject)j[i]["features"][m]["attributes"];
                    GH_Path path = new GH_Path(i, m);

                    //choose type of geometry to read
                    JsonReader jreader = j[i]["features"][m]["geometry"].CreateReader();
                    int jrc = 0;
                    string gt = null;
                    while ((jreader.Read()) && (jrc < 1))
                    {
                        if (jreader.Value != null)
                        {
                            //gtype.Add(jreader.Value, path);
                            gt = jreader.Value.ToString();
                            jrc++;
                        }
                    }

                    JArray c = (JArray)j[i]["features"][m]["geometry"][gt][0];
                    for (int k = 0; k < c.Count; k++)
                    {
                        double xx = (double)j[i]["features"][m]["geometry"][gt][0][k][0];
                        double yy = (double)j[i]["features"][m]["geometry"][gt][0][k][1];
                        restpoints.Append(new GH_Point(ConvertXY(xx, yy, SRef)), path);
                    }

                    foreach (JProperty attribute in j[i]["features"][m]["attributes"])
                    {
                        attpoints.Append(new GH_String(attribute.Value.ToString()), path);
                    }
                }

                //Get the field names
                foreach (JObject fn in j[i]["fields"])
                {
                    fieldnames.Append(new GH_String(fn["alias"].Value<string>()), cpath);
                }
            }

            DA.SetDataList(0, fieldnames.get_Branch(0));
            DA.SetDataTree(1, attpoints);
            DA.SetDataTree(2, restpoints);
            DA.SetDataTree(3, mapquery);

        }

        //Conversion from WSG84 to Google/Bing from
        //http://alastaira.wordpress.com/2011/01/23/the-google-maps-bing-maps-spherical-mercator-projection/

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


        //Using Rhino's EarthAnchorPoint to Transform.  GetModelToEarthTransform() translates to WSG84.
        //https://github.com/gHowl/gHowlComponents/blob/master/gHowl/gHowl/GEO/XYZtoGeoComponent.cs
        //https://github.com/mcneel/rhinocommon/blob/master/dotnet/opennurbs/opennurbs_3dm_settings.cs  search for "model_to_earth"

        public static Point3d ConvertToWSG(Point3d xyz)
        {
            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);
            Point3d ptON = new Point3d(xyz.X, xyz.Y, xyz.Z);
            ptON = xf * ptON;
            return ptON;
        }

        public static Point3d ConvertToXYZ(Point3d wsg)
        {
            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);

            //http://www.grasshopper3d.com/forum/topics/matrix-datatype-in-rhinocommon
            //Thanks Andrew
            Transform Inversexf = new Transform();
            xf.TryGetInverse(out Inversexf);
            Point3d ptMod = new Point3d(wsg.X, wsg.Y, wsg.Z);
            ptMod = Inversexf * ptMod;
            return ptMod;
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

            return ConvertToXYZ(coord);
        }



        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.vector;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{3E93C79E-954C-4074-8637-E1B9BDC8B367}"); }
        }
    }
}
