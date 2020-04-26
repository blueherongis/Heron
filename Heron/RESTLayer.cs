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
    public class RESTLayer : GH_Component
    {
        //Class Constructor
        public RESTLayer() : base("Get REST Service Layers","RESTLayer","Discover ArcGIS REST Service Layers","Heron","GIS REST")
        { 
        
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Service URL", "serviceURL", "Service URL string", GH_ParamAccess.item);
            
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Map Description", "mapDescription", "Description of the REST Service", GH_ParamAccess.item);
            pManager.AddTextParameter("Map Layer", "mapLayers", "Names of available Service Layers", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Map Integer", "mapIndex", "Index of available Service Layers", GH_ParamAccess.list);
            
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string URL = "";
                       
            DA.GetData<string>("Service URL", ref URL);

    //get json from rest service
    string restquery = URL + "?f=pjson";

    System.Net.HttpWebRequest req = System.Net.WebRequest.Create(restquery) as System.Net.HttpWebRequest;
    string result = null;

    using (System.Net.HttpWebResponse resp = req.GetResponse() as System.Net.HttpWebResponse)
    {
      System.IO.StreamReader reader = new System.IO.StreamReader(resp.GetResponseStream());
      result = reader.ReadToEnd();
      reader.Close();
    }

    //parse json into a description and list of layer values
    JObject j = JObject.Parse(result);
    List<string> layerKey = new List<string>();
    List<int> layerInt = new List<int>();

    Dictionary<string, int> d = new Dictionary<string, int>();

    for (int i = 1; i < j["layers"].Children()["name"].Count(); i++){
      d[(string) j["layers"][i]["name"]] = (int) j["layers"][i]["id"];
      layerKey.Add((string) j["layers"][i]["name"]);
      layerInt.Add((int) j["layers"][i]["id"]);
    }

    DA.SetData("Map Description", (string)j["description"]);
    //mapDescription = (string) j["description"];
    DA.SetDataList("Map Layer", layerKey);
    //mapLayer = layerKey;
    DA.SetDataList("Map Integer", layerInt);
    //mapInt = layerInt;

        }

  

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.layer;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{AD3A9FBB-AD30-4C95-BDD4-44D804895120}"); }
        }
    }
}
