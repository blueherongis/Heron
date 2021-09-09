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
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Special;
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
    public class RESTLayer : HeronComponent
    {
        //Class Constructor
        public RESTLayer() : base("Get REST Service Layers", "RESTLayer", "Discover ArcGIS REST Service Layers", "GIS REST")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Service URL", "serviceURL", "Service URL string", GH_ParamAccess.item);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Map Description", "mapDescription", "Description of the REST Service", GH_ParamAccess.item);
            pManager.AddTextParameter("Map Layers", "mapLayers", "Names of available Service Layers", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Map Integers", "mapIndexes", "Indexes of available Service Layers", GH_ParamAccess.list);
            pManager.AddTextParameter("Map Layer URLs", "URLs", "URLs of available Service Layers", GH_ParamAccess.list);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string URL = string.Empty;

            DA.GetData<string>("Service URL", ref URL);
            if (!URL.EndsWith(@"/")) { URL = URL + "/"; }

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
            List<string> layerUrl = new List<string>();

            Dictionary<string, int> d = new Dictionary<string, int>();

            for (int i = 1; i < j["layers"].Children()["name"].Count(); i++)
            {
                d[(string)j["layers"][i]["name"]] = (int)j["layers"][i]["id"];
                layerKey.Add((string)j["layers"][i]["name"]);
                layerInt.Add((int)j["layers"][i]["id"]);
                layerUrl.Add(URL + j["layers"][i]["id"].ToString() + "/");
            }

            DA.SetData(0, (string)j["description"]);
            //mapDescription = (string) j["description"];
            DA.SetDataList(1, layerKey);
            //mapLayer = layerKey;
            DA.SetDataList(2, layerInt);
            //mapInt = layerInt;
            DA.SetDataList(3, layerUrl);

        }





        private JObject vectorJson = JObject.Parse(Heron.Convert.GetEnpoints());

        /// <summary>
        /// Adds to the context menu an option to create a pre-populated list of common REST Vector sources
        /// </summary>
        /// <param name="menu"></param>
        /// https://discourse.mcneel.com/t/generated-valuelist-not-working/79406/6?u=hypar
        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            var rasterSourcesJson = vectorJson["REST Vector"].Select(x => x["source"]).Distinct();
            List<string> rasterSources = rasterSourcesJson.Values<string>().ToList();
            foreach (var src in rasterSourcesJson)
            {
                ToolStripMenuItem root = GH_DocumentObject.Menu_AppendItem(menu, "Create " + src.ToString() + " Source List", CreateRasterList);
                root.ToolTipText = "Click this to create a pre-populated list of some " + src.ToString() + " sources.";
                base.AppendAdditionalMenuItems(menu);
            }
        }

        /// <summary>
        /// Creates a value list pre-populated with possible accent colors and adds it to the Grasshopper Document, located near the component pivot.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void CreateRasterList(object sender, System.EventArgs e)
        {
            string source = sender.ToString();
            source = source.Replace("Create ", "");
            source = source.Replace(" Source List", "");

            GH_DocumentIO docIO = new GH_DocumentIO();
            docIO.Document = new GH_Document();

            ///Initialize object
            GH_ValueList vl = new GH_ValueList();

            ///Clear default contents
            vl.ListItems.Clear();

            foreach (var service in vectorJson["REST Vector"])
            {
                if (service["source"].ToString() == source)
                {
                    GH_ValueListItem vi = new GH_ValueListItem(service["service"].ToString(), String.Format("\"{0}\"", service["url"].ToString()));
                    vl.ListItems.Add(vi);
                }
            }

            ///Set component nickname
            vl.NickName = source;

            ///Get active GH doc
            GH_Document doc = OnPingDocument();
            if (docIO.Document == null) return;

            ///Place the object
            docIO.Document.AddObject(vl, false, 1);

            ///Get the pivot of the "URL" param
            PointF currPivot = Params.Input[0].Attributes.Pivot;

            ///Set the pivot of the new object
            vl.Attributes.Pivot = new PointF(currPivot.X - 400, currPivot.Y - 11);

            docIO.Document.SelectAll();
            docIO.Document.ExpireSolution();
            docIO.Document.MutateAllIds();
            IEnumerable<IGH_DocumentObject> objs = docIO.Document.Objects;
            doc.DeselectAll();
            doc.UndoUtil.RecordAddObjectEvent("Create REST Vector Source List", objs);
            doc.MergeDocument(docIO.Document);
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
            get { return new Guid("{8F33D7B4-FF14-438A-B49B-7DF895890BDD}"); }
        }
    }
}
