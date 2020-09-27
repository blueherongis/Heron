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

namespace Heron
{
    public class RESTTopo : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MyComponent1 class.
        /// </summary>
        public RESTTopo()
          : base("Get REST Topography", "RESTTopo",
              "Get STRM, ALOS and GMRT topographic data from web services.  " +
                "These services include global coverage from the " +
                "Shuttle Radar Topography Mission (SRTM GL3 90m and SRTM GL1 30m), " +
                "Advanced Land Observing Satellite (ALOS World 3D - 30m) and " +
                "Global Multi-Resolution Topography (GMRT including bathymetry). Sources are opentopography.org and gmrt.org.",
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
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item, topoService);
            pManager.AddBooleanParameter("Run", "get", "Go ahead and download imagery from the service", GH_ParamAccess.item, false);

            Message = TopoService;
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

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            Dictionary<string, TopoServices> tServices = GetTopoServices();

            GH_Structure<GH_String> demList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> demQuery = new GH_Structure<GH_String>();

            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path path = new GH_Path(i);

                //Get image frame for given boundary and  make sure it's valid
                if (!boundary[i].GetBoundingBox(true).IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                    return;
                }

                //offset boundary to ensure data from opentopography fully contains query boundary
                double offsetD = 200 * Rhino.RhinoMath.UnitScale(UnitSystem.Meters, Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem);
                Curve offsetB = boundary[i].Offset(Plane.WorldXY, offsetD, 1, CurveOffsetCornerStyle.Sharp)[0];

                //Get dem frame for given boundary
                Point3d min = Heron.Convert.XYZToWGS(offsetB.GetBoundingBox(true).Min);
                Point3d max = Heron.Convert.XYZToWGS(offsetB.GetBoundingBox(true).Max);

                //Query opentopography.org
                //DEM types
                //SRTMGL3 SRTM GL3 (90m)
                //SRTMGL1 SRTM GL1 (30m)
                //SRTMGL1_E SRTM GL1 (Ellipsoidal)
                //AW3D30 ALOS World 3D 30m
                double west = min.X;
                double south = min.Y;
                double east = max.X;
                double north = max.Y;

                string tQ = String.Format(tServices[topoService].URL, west, south, east, north);

                if (run)
                {
                    System.Net.WebClient webClient = new System.Net.WebClient();
                    webClient.DownloadFile(tQ, folderPath + prefix + "_" + i + ".tif");
                    webClient.Dispose();
                }

                demList.Append(new GH_String(folderPath + prefix + "_" + i + ".tif"), path);
                demQuery.Append(new GH_String(tQ), path);

            }

            //populate outputs
            DA.SetDataTree(0, demList);
            DA.SetDataTree(1, demQuery);
        }

        ////////////////////////////
        //Menu Items

        private bool IsServiceSelected(string serviceString)
        {
            return serviceString.Equals(topoService);
        }


        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            Dictionary<string, TopoServices> services = GetTopoServices();

            //ToolStripMenuItem root = new ToolStripMenuItem("Pick a topo service");

            foreach (var service in services)
            {
                string sName = service.Key;

                ToolStripMenuItem serviceName = new ToolStripMenuItem(sName);
                serviceName.Tag = sName;
                serviceName.Checked = IsServiceSelected(sName);
                serviceName.ToolTipText = service.Value.ServiceDesc;
                serviceName.Click += ServiceItemOnClick;

                //root.DropDownItems.Add(serviceName);
                menu.Items.Add(serviceName);
            }

            //menu.Items.Add(root);

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

            RecordUndoEvent("TopoService");

            topoService = code;
            Message = topoService;

            ExpireSolution(true);
        }


        ///////////////////////////

        ///////////////////////////
        //Stick Parameters

        public class TopoServices
        {
            public string ServiceName { get; set; }
            public string ServiceDesc { get; set; }
            public string URL { get; set; }
        }

        public static Dictionary<string, TopoServices> GetTopoServices()
        {
            Dictionary<string, TopoServices> services = new Dictionary<string, TopoServices>()
            {
                {"SRTM GL1 (30m)", new TopoServices{ServiceName = "SRTM GL1 (30m)", ServiceDesc = "SRTM GL1 (30m) 1 Arcsecond resolution", URL = "https://portal.opentopography.org/API/globaldem?demtype=SRTMGL1&west={0}&south={1}&east={2}&north={3}"} },
                {"SRTM GL3 (90m)", new TopoServices{ServiceName = "SRTM GL3 (90m)", ServiceDesc = "SRTM GL3 (90m) 3 Arsecond resolution", URL = "https://portal.opentopography.org/API/globaldem?demtype=SRTMGL3&west={0}&south={1}&east={2}&north={3}"} },
                {"ALOS World 3D (30m)", new TopoServices{ServiceName = "ALOS World 3D (30m)", ServiceDesc = "ALOS World 3D (30m)", URL = "https://portal.opentopography.org/API/globaldem?demtype=AW3D30&west={0}&south={1}&east={2}&north={3}"} },
                {"GMRT Max", new TopoServices{ServiceName = "GMRT Max", ServiceDesc = "GMRT Max", URL = "https://www.gmrt.org/services/GridServer?west={0}&south={1}&east={2}&north={3}&layer=topo&format=geotiff&resolution=max"} },
                {"GMRT High", new TopoServices{ServiceName = "GMRT High", ServiceDesc = "GMRT High resolution. Will default to highest resolution if boundary is too small.", URL = "https://www.gmrt.org/services/GridServer?west={0}&south={1}&east={2}&north={3}&layer=topo&format=geotiff&resolution=high"} },
                {"GMRT Medium", new TopoServices{ServiceName = "GMRT Medium", ServiceDesc = "GMRT Medium resolution. Will default to higher resolution if boundary is too small.", URL = "https://www.gmrt.org/services/GridServer?west={0}&south={1}&east={2}&north={3}&layer=topo&format=geotiff&resolution=med"} },
                {"GMRT Low", new TopoServices{ServiceName = "GMRT Low", ServiceDesc = "GMRT Low resolution. Will default to higher resolution if boundary is too small.", URL = "https://www.gmrt.org/services/GridServer?west={0}&south={1}&east={2}&north={3}&layer=topo&format=geotiff&resolution=low"} }
            };
            return services;
        }



        private string topoService = "SRTM GL1 (30m)";

        public string TopoService
        {
            get { return topoService; }
            set
            {
                topoService = value;
                Message = topoService;
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("TopoService", TopoService);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            TopoService = reader.GetString("TopoService");
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
                //You can add image files to your project resources and access them like this:
                // return Resources.IconForThisComponent;
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