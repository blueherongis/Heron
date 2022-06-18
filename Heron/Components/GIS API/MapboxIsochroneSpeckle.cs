using Grasshopper.Kernel;
using GH_IO;
using GH_IO.Serialization;
using Rhino.Geometry;
using System;
using System.IO;
using System.Net;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrasshopperAsyncComponent;
using System.Windows.Forms;
using Newtonsoft;
using Newtonsoft.Json.Linq;
using Grasshopper.Kernel.Types;
using OSGeo.OGR;
using OSGeo.OSR;

namespace Heron
{
    public class MapboxIsochroneSpeckle : GH_AsyncComponent
    {
        /// <summary>
        /// Initializes a new instance of the MapboxIsochrone class.
        /// </summary>
        public MapboxIsochroneSpeckle()
          : base("Mapbox Isochrone", "MapboxIsochrone",
              "The Mapbox Isochrone API allows you to request polygon features that show areas which are reachable within a specified amount of time from a location.  " +
                "The API calculates isochrones up to 60 minutes using driving, cycling, or walking profiles.",
              "Heron", "GIS API")
        {
            BaseWorker = new MapboxIsochroneWorker(this);
        }

        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.secondary; }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("Location", "location", "Location around which to center the isochrone lines.", GH_ParamAccess.item);

            pManager.AddNumberParameter("Contour", "contour", "The time, in minutes, or distance to use for the isochrone contour. Specify which type to use in the component menu." +
                "The maximum time that can be specified is 60 minutes. The maximum distance that can be specified is Rhino unit equivalent of 100,000 meters (100km).", GH_ParamAccess.item);

            pManager.AddNumberParameter("Denoise", "denoise", "A positive floating point from 0.0 to 1.0 that can be used to remove smaller contours. " +
                "The default is 0.25. A value of 1.0 will only return the largest contour for a given value. " +
                "A value of 0.5 drops any contours that are less than half the area of the largest contour in the set of contours for that same value.", GH_ParamAccess.item, 0.25);

            pManager.AddNumberParameter("Generalize", "generalize", "A positive floating point value, in meters, used as the tolerance for Douglas-Peucker generalization. " +
                "There is no upper bound. If no value is specified in the request, the Isochrone API will choose the most optimized generalization to use for the request. " +
                "Note that the generalization of contours can lead to self-intersections, as well as intersections of adjacent contours.", GH_ParamAccess.item);
            pManager[3].Optional = true;

            pManager.AddTextParameter("Mapbox Access Token", "mbToken", "Mapbox Access Token string for access to Mapbox resources. Or set an Environment Variable 'HERONMAPOXTOKEN' with your token as the string.", GH_ParamAccess.item, "");
            pManager.AddBooleanParameter("Run", "get", "Go ahead and get isochrones from the service", GH_ParamAccess.item, false);

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Isochrone", "Isochrone", "The Mapbox-generated Isochrone from a given point", GH_ParamAccess.item);
            pManager.AddTextParameter("URL", "url", "URL queried", GH_ParamAccess.item);
        }

        private bool IsProfileSelected(string profileString)
        {
            return profileString.Equals(profile);
        }
        private bool IsMinutesOrMetersSelected(string minutesOrMetersString)
        {
            return minutesOrMetersString.Equals(minutesOrMeters);
        }
        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            MapboxIsochroneSpeckle.Menu_AppendSeparator(menu);

            //base.AppendAdditionalMenuItems(menu);
            Menu_AppendItem(menu, "Cancel", (s, e) =>
            {
                RequestCancellation();
            });

            MapboxIsochroneSpeckle.Menu_AppendSeparator(menu);

            ///Add profile... to menu
            ToolStripMenuItem profileMenu = new ToolStripMenuItem("Profile...");
            profileMenu.ToolTipText = "Walking = For fastest travel by pedestrian and hiking travel paths. " +
                "Cycling = For fastest travel by bicycle. " +
                "Driving = For fastest travel by car using average conditions.";

            var profileList = new List<string> { "Walking", "Cycling", "Driving" };
            foreach (var p in profileList)
            {
                ToolStripMenuItem profileName = new ToolStripMenuItem(p);
                profileName.Checked = IsProfileSelected(p);
                profileName.Click += ProfileOnClick;
                profileName.Tag = p;
                profileMenu.DropDownItems.Add(profileName);
            }

            menu.Items.Add(profileMenu);

            ///Add price to menu
            ToolStripMenuItem minutesOrMetersMenu = new ToolStripMenuItem("Minutes or Distance...");
            minutesOrMetersMenu.ToolTipText = "Minutes = Use time to calculate the isochrone. The times, in minutes, to use for each isochrone contour. " +
                "The maximum time that can be specified is 60 minutes. " +
                "Distance = Use distance to calculate the isochrone. The maximum distance that can be specified is Rhino unit equivalent of 100,000 meters (100km).";

            var minutesOrMetersList = new List<string> { "Minutes", "Distance" };
            foreach (var m in minutesOrMetersList)
            {
                ToolStripMenuItem minutesOrMetersName = new ToolStripMenuItem(m);
                minutesOrMetersName.Checked = IsMinutesOrMetersSelected(m);
                minutesOrMetersName.Click += MinutesOrMetersOnClick;
                minutesOrMetersName.Tag = m;
                minutesOrMetersMenu.DropDownItems.Add(minutesOrMetersName);
            }

            menu.Items.Add(minutesOrMetersMenu);

            base.AppendAdditionalComponentMenuItems(menu);
        }

        private void ProfileOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string profileCode = (string)item.Tag;
            if (IsProfileSelected(profileCode))
                return;

            RecordUndoEvent("Profile");

            profile = profileCode;
            ExpireSolution(true);
        }

        private void MinutesOrMetersOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem minutesOrMetersItem = sender as ToolStripMenuItem;
            if (minutesOrMetersItem == null)
                return;

            string minutesOrMetersCode = (string)minutesOrMetersItem.Tag;
            if (IsMinutesOrMetersSelected(minutesOrMetersCode))
                return;

            RecordUndoEvent("MinutesOrMeters");

            minutesOrMeters = minutesOrMetersCode;
            ExpireSolution(true);
        }


        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private static string profile = "Walking";
        private static string minutesOrMeters = "Minutes";


        public static string Profile
        {
            get { return profile; }
            set
            {
                profile = value;
            }
        }

        public static string MinutesOrMeters
        {
            get { return minutesOrMeters; }
            set
            {
                minutesOrMeters = value;
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("Profile", Profile);
            writer.SetString("MinutesOrMeters", MinutesOrMeters);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            Profile = reader.GetString("Profile");
            MinutesOrMeters = reader.GetString("MinutesOrMeters");
            return base.Read(reader);
        }


        /////////////////////////

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.vector;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("8A852A1D-DD7E-4E93-8BD9-4B32DA71FD71"); }
        }
    }

    public class MapboxIsochroneWorker : WorkerInstance
    {
        public MapboxIsochroneWorker(GH_Component parent) : base(parent) { }

        Point3d Location { get; set; }

        double? Contour { get; set; }

        double? Denoise { get; set; }

        double? Generalize { get; set; }

        string ApiKey { get; set; } = string.Empty;

        bool Run { get; set; }

        List<IGH_GeometricGoo> Isochrones { get; set; }

        string Url { get; set; } = string.Empty;

        public override void DoWork(Action<string, double> ReportProgress, Action Done)
        {
            // Checking for cancellation
            if (CancellationToken.IsCancellationRequested) { return; }

            ///Make sure there's an API key
            string mbToken = ApiKey;
            if (mbToken == "")
            {
                string hmbToken = System.Environment.GetEnvironmentVariable("HERONMAPBOXTOKEN");
                if (hmbToken != null)
                {
                    mbToken = hmbToken;
                    RuntimeMessages.Add((GH_RuntimeMessageLevel.Remark, "Using Mapbox token stored in Environment Variable HERONMAPBOXTOKEN."));
                }
                else
                {
                    RuntimeMessages.Add((GH_RuntimeMessageLevel.Error, "No Mapbox token is specified.  Please get a valid token from mapbox.com"));
                    return;
                }
            }

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();

            ///Set transform from input spatial reference to Rhino spatial reference
            ///TODO: look into adding a step for transforming to CRS set in SetCRS 
            OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
            rhinoSRS.SetWellKnownGeogCS("WGS84");

            ///TODO: verify the userSRS is valid
            ///TODO: use this as override of global SetSRS
            OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
            //userSRS.SetFromUserInput(userSRStext);
            userSRS.SetFromUserInput("WGS84");

            ///Mapbox uses EPSG:3857
            OSGeo.OSR.SpatialReference sourceSRS = new SpatialReference("");
            sourceSRS.SetFromUserInput("EPSG:3857");

            ///These transforms move and scale in order to go from userSRS to XYZ and vice versa
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);
            Transform modelToUserSRSTransform = Heron.Convert.GetModelToUserSRSTransform(userSRS);
            Transform sourceToModelSRSTransform = Heron.Convert.GetUserSRSToModelTransform(sourceSRS);
            Transform modelToSourceSRSTransform = Heron.Convert.GetModelToUserSRSTransform(sourceSRS);
            
            double lat = Heron.Convert.XYZToWGS(Location).Y;
            double lon = Heron.Convert.XYZToWGS(Location).X;

            ///Setup contour style and unit type
            string contourType = String.Empty;
            double? contourNum = Contour;
            if (contourNum < 0.0) { contourNum = 0.0; }
            double unitConversion = Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);
            if (MapboxIsochroneSpeckle.MinutesOrMeters == "Distance") 
            { 
                contourType = "contours_meters=";
                contourNum = contourNum * unitConversion;
            }
            else
            {
                contourType = "contours_minutes=";
                if (contourNum > 60) 
                { 
                    contourNum = 60;
                    RuntimeMessages.Add((GH_RuntimeMessageLevel.Warning, "Maximum minute contour is 60.  Contour value reduced to 60."));
                }
            }

            ///Make sure Denoise is between 0..1
            double? denoiseNum = Denoise;
            if (denoiseNum < 0.0) { denoiseNum = 0.0; }
            if (denoiseNum > 1.0) { denoiseNum = 1.0; }

            ///Make sure Generalize is > 0
            double? generalizeNum = Generalize * unitConversion;
            if (generalizeNum < 0.0) { generalizeNum = 0.0; }

            ///Construct URL query string
            string url = @"https://api.mapbox.com/isochrone/v1/mapbox/";
            if (!String.IsNullOrEmpty(MapboxIsochroneSpeckle.Profile)) { url = url + MapboxIsochroneSpeckle.Profile.ToLower() + "/"; }
            if (lon > -180.0 && lon <= 180.0) { url = url + lon + "%2C"; }
            if (lat > -90.0 && lat <= 90.0) { url = url + lat + "?"; }
            if (!String.IsNullOrEmpty(contourType)) { url = url + contourType.ToLower(); }
            if (contourNum != null) { url = url + contourNum; }
            url = url + "&polygons=true";
            if (denoiseNum != null) { url = url + "&denoise=" + denoiseNum; }
            if (generalizeNum != null) { url = url + "&generalize=" + generalizeNum; }
            if (mbToken != "") { url = url + "&access_token=" + mbToken; }

            Url = url;


            if (CancellationToken.IsCancellationRequested) { return; }
            if (Run)
            {

                List<IGH_GeometricGoo> gGoo = new List<IGH_GeometricGoo>();
                OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(url, 0);

                if (dataSource == null)
                {
                    RuntimeMessages.Add((GH_RuntimeMessageLevel.Error, "Mapbox returned an invalid response."));
                    return;
                }

                ///Only interested in geometry, not fields or values
                OSGeo.OGR.Layer ogrLayer = dataSource.GetLayerByIndex(0);
                OSGeo.OGR.Feature feat;

                while ((feat = ogrLayer.GetNextFeature()) != null)
                {
                    OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();
                    ///reproject geometry to WGS84 and userSRS
                    ///TODO: look into using the SetCRS global variable here
                    if (geomUser.GetSpatialReference() == null) { geomUser.AssignSpatialReference(userSRS); }

                    geomUser.TransformTo(userSRS);
                    if (feat.GetGeometryRef() != null)
                    {
                        gGoo.AddRange(Heron.Convert.OgrGeomToGHGoo(geomUser, userSRSToModelTransform));
                    }
                }

                Isochrones = gGoo;
            }

            Done();
        }

        public override WorkerInstance Duplicate() => new MapboxIsochroneWorker(Parent);

        public override void GetData(IGH_DataAccess DA, GH_ComponentParamServer Params)
        {
            if (CancellationToken.IsCancellationRequested) return;

            Point3d _location = Point3d.Unset;
            double? _contour = null;
            double? _denoise = null;
            double? _generalize = null;
            string _apiKey = string.Empty;
            bool _run = false;

            DA.GetData(0, ref _location);
            DA.GetData(1, ref _contour);
            DA.GetData(2, ref _denoise);
            DA.GetData(3, ref _generalize);
            DA.GetData(4, ref _apiKey);
            DA.GetData(5, ref _run);

            Location = _location;
            Contour = _contour;
            Denoise = _denoise;
            Generalize = _generalize;
            ApiKey = _apiKey;
            Run = _run;
        }

        List<(GH_RuntimeMessageLevel, string)> RuntimeMessages { get; set; } = new List<(GH_RuntimeMessageLevel, string)>();

        public override void SetData(IGH_DataAccess DA)
        {
            if (CancellationToken.IsCancellationRequested) return;

            foreach (var (level, message) in RuntimeMessages)
            {
                Parent.AddRuntimeMessage(level, message);
            }
            Parent.Message = "hello";
            DA.SetDataList(0, Isochrones);
            DA.SetData(1, Url);
        }

    }
}