using Grasshopper.GUI;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace Heron
{
    public class RESTOSM : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MyComponent1 class.
        /// </summary>
        public RESTOSM()
          : base("Get REST OSM", "OSMRest",
              "Get an OSM vector file within a boundary from web services such as the Overpass API.  " +
                "Use a search term to filter results and increase speed. ",
               "GIS REST")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for data", GH_ParamAccess.item);
            pManager.AddTextParameter("Folder Path", "folderPath", "Folder to save OSM vector files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for OSM vector file name", GH_ParamAccess.item, OSMSource);
            pManager.AddTextParameter("Search Term", "searchTerm", "A basic search term to filter the response from the web service. For more advanced queries, use the overpassQL input.", GH_ParamAccess.item);
            pManager.AddTextParameter("Overpass Query Language API", "overpassQL", "Query string for the Overpass API.  " +
                "You can use '{bbox}' as a placeholder for '(bottom, left, top, right)' in the Overpass API query if a boundary polyline is provided.  " +
                "See https://wiki.openstreetmap.org/wiki/Overpass_API/Language_Guide", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "get", "Go ahead and download OSM vector files from the service", GH_ParamAccess.item, false);

            pManager[0].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;

            Message = OSMSource;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("OSM File", "osmFile", "File location of downloaded OSM vector file. To be used as input for the ImportOSM component.", GH_ParamAccess.item);
            pManager.AddTextParameter("OSM Query", "osmQuery", "Full text of REST query.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve boundary = null;
            DA.GetData<Curve>(0, ref boundary);

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
                prefix = osmSource;
            }

            string searchTerm = string.Empty;
            DA.GetData<string>(3, ref searchTerm);
            if (!String.IsNullOrEmpty(searchTerm))
            {
                searchTerm = System.Net.WebUtility.UrlEncode("[" + searchTerm + "]");
            }

            string overpassQL = string.Empty;
            DA.GetData<string>(4, ref overpassQL);

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            string URL = osmURL;

            GH_Structure<GH_String> osmList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> osmQuery = new GH_Structure<GH_String>();

            string oQ = string.Empty;

            string left = string.Empty;
            string bottom = string.Empty;
            string right = string.Empty;
            string top = string.Empty;

            ///Construct query with bounding box
            if (boundary != null)
            {
                /// Check boundary to make sure it's valid
                if (!boundary.GetBoundingBox(true).IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                    return;
                }

                //offset boundary to ensure data from opentopography fully contains query boundary
                //double offsetD = 200 * Rhino.RhinoMath.UnitScale(UnitSystem.Meters, Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem);
                //Curve offsetB = boundary.Offset(Plane.WorldXY, offsetD, 1, CurveOffsetCornerStyle.Sharp)[0];
                //offsetB = boundary;


                ///GDAL setup
                Heron.GdalConfiguration.ConfigureOgr();
                Heron.GdalConfiguration.ConfigureGdal();

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

                ///Get OSM frame for given boundary
                Point3d max = new Point3d();
                Point3d maxM = boundary.GetBoundingBox(true).Max;
                maxM.Transform(heronToUserSRSTransform);
                max = Heron.Convert.OSRTransformPoint3dToPoint3d(maxM, revTransform);

                Point3d min = new Point3d();
                Point3d minM = boundary.GetBoundingBox(true).Min;
                minM.Transform(heronToUserSRSTransform);
                min = Heron.Convert.OSRTransformPoint3dToPoint3d(minM, revTransform);

                left = min.X.ToString();
                bottom = min.Y.ToString();
                right = max.X.ToString();
                top = max.Y.ToString();

                ///Override search with Query Language
                if (!String.IsNullOrEmpty(overpassQL))
                {
                    string bbox = "(" + bottom + "," + left + "," + top + "," + right + ")";
                    overpassQL = overpassQL.Replace("{bbox}", bbox);
                    oQ = osmURL.Split('=')[0] + "=" + overpassQL;
                    osmQuery.Append(new GH_String(oQ));
                    DA.SetDataTree(1, osmQuery);
                }

                else
                {
                    oQ = Convert.GetOSMURL(timeout, searchTerm, left, bottom, right, top, osmURL);
                    osmQuery.Append(new GH_String(oQ));
                    DA.SetDataTree(1, osmQuery);
                }
            }

            ///Construct query with Overpass QL 
            ///https://wiki.openstreetmap.org/wiki/Overpass_API/Language_Guide
            else if (!String.IsNullOrEmpty(overpassQL) && boundary == null)
            {
                oQ = osmURL.Split('=')[0] + "=" + overpassQL;
                osmQuery.Append(new GH_String(oQ));
                DA.SetDataTree(1, osmQuery);
            }


            if (run && !done)
            {
                Message = "Connecting with server...";
                ///Allow async download of OSM files
                ///https://docs.microsoft.com/en-us/dotnet/api/system.net.webclient.downloadfilecompleted?view=net-6.0
                using (var webClient = new WebClient())
                {
                    webClient.DownloadFileCompleted += new AsyncCompletedEventHandler(DownloadCompleted);
                    webClient.DownloadProgressChanged += new DownloadProgressChangedEventHandler(ProgressChanged);
                    webClient.DownloadFileAsync(new Uri(oQ), Path.Combine(folderPath, prefix + ".osm"));
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, webClient.Headers.ToString());
                    if (webClient.IsBusy)
                    {
                        //Message = "Busy";
                    }
                }
            }

            osmList.Append(new GH_String(Path.Combine(folderPath, prefix + ".osm")));

            ///Populate outputs
            if (done)
            {
                DA.SetDataTree(0, osmList);
            }
            else { DA.SetDataTree(0, new GH_Structure<GH_String>()); }
            done = false;
        }


        private bool done = false;
        private string lastSize = String.Empty;
        private void ProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            string s = ConvertSize.ToSize(e.BytesReceived, ConvertSize.SizeUnits.MB);
            if (lastSize != s)
            {
                Message = "Downloading file (" + s + " MB)";
                //Grasshopper.Instances.RedrawCanvas();
                lastSize = s;
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

            Message = "Downloaded " + lastSize + " MB OSM file";
            done = true;
            lastSize = String.Empty;
            System.Threading.Thread.Sleep(100);
            Grasshopper.Instances.RedrawCanvas();
            ExpireSolution(true);
            Message = OSMSource;
        }

        ////////////////////////////
        //Menu Items

        private bool IsServiceSelected(string serviceString)
        {
            return serviceString.Equals(osmSource);
        }


        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            if (osmSourceList == "")
            {
                osmSourceList = Convert.GetEnpoints();
            }

            JObject osmJson = JObject.Parse(osmSourceList);

            ToolStripMenuItem root = new ToolStripMenuItem("Pick OSM vector service");

            foreach (var service in osmJson["OSM Vector"])
            {
                string sName = service["service"].ToString();

                ToolStripMenuItem serviceName = new ToolStripMenuItem(sName);
                serviceName.Tag = sName;
                serviceName.Checked = IsServiceSelected(sName);
                serviceName.ToolTipText = service["description"].ToString();
                serviceName.Click += ServiceItemOnClick;

                root.DropDownItems.Add(serviceName);
            }

            menu.Items.Add(root);

            ToolStripMenuItem tOut = new ToolStripMenuItem("Set Timeout (in seconds)");
            GH_DocumentObject.Menu_AppendTextItem(tOut.DropDown, timeout.ToString(), 
                new GH_MenuTextBox.KeyDownEventHandler(this.Menu_TimeoutValueKeyDown), 
                new GH_MenuTextBox.TextChangedEventHandler(this.Menu_TimeoutValueTextChanged), true, 200, true);

            menu.Items.Add(tOut);

            base.AppendAdditionalComponentMenuItems(menu);

        }

        ///Menu interaction for a text field from Grasshopper.Kernel.Parameters.Param_Interval in Grasshopper.dll
        private void Menu_TimeoutValueTextChanged(GH_MenuTextBox sender, string text)
        {
            if (text.Length == 0)
            {
                sender.TextBoxItem.ForeColor = SystemColors.WindowText;
                return;
            }
            
            if (Int32.TryParse(text, out int t)) 
            {
                sender.TextBoxItem.ForeColor = SystemColors.WindowText;
                return;
            }
            sender.TextBoxItem.ForeColor = Color.Red;
        }

        private void Menu_TimeoutValueKeyDown(GH_MenuTextBox sender, KeyEventArgs e)
        {
            Keys keyCode = e.KeyCode;
            if (keyCode == Keys.Return)
            {
                e.Handled = true;
                string text = sender.Text;
                if (text.Length > 0)
                {
                    if (Int32.TryParse(text, out int t))
                    {
                        timeout = t;
                    }
                }
                base.OnObjectChanged(GH_ObjectEventType.PersistentData);
                if (Control.ModifierKeys == Keys.Shift || Control.ModifierKeys == Keys.Control)
                {
                    sender.CloseEntireMenuStructure();
                }
                this.ExpireSolution(true);
                return;
            }
            if (keyCode != Keys.Escape)
            {
                return;
            }
            sender.CloseEntireMenuStructure();
        }

        private void ServiceItemOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string code = (string)item.Tag;
            if (IsServiceSelected(code))
                return;

            RecordUndoEvent("OSMSource");
            RecordUndoEvent("OSMURL");

            osmSource = code;
            osmURL = JObject.Parse(osmSourceList)["OSM Vector"].SelectToken("[?(@.service == '" + osmSource + "')].url").ToString();
            Message = osmSource;

            ExpireSolution(true);
        }


        ///////////////////////////

        ///////////////////////////
        //Sticky Parameters

        private string osmSourceList = Convert.GetEnpoints();
        private string osmSource = JObject.Parse(Convert.GetEnpoints())["OSM Vector"][0]["service"].ToString();
        private string osmURL = JObject.Parse(Convert.GetEnpoints())["OSM Vector"][0]["url"].ToString();
        private int timeout = 60; ///timeout in seconds

        public string SlippySourceList
        {
            get { return osmSourceList; }
            set
            {
                osmSourceList = value;
            }
        }

        public string OSMSource
        {
            get { return osmSource; }
            set
            {
                osmSource = value;
                Message = osmSource;
            }
        }

        public string OSMURL
        {
            get { return osmURL; }
            set
            {
                osmURL = value;
            }
        }

        public int Timeout
        {
            get { return timeout; }
            set
            {
                timeout = value;
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("OSMService", OSMSource);
            writer.SetInt32("Timeout", Timeout);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            OSMSource = reader.GetString("OSMService");
            Timeout = reader.GetInt32("Timeout");
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
                return Properties.Resources.vector;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("22BC9A87-C63C-4BCA-AE62-906BB98522C1"); }
        }
    }
}