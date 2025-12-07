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
using System.Threading.Tasks;
using System.ComponentModel;
using System.Net;
using System.Net.Http;

using OSGeo.OSR;
using OSGeo.OGR;
using Grasshopper.Kernel.Special;
using System.Drawing;

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
            pManager.AddTextParameter("Folder Path", "folderPath", "Folder to save image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item);
            pManager.AddTextParameter("Custom URL", "customURL", "Optional input for a custom REST Topo URL.  Make sure to format the URL with bounding box corner placeholders {0} {1} {2} and {3} " +
                "where 0=western-most longitude, 1=southern-most latitude, 2=eastern-most longitude, 3=northern-most latitude.  " +
                "Create a sample REST Topo dropdown list from the menu for examples with other parameters included.  " +
                "This input will override the URL selected in the menu.", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "get", "Go ahead and download imagery from the service", GH_ParamAccess.item, false);

            pManager[2].Optional = true;
            pManager[3].Optional = true;

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
            if (!Directory.Exists(folderPath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Folder " + folderPath + " does not exist.  Using your system's temp folder " + Path.GetTempPath() + " instead.");
                folderPath = Path.GetTempPath();
            }

            string prefix = string.Empty;
            DA.GetData<string>(2, ref prefix);
            if (prefix == "")
            {
                prefix = topoSource;
            }

            string customURL = string.Empty;
            DA.GetData<string>(3, ref customURL);
            if (!string.IsNullOrEmpty(customURL))
            {
                topoURL = customURL;
                custom = true;
                Message = "Custom URL";
            }
            else
            {
                topoURL = JObject.Parse(topoSourceList)["REST Topo"].SelectToken("[?(@.service == '" + topoSource + "')].url").ToString();
            }

            bool run = false;
            DA.GetData<bool>(4, ref run);

            GH_Structure<GH_String> demList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> demQuery = new GH_Structure<GH_String>();

            ///Load in key from secrets
            HeronConfig.LoadKeys();
            ///For troubleshooting
            //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, HeronConfig.OpenTopographyAPIKey);

            ///GDAL setup
            Heron.GdalConfiguration.ConfigureGdal();
            Heron.GdalConfiguration.ConfigureOgr();

            ///Set transform from input spatial reference to Heron spatial reference
            ///TODO: verify the userSRS is valid
            OSGeo.OSR.SpatialReference wgsSRS = new OSGeo.OSR.SpatialReference("");
            wgsSRS.SetFromUserInput("WGS84");
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));

            ///Apply EAP to HeronSRS
            Transform userSRSToModelTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);


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
                Point3d min = offsetB.GetBoundingBox(true).Min;
                Point3d max = offsetB.GetBoundingBox(true).Max;
                OSGeo.OGR.Geometry minOGR = Heron.Convert.Point3dToOgrPoint(min, userSRSToModelTransform);
                minOGR.AssignSpatialReference(heronSRS);
                minOGR.TransformTo(wgsSRS);
                OSGeo.OGR.Geometry maxOGR = Heron.Convert.Point3dToOgrPoint(max, userSRSToModelTransform) ;
                maxOGR.AssignSpatialReference(heronSRS);
                maxOGR.TransformTo(wgsSRS);

                double west = 0.0, south = 0.0, east = 0.0, north = 0.0;
                ///Query opentopography.org
                if (topoURL.Contains("arcgis"))
                {
                    min.Transform(userSRSToModelTransform);
                    max.Transform(userSRSToModelTransform);
                    west = min.X;
                    south = min.Y;
                    east = max.X;
                    north = max.Y;
                }
                else
                {
                    west = minOGR.GetX(0);
                    south = minOGR.GetY(0);
                    east = maxOGR.GetX(0);
                    north = maxOGR.GetY(0);
                }


                string tQ = String.Format(topoURL, west, south, east, north);
                tQ = tQ.Replace("bboxSR=4326", "bboxSR=" + heronSRSInt);
                tQ = tQ.Replace("imageSR=4326", "imageSR=" + heronSRSInt);

                if (topoURL.Contains("portal.opentopography.org"))
                {
                    demQuery.Append(new GH_String(tQ + "YourKeyHere"));
                    tQ = tQ + HeronConfig.OpenTopographyAPIKey;
                }
                else
                {
                    demQuery.Append(new GH_String(tQ), path);
                }

                demList.Append(new GH_String(Path.Combine(folderPath, prefix + "_" + i + ".tif")), path);

                if (run && !done)
                {
                    Message = "Connecting with server...";
                    ///Allow async download of topo files
                    ///https://docs.microsoft.com/en-us/dotnet/api/system.net.webclient.downloadfilecompleted?view=net-6.0
                    using (var webClient = new WebClient())
                    {
                        webClient.DownloadFileCompleted += new AsyncCompletedEventHandler(DownloadCompleted);
                        webClient.DownloadProgressChanged += new DownloadProgressChangedEventHandler(ProgressChanged);
                        webClient.DownloadFileAsync(new Uri(tQ), Path.Combine(folderPath, prefix + "_" + i + ".tif"));
                        webClient.Dispose();
                    }
                }
            }
            
            ///Populate outputs
            if (done) 
            { 
                DA.SetDataTree(0, demList); 
            }
            else { DA.SetDataTree(0, new GH_Structure<GH_String>()); }
            DA.SetDataTree(1, demQuery);
            done = false;
        }

        public bool done = false;
        public bool custom = false; 
        private void ProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            if (e.ProgressPercentage.ToString().EndsWith("0"))
            {
                Message = "Downloading file..." + e.ProgressPercentage.ToString() + "%";
                Grasshopper.Instances.RedrawCanvas();            
            }
        }

        public void DownloadCompleted(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "File download cancelled.");
            }

            if (e.Error != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, e.Error.ToString());
            }

            Message = "Downloaded";
            done = true;
            System.Threading.Thread.Sleep(100);
            Message = topoSource;
            Grasshopper.Instances.RedrawCanvas();
            ExpireSolution(true);
        }

        ////////////////////////////
        //Menu Items

        private bool IsServiceSelected(string serviceString)
        {
            return serviceString.Equals(topoSource);
        }

        private JObject topoJson = JObject.Parse(Heron.Convert.GetEnpoints());

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem root = GH_DocumentObject.Menu_AppendItem(menu, "Create a REST Topo Source List", CreateTopoList);
            root.ToolTipText = "Click this to create a pre-populated list of some REST Topo sources.";
            base.AppendAdditionalMenuItems(menu);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            if (topoSourceList == "")
            {
                topoSourceList = Convert.GetEnpoints();
            }

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

            if (!custom)
            {
                Message = topoSource;
            }

            ExpireSolution(true);
        }

        /// <summary>
        /// Creates a value list pre-populated with possible accent colors and adds it to the Grasshopper Document, located near the component pivot.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void CreateTopoList(object sender, System.EventArgs e)
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

            foreach (var service in topoJson["REST Topo"])
            {
                //if (service["source"].ToString() == source)
                //{
                    GH_ValueListItem vi = new GH_ValueListItem(service["service"].ToString(), String.Format("\"{0}\"", service["url"].ToString()));
                    vl.ListItems.Add(vi);
                //}
            }

            ///Set component nickname
            vl.NickName = source;

            ///Get active GH doc
            GH_Document doc = OnPingDocument();
            if (docIO.Document == null) return;

            ///Place the object
            docIO.Document.AddObject(vl, false, 1);

            ///Get the pivot of the "URL" param
            PointF currPivot = Params.Input[3].Attributes.Pivot;

            ///Set the pivot of the new object
            vl.Attributes.Pivot = new PointF(currPivot.X - 500, currPivot.Y - 11);

            docIO.Document.SelectAll();
            docIO.Document.ExpireSolution();
            docIO.Document.MutateAllIds();
            IEnumerable<IGH_DocumentObject> objs = docIO.Document.Objects;
            doc.DeselectAll();
            doc.UndoUtil.RecordAddObjectEvent("Create REST Raster Source List", objs);
            doc.MergeDocument(docIO.Document);
        }


        ///////////////////////////

        ///////////////////////////
        //Stick Parameters

        private string topoSourceList = Convert.GetEnpoints();
        private string topoSource = JObject.Parse(Convert.GetEnpoints())["REST Topo"][0]["service"].ToString();
        private string topoURL;
        
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
            get { return new Guid("CB3CEA1F-9D17-4CA1-9294-35FF98908858"); }
        }
    }
}