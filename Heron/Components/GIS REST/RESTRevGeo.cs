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
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
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
using Newtonsoft.Json.Utilities;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Serialization;

namespace Heron
{
    public class RESTRevGeo : HeronComponent
    {
        //Class Constructor
        public RESTRevGeo() : base("ESRI REST Service Reverse Geocode", "RESTRevGeo_compute", "Get the closest addresses to XY coordinates using the ESRI reverse geocode service.", "GIS REST")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("XY", "xyPoint", "Points for which to find addresses", GH_ParamAccess.tree);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Address", "Address", "Address closest to point", GH_ParamAccess.tree);
            pManager.AddTextParameter("Neighborhood", "Neighborhood", "Neighborhood", GH_ParamAccess.tree);
            pManager.AddTextParameter("City", "City", "City", GH_ParamAccess.tree);
            pManager.AddTextParameter("Region", "Region", "Region", GH_ParamAccess.tree);
            pManager.AddTextParameter("Postal", "Postal", "Postal", GH_ParamAccess.tree);
            pManager.AddTextParameter("Country", "Country", "Country", GH_ParamAccess.tree);
            pManager.AddTextParameter("LAT", "LAT", "Latitude", GH_ParamAccess.tree);
            pManager.AddTextParameter("LON", "LON", "Longitude", GH_ParamAccess.tree);

        }

        public delegate JObject jsonDelegate(string delegString);
        /*

        GH_Structure<GH_String> addressTree = new GH_Structure<GH_String>();
        GH_Structure<GH_String> neighborhoodTree = new GH_Structure<GH_String>();
        GH_Structure<GH_String> cityTree = new GH_Structure<GH_String>();
        GH_Structure<GH_String> regionTree = new GH_Structure<GH_String>();
        GH_Structure<GH_String> postalTree = new GH_Structure<GH_String>();
        GH_Structure<GH_String> countryTree = new GH_Structure<GH_String>();
        GH_Structure<GH_String> latTree = new GH_Structure<GH_String>();
        GH_Structure<GH_String> lonTree = new GH_Structure<GH_String>();
         * */

        //add "async" after override to make asynchronous
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Point> xyz = new GH_Structure<GH_Point>();

            DA.GetDataTree<GH_Point>(0, out xyz);

            GH_Structure<GH_String> addressTree = new GH_Structure<GH_String>();
            GH_Structure<GH_String> neighborhoodTree = new GH_Structure<GH_String>();
            GH_Structure<GH_String> cityTree = new GH_Structure<GH_String>();
            GH_Structure<GH_String> regionTree = new GH_Structure<GH_String>();
            GH_Structure<GH_String> postalTree = new GH_Structure<GH_String>();
            GH_Structure<GH_String> countryTree = new GH_Structure<GH_String>();
            GH_Structure<GH_String> latTree = new GH_Structure<GH_String>();
            GH_Structure<GH_String> lonTree = new GH_Structure<GH_String>();

            //GetAsyncClass deldata = new GetAsyncClass();
            //jsonDelegate del = new jsonDelegate(deldata.GetAsyncJson);

            ///GDAL setup
            //Heron.GdalConfiguration.ConfigureOgr();
            //Heron.GdalConfiguration.ConfigureGdal();

            ///Set transform from input spatial reference to Heron spatial reference
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            OSGeo.OSR.SpatialReference osmSRS = new OSGeo.OSR.SpatialReference("");
            osmSRS.SetFromUserInput("WGS84");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);

            ///Apply EAP to HeronSRS
            Transform heronToUserSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);

            ///Set transforms between source and HeronSRS
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(heronSRS, osmSRS);


            for (int a = 0; a < xyz.Branches.Count; a++)
            {
                IList branch = xyz.Branches[a];
                GH_Path path = xyz.Paths[a];
                foreach (GH_Point pt in branch)
                {
                    Point3d userPt = pt.Value;
                    userPt.Transform(heronToUserSRSTransform);
                    //Point3d geopt = Heron.Convert.XYZToWGS(pt.Value);
                    Point3d geopt = Heron.Convert.OSRTransformPoint3dToPoint3d(userPt,revTransform);
                    string webrequest = "https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/reverseGeocode?location=" + geopt.X + "%2C+" + geopt.Y + "&distance=200&outSR=&f=pjson";

                    //Synchronous method
                    string output = GetData("https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/reverseGeocode?location=" + geopt.X + "%2C+" + geopt.Y + "&distance=200&outSR=&f=pjson");
                    JObject ja = JObject.Parse(output);

                    //Delegate method
                    //IAsyncResult jaInvoke = del.BeginInvoke(webrequest, null, null);
                    //JObject ja = del.EndInvoke(jaInvoke);

                    //Asynchronous method.  Needs "async" after override to work
                    //JObject ja = await GetAsync("https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/reverseGeocode?location=" + geopt.X + "%2C+" + geopt.Y + "&distance=200&outSR=&f=pjson");


                    addressTree.Append(new GH_String(ja["address"]["Address"].ToString()), path);
                    neighborhoodTree.Append(new GH_String(ja["address"]["Neighborhood"].ToString()), path);
                    cityTree.Append(new GH_String(ja["address"]["City"].ToString()), path);
                    regionTree.Append(new GH_String(ja["address"]["Region"].ToString()), path);
                    postalTree.Append(new GH_String(ja["address"]["Postal"].ToString()), path);
                    countryTree.Append(new GH_String(ja["address"]["CountryCode"].ToString()), path);
                    latTree.Append(new GH_String(ja["location"]["y"].ToString()), path);
                    lonTree.Append(new GH_String(ja["location"]["x"].ToString()), path);

                }
            }

            if (addressTree == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Candidate locations found");
                return;
            }
            else
            {
                DA.SetDataTree(0, addressTree);
                DA.SetDataTree(1, neighborhoodTree);
                DA.SetDataTree(2, cityTree);
                DA.SetDataTree(3, regionTree);
                DA.SetDataTree(4, postalTree);
                DA.SetDataTree(5, countryTree);
                DA.SetDataTree(6, latTree);
                DA.SetDataTree(7, lonTree);

            }

        }


        public static string GetData(string qst)
        {
            System.Net.HttpWebRequest req = System.Net.WebRequest.Create(qst) as System.Net.HttpWebRequest;
            string result = null;
            try
            {
                using (System.Net.HttpWebResponse resp = req.GetResponse() as System.Net.HttpWebResponse)
                {
                    System.IO.StreamReader reader = new System.IO.StreamReader(resp.GetResponseStream());
                    result = reader.ReadToEnd();
                    reader.Close();
                }
            }
            catch
            {
                return "Something went wrong getting data from the Service";
            }
            return result;
        }

        // Async .NET 4.5 Json from here http://www.jayway.com/2012/03/13/httpclient-makes-get-and-post-very-simple/
        public async Task<JObject> GetAsync(string uri)
        {
            var httpClient = new HttpClient();
            var content = await httpClient.GetStringAsync(uri);
            return await Task.Run(() => JObject.Parse(content));
        }

        public class GetAsyncClass
        {
            public JObject GetAsyncJson(string uri)
            {
                string output = GetData(uri);
                JObject ja = JObject.Parse(output);
                return ja;
            }
        }

        static void delCallBack(IAsyncResult async)
        {
            System.Runtime.Remoting.Messaging.AsyncResult ar = (System.Runtime.Remoting.Messaging.AsyncResult)async;
            jsonDelegate del = (jsonDelegate)ar.AsyncDelegate;
            JObject ja = del.EndInvoke(async);
        }



        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.revgeocode;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{a4c71bd1-e3ab-46bf-9484-f7f3bd09d383}"); }
        }


    }
}
