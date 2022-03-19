using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using GH_IO;
using GH_IO.Serialization;
using Rhino;
using Rhino.Geometry;

using Newtonsoft.Json.Linq;

namespace Heron
{
    public class RESTTopo : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MyComponent1 class.
        /// </summary>
        public RESTTopo()
          : base("Get REST Topography", "RESTTopo",
              "Get STRM, ALOS, GMRT and USGS topographic data from web services.  " +
                "These services include global coverage from the " +
                "Shuttle Radar Topography Mission (SRTM GL3 90m and SRTM GL1 30m), " +
                "Advanced Land Observing Satellite (ALOS World 3D - 30m) and " +
                "Global Multi-Resolution Topography (GMRT including bathymetry) " +
                "and North American coverage from the U.S. Geological Survey 3D Elevation Program (USGS 3DEP). Sources are opentopography.org, gmrt.org and elevation.nationalmap.gov.",
               "GIS REST")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddTextParameter("Target folder", "folderPath", "Folder to save image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "get", "Go ahead and download imagery from the service", GH_ParamAccess.item, false);

            pManager[2].Optional = true;

            Message = TopoSource;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Topo File", "topoFile", "File location of downloaded topographic data in GeoTIFF format. To be used as input for the ImportTopo component.", GH_ParamAccess.tree);
            pManager.AddTextParameter("Topo Query", "topoQuery", "Full text of REST query.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>(0, boundary);

            string folderPath = string.Empty;
            DA.GetData<string>(1, ref folderPath);
            if (!folderPath.EndsWith(@"\")) folderPath = folderPath + @"\";

            string prefix = string.Empty;
            DA.GetData<string>(2, ref prefix);
            if (prefix == "")
            {
                prefix = topoSource;
            }

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            GH_Structure<GH_String> demList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> demQuery = new GH_Structure<GH_String>();

            ///Load in key from secrets
            HeronConfig.LoadKeys();
            //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, HeronConfig.OpenTopographyAPIKey);

            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path path = new GH_Path(i);

                //Get image frame for given boundary and  make sure it's valid
                if (!boundary[i].GetBoundingBox(true).IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                    return;
                }

                ///Offset boundary to ensure data from opentopography fully contains query boundary
                Curve orientedBoundary = boundary[i].ToNurbsCurve();
                if (orientedBoundary.ClosedCurveOrientation(Plane.WorldXY) == CurveOrientation.Clockwise) { orientedBoundary.Reverse(); }
                double offsetD = 200 * Rhino.RhinoMath.UnitScale(UnitSystem.Meters, Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem);
                Curve offsetB = orientedBoundary.Offset(Plane.WorldXY, offsetD, 1, CurveOffsetCornerStyle.Sharp)[0];

                ///Get dem frame for given boundary
                Point3d min = Heron.Convert.XYZToWGS(offsetB.GetBoundingBox(true).Min);
                Point3d max = Heron.Convert.XYZToWGS(offsetB.GetBoundingBox(true).Max);

                ///Query opentopography.org
                double west = min.X;
                double south = min.Y;
                double east = max.X;
                double north = max.Y;

                string tQ = String.Format(topoURL, west, south, east, north);

                if (topoURL.Contains("portal.opentopography.org"))
                {
                    demQuery.Append(new GH_String(tQ + "YourKeyHere"));
                    tQ = tQ + HeronConfig.OpenTopographyAPIKey;
                }
                else
                {
                    demQuery.Append(new GH_String(tQ), path);
                }

                if (run)
                {
                    System.Net.WebClient webClient = new System.Net.WebClient();
                    webClient.DownloadFile(tQ, folderPath + prefix + "_" + i + ".tif");
                    webClient.Dispose();
                }

                demList.Append(new GH_String(folderPath + prefix + "_" + i + ".tif"), path);

            }

            ///Populate outputs
            DA.SetDataTree(0, demList);
            DA.SetDataTree(1, demQuery);
        }

        ////////////////////////////
        //Menu Items

        private bool IsServiceSelected(string serviceString)
        {
            return serviceString.Equals(topoSource);
        }


        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            if (topoSourceList == "")
            {
                topoSourceList = Convert.GetEnpoints();
            }

            JObject topoJson = JObject.Parse(topoSourceList);

            foreach (var service in topoJson["REST Topo"])
            {
                string sName = service["service"].ToString();

                ToolStripMenuItem serviceName = new ToolStripMenuItem(sName);
                serviceName.Tag = sName;
                serviceName.Checked = IsServiceSelected(sName);
                serviceName.ToolTipText = service["description"].ToString();
                serviceName.Click += ServiceItemOnClick;

                menu.Items.Add(serviceName);
            }

            base.AppendAdditionalComponentMenuItems(menu);

        }

        private void ServiceItemOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string code = (string)item.Tag;
            if (IsServiceSelected(code))
                return;

            RecordUndoEvent("TopoSource");

            topoSource = code;
            topoURL = JObject.Parse(topoSourceList)["REST Topo"].SelectToken("[?(@.service == '" + topoSource + "')].url").ToString();
            Message = topoSource;

            ExpireSolution(true);
        }


        ///////////////////////////

        ///////////////////////////
        //Stick Parameters

        private string topoSourceList = Convert.GetEnpoints();
        private string topoSource = JObject.Parse(Convert.GetEnpoints())["REST Topo"][0]["service"].ToString();
        private string topoURL = JObject.Parse(Convert.GetEnpoints())["REST Topo"][0]["url"].ToString();
        
        public string TopoSourceList
        {
            get { return topoSourceList; }
            set { topoSourceList = value; }
        }

        public string TopoSource
        {
            get { return topoSource; }
            set
            {
                topoSource = value;
                Message = topoSource;
            }
        }

        public string TopoURL
        {
            get { return topoURL; }
            set { topoURL = value; }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("TopoSource", TopoSource);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            TopoSource = reader.GetString("TopoSource");
            return base.Read(reader);
        }


        ///////////////////////////

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.img;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("C91EC97A-7275-4498-A16C-C2B87BB697A4"); }
        }
    }
}