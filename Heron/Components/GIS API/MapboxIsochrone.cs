using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Heron
{
    public class MapboxIsochrone : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MapboxIsochrone class.
        /// </summary>
        public MapboxIsochrone()
          : base("Mapbox Isochrone", "MapboxIsochrone",
              "The Mapbox Isochrone API allows you to request polygon features that show areas which are reachable within a specified amount of time from a location.  " +
                "The API calculates isochrones up to 60 minutes using driving, cycling, or walking profiles.",
              "GIS API")
        {
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
                "The maximum time that can be specified is 60 minutes. The maximum distance that can be specified is Rhino unit equivalent of 100,000 meters (100km).", GH_ParamAccess.list);

            pManager.AddNumberParameter("Denoise", "denoise", "A positive floating point from 0.0 to 1.0 that can be used to remove smaller contours. " +
                "The default is 0.25. A value of 1.0 will only return the largest contour for a given value. " +
                "A value of 0.5 drops any contours that are less than half the area of the largest contour in the set of contours for that same value.", GH_ParamAccess.item, 0.25);

            pManager.AddNumberParameter("Generalize", "generalize", "A positive floating point value, in meters, used as the tolerance for Douglas-Peucker generalization. " +
                "There is no upper bound. If no value is specified in the request, the Isochrone API will choose the most optimized generalization to use for the request. " +
                "Note that the generalization of contours can lead to self-intersections, as well as intersections of adjacent contours.", GH_ParamAccess.item);
            pManager[3].Optional = true;

            pManager.AddTextParameter("Mapbox Access Token", "mbToken", "Mapbox Access Token string for access to Mapbox resources. Or set an Environment Variable 'HERONMAPOXTOKEN' with your token as the string.", GH_ParamAccess.item, "");
            pManager.AddBooleanParameter("Run", "get", "Go ahead and get isochrones from the service", GH_ParamAccess.item, false);
            Message = Profile + " | " + MinutesOrMeters;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Isochrone", "Isochrone", "The Mapbox-generated Isochrone from a given point", GH_ParamAccess.list);
            pManager.AddTextParameter("URL", "Url", "URL queried", GH_ParamAccess.list);
            pManager.AddTextParameter("Mapbox Attribution", "mbAtt", "Mapbox word mark and text attribution required by Mapbox", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///Setup location
            Point3d location = new Point3d();
            DA.GetData(0, ref location);
            double lat = Heron.Convert.XYZToWGS(location).Y;
            double lon = Heron.Convert.XYZToWGS(location).X;

            ///Setup contour style and unit type
            List<double> contourNums = new List<double>();
            DA.GetDataList<double>(1, contourNums);
            string contourType = String.Empty;
            double unitConversion = Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);

            contourNums.Sort();
            for (int i = 0; i < contourNums.Count; i++)
            {
                double contourNum = contourNums[i];
                if (contourNum < 0.0)
                {
                    contourNum = 0.0;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Minimum countour must be greater than 0.");
                }

                if (MinutesOrMeters == "Distance")
                {
                    contourType = "contours_meters=";
                    contourNum = Math.Round(contourNum * unitConversion, 0);
                    if (contourNum < 1)
                    {
                        contourNum = 1;
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Minimum distance contour must be greater than the Rhino unit equivalent of 1 meter.");
                    }
                    if (contourNum > 100000)
                    {
                        contourNum = 100000;
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Maximum distance contour must be less than the Rhino unit equivalent of 1,000km.");
                    }
                }
                else
                {
                    contourType = "contours_minutes=";
                    if (contourNum > 60)
                    {
                        contourNum = 60;
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Maximum minute contour is 60.  Contour value reduced to 60.");
                    }
                }

                contourNums[i] = contourNum;
            }

            ///Setup denoise and make sure Denoise is between 0..1
            double? denoiseNum = null;
            DA.GetData(2, ref denoiseNum);
            if (denoiseNum < 0.0)
            {
                denoiseNum = 0.0;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Denoise must be greater than 0.");
            }
            if (denoiseNum > 1.0)
            {
                denoiseNum = 1.0;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Denoise must be less than 1.0");
            }

            ///Setup generalize and make sure it's greater than 0
            double? generalize = null;
            DA.GetData(3, ref generalize);
            double? generalizeNum = generalize * unitConversion;
            if (generalizeNum < 0.0)
            {
                generalizeNum = 0.0;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Generalize must be greater than 0");
            }

            ///Setup Mapbox token
            string apiKey = string.Empty;
            DA.GetData(4, ref apiKey);
            string mbToken = apiKey;
            if (mbToken == "")
            {
                string hmbToken = System.Environment.GetEnvironmentVariable("HERONMAPBOXTOKEN");
                if (hmbToken != null)
                {
                    mbToken = hmbToken;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using Mapbox token stored in Environment Variable HERONMAPBOXTOKEN.");
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No Mapbox token is specified.  Please get a valid token from mapbox.com");
                    return;
                }
            }

            ///Setup run
            bool run = false;
            DA.GetData(5, ref run);


            ///Outputs
            List<IGH_GeometricGoo> isochrones = new List<IGH_GeometricGoo>();
            List<string> urls = new List<string>();

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();

            ///Set transform from input spatial reference to Rhino spatial reference
            OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
            rhinoSRS.SetWellKnownGeogCS("WGS84");

            ///TODO: use this as override of global SetSRS
            OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
            //userSRS.SetFromUserInput(userSRStext);
            userSRS.SetFromUserInput("WGS84");

            ///These transforms move and scale in order to go from userSRS to XYZ and vice versa
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);


            ///Construct URL query string
            int increment = 0;
            while (increment < contourNums.Count)
            {
                string contourString = contourNums[increment].ToString();

                if (increment + 1 < contourNums.Count) { contourString = contourString + "%2C" + contourNums[increment + 1]; }
                if (increment + 2 < contourNums.Count) { contourString = contourString + "%2C" + contourNums[increment + 2]; }
                if (increment + 3 < contourNums.Count) { contourString = contourString + "%2C" + contourNums[increment + 3]; }

                string url = @"https://api.mapbox.com/isochrone/v1/mapbox/";
                if (!String.IsNullOrEmpty(Profile)) { url = url + Profile.ToLower() + "/"; }
                if (lon > -180.0 && lon <= 180.0) { url = url + lon + "%2C"; }
                if (lat > -90.0 && lat <= 90.0) { url = url + lat + "?"; }
                if (!String.IsNullOrEmpty(contourType)) { url = url + contourType.ToLower(); }
                if (contourString != null) { url = url + contourString; }
                if (polygons == "Meshes") { url = url + "&polygons=true"; }
                else { url = url + "&polygons=false"; }
                if (denoiseNum != null) { url = url + "&denoise=" + denoiseNum; }
                if (generalizeNum != null) { url = url + "&generalize=" + generalizeNum; }
                if (mbToken != "") { url = url + "&access_token=" + mbToken; }

                urls.Add(url);
                increment = increment + 4;
            }


            if (run)
            {
                foreach (string u in urls)
                {
                    List<IGH_GeometricGoo> gGoo = new List<IGH_GeometricGoo>();
                    OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(u, 0);

                    if (dataSource == null)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mapbox returned an invalid response.");
                        return;
                    }

                    ///Only interested in geometry, not fields or values
                    OSGeo.OGR.Layer ogrLayer = dataSource.GetLayerByIndex(0);
                    OSGeo.OGR.Feature feat;

                    while ((feat = ogrLayer.GetNextFeature()) != null)
                    {
                        if (feat.GetGeometryRef() == null)
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Isochrone may be too small or otherwise degenerate.");
                            continue;
                        }
                        OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();
                        ///reproject geometry to WGS84 and userSRS
                        ///TODO: look into using the SetCRS global variable here
                        if (geomUser.GetSpatialReference() == null) { geomUser.AssignSpatialReference(userSRS); }

                        geomUser.TransformTo(userSRS);
                        if (feat.GetGeometryRef() != null)
                        {
                            isochrones.AddRange(Heron.Convert.OgrGeomToGHGoo(geomUser, userSRSToModelTransform));
                        }
                    }
                    ///Garbage cleanup
                    ogrLayer.Dispose();
                    dataSource.Dispose();
                }
            }

            List<string> mbAtts = new List<string> { "© Mapbox, © OpenStreetMap", "https://www.mapbox.com/about/maps/", "http://www.openstreetmap.org/copyright" };

            DA.SetDataList(0, isochrones);
            DA.SetDataList(1, urls);
            DA.SetDataList(2, mbAtts);
        }



        //////////////////////////////////
        ///Menu Items
        ///


        private bool IsProfileSelected(string profileString)
        {
            return profileString.Equals(profile);
        }
        private bool IsMinutesOrMetersSelected(string minutesOrMetersString)
        {
            return minutesOrMetersString.Equals(minutesOrMeters);
        }

        private bool IsPolygonsSelected(string polygonString)
        {
            return polygonString.Equals(polygons);
        }
        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            MapboxIsochrone.Menu_AppendSeparator(menu);

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

            ///Add type of contour to menu
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

            ///Add polygon or linestring return value to menu
            ToolStripMenuItem polygonsMenu = new ToolStripMenuItem("Meshes or Polylines...");
            polygonsMenu.ToolTipText = "Minutes = Use time to calculate the isochrone. The times, in minutes, to use for each isochrone contour. " +
                "The maximum time that can be specified is 60 minutes. " +
                "Distance = Use distance to calculate the isochrone. The maximum distance that can be specified is Rhino unit equivalent of 100,000 meters (100km).";

            var polygonsList = new List<string> { "Meshes", "Polylines" };
            foreach (var p in polygonsList)
            {
                ToolStripMenuItem polygonsName = new ToolStripMenuItem(p);
                polygonsName.Checked = IsPolygonsSelected(p);
                polygonsName.Click += PolygonsOnClick;
                polygonsName.Tag = p;
                polygonsMenu.DropDownItems.Add(polygonsName);
            }

            menu.Items.Add(polygonsMenu);

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
            Message = Profile + " | " + MinutesOrMeters;
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
            Message = Profile + " | " + MinutesOrMeters;
            ExpireSolution(true);
        }

        private void PolygonsOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem polygonsItem = sender as ToolStripMenuItem;
            if (polygonsItem == null)
                return;

            string polygonsCode = (string)polygonsItem.Tag;
            if (IsMinutesOrMetersSelected(polygonsCode))
                return;

            RecordUndoEvent("Polygons");

            polygons = polygonsCode;
            ExpireSolution(true);
        }


        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private static string profile = "Walking";
        private static string minutesOrMeters = "Minutes";
        private static string polygons = "Polylines";


        public static string Profile
        {
            get { return profile; }
            set { profile = value; }
        }

        public static string MinutesOrMeters
        {
            get { return minutesOrMeters; }
            set { minutesOrMeters = value; }
        }

        public static string Polygons
        {
            get { return polygons; }
            set { polygons = value; }
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("Profile", Profile);
            writer.SetString("MinutesOrMeters", MinutesOrMeters);
            writer.SetString("Polygons", Polygons);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            Profile = reader.GetString("Profile");
            MinutesOrMeters = reader.GetString("MinutesOrMeters");
            Polygons = reader.GetString("Polygons");
            return base.Read(reader);
        }


        //////////////////////////////////

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
            get { return new Guid("fbfe3ed5-6629-44da-94a9-ba518f38b0f0"); }
        }
    }
}