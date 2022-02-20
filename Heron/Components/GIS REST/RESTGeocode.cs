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
using Rhino.Geometry;
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
    public class RESTGeocode : HeronComponent
    {
        //Class Constructor
        public RESTGeocode() : base("ESRI REST Service Geocode", "RESTGeocode", "Get coordinates based on a Point-of-Interest or Address using the ESRI geocode service.", "GIS REST")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Addresses", "addresses", "POI or Address string(s) to geocode", GH_ParamAccess.tree);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Candidates", "Candidates", "List of Candidate locations", GH_ParamAccess.tree);
            pManager.AddTextParameter("Latitude", "LAT", "Latitude of Candidate location", GH_ParamAccess.tree);
            pManager.AddTextParameter("Longitude", "LON", "Longitude of Candidate location", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_String> Addresses = new GH_Structure<GH_String>();

            DA.GetDataTree<GH_String>("Addresses", out Addresses);

            GH_Structure<GH_String> addr = new GH_Structure<GH_String>();
            GH_Structure<GH_String> latx = new GH_Structure<GH_String>();
            GH_Structure<GH_String> lony = new GH_Structure<GH_String>();

            for (int a = 0; a < Addresses.Branches.Count; a++)
            {
                IList branch = Addresses.Branches[a];
                GH_Path path = Addresses.Paths[a];
                int count = 0;
                foreach (GH_String addressString in branch)
                {
                    string address = System.Net.WebUtility.UrlEncode(addressString.Value);
                    string output = GetData("https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/findAddressCandidates?Address=" + address + "&f=pjson");
                    JObject ja = JObject.Parse(output);

                    if (ja["candidates"].Count() < 1)
                    {
                        addr.Append(new GH_String("No Cadidate location found for this address"), path);
                        lony.Append(new GH_String(""), path);
                        latx.Append(new GH_String(""), path);
                    }
                    else
                    {
                        for (int i = 0; i < ja["candidates"].Count(); i++)
                        {
                            if (ja["candidates"][i]["score"].Value<int>() > 99)
                            {
                                addr.Append(new GH_String(ja["candidates"][i]["address"].ToString()), new GH_Path(path[count], i));
                                addr.Append(new GH_String("LON: " + ja["candidates"][i]["location"]["x"].ToString()), new GH_Path(path[count], i));
                                addr.Append(new GH_String("LAT: " + ja["candidates"][i]["location"]["y"].ToString()), new GH_Path(path[count], i));
                                lony.Append(new GH_String(ja["candidates"][i]["location"]["y"].ToString()), new GH_Path(path[count], i));
                                latx.Append(new GH_String(ja["candidates"][i]["location"]["x"].ToString()), new GH_Path(path[count], i));
                            }
                        }
                    }
                }
            }

            if (addr == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Candidate locations found");
                return;
            }
            else
            {
                DA.SetDataTree(0, addr);
                DA.SetDataTree(1, lony);
                DA.SetDataTree(2, latx);
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


        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.geocode;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{019FCF0D-08A1-4CB0-A0D7-EDD6F840378E}"); }
        }
    }
}
