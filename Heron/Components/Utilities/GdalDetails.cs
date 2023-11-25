using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Gdal = OSGeo.GDAL.Gdal;
using Ogr = OSGeo.OGR.Ogr;
using System.Linq;

using Grasshopper;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Heron
{
    public class GdalDetails : HeronComponent
    {
        /// <summary>
        /// Each implementation of GH_Component must provide a public 
        /// constructor without any arguments.
        /// Category represents the Tab in which the component will appear, 
        /// Subcategory the panel. If you use non-existing tab or panel names, 
        /// new tabs/panels will automatically be created.
        /// </summary>
        public GdalDetails()
          : base("GDAL Details", "GD",
            "This component enumerates the current version of GDAL and it's available raster and vector drivers.",
            "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("D", "GDAL Details", "", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object can be used to retrieve data from input parameters and 
        /// to store data in output parameters.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var info = new List<string>();

            Heron.GdalConfiguration.ConfigureGdal();
            Heron.GdalConfiguration.ConfigureOgr();

            string gdalVersion = Gdal.VersionInfo("");
            info.Add("Gdal Version: " + gdalVersion);

            string heronVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            info.Add("Heron Version: " + heronVersion);
            string executingAssemblyFileMac = new Uri(Assembly.GetExecutingAssembly().GetName().CodeBase).LocalPath;
            info.Add("Heron Location: " + executingAssemblyFileMac);

            string executingDirectory = Path.GetDirectoryName(executingAssemblyFileMac);


            string osxPlatform = "";
            var arch = RuntimeInformation.ProcessArchitecture;
            var isOSX = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            if (arch == Architecture.X64 && isOSX == true) { osxPlatform = "osx-64"; }
            if (arch == Architecture.Arm64 && isOSX == true) { osxPlatform = "osx-Arm64"; }

            string gdalPath = Path.Combine(executingDirectory, "gdal");
            string nativePath = Path.Combine(gdalPath, osxPlatform);

            //info.Add("Gdal Path: " + gdalPath);
            //info.Add(nativePath);

            string envGdalDriverPath = Environment.GetEnvironmentVariable("GDAL_DRIVER_PATH");
            //info.Add("GDAL Driver Path: " + envGdalDriverPath);

            ///Add Gdal driver info
            Gdal.AllRegister();
            var driverCount = Gdal.GetDriverCount();
            List<string> gdrivers = new List<string>();
            info.Add("----------");
            info.Add("GDAL drivers (" + driverCount + "):");

            for (int drv = 0; drv < Gdal.GetDriverCount(); drv++)
            {
                gdrivers.Add(Gdal.GetDriver(drv).ShortName + " (" + Gdal.GetDriver(drv).LongName + ")");
            }
            gdrivers.Sort();
            info.AddRange(gdrivers);

            ///Add Ogr driver info
            Ogr.RegisterAll();
            var ogrDriverCount = Ogr.GetDriverCount();
            List<string> odrivers = new List<string>();
            info.Add("----------");
            info.Add("OGR drivers (" + ogrDriverCount+ ") :");
            for (int odrv = 0; odrv < Ogr.GetDriverCount(); odrv++)
            {
                odrivers.Add(Ogr.GetDriver(odrv).GetName());
            }
            odrivers.Sort();
            info.AddRange(odrivers);

            ///Get Environment Variables for troublshooting
            var d = Environment.GetEnvironmentVariables();
            List<string> ks = Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList();
            ks.Sort();
            //info.Add("----------");
            //info.Add("Environment Variables:");
            foreach (var key in ks)
            {
                string k = key;
                string v = (string)d[key];
                //info.Add(k + " : " + v);
            }


            DA.SetDataList(0, info);
        }

        /// <summary>
        /// Provides an Icon for every component that will be visible in the User Interface.
        /// Icons need to be 24x24 pixels.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                // You can add image files to your project resources and access them like this:
                //return Resources.IconForThisComponent;
                return Properties.Resources.heron_favicon;
            }
        }

        /// <summary>
        /// Each component must have a unique Guid to identify it. 
        /// It is vital this Guid doesn't change otherwise old ghx files 
        /// that use the old ID will partially fail during loading.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("bf25d013-6042-4b5d-a20f-8ba37c96a24c"); }
        }
    }
}
