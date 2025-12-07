using System;
using System.Collections.Generic;
using System.Runtime;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Grasshopper.Kernel;
using Rhino.Geometry;

using OSGeo.GDAL;
using OSGeo.OSR;
using OSGeo.OGR;
using System.Reflection;

namespace Heron
{
    public class GdalGrid : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the GdalTranslate class.
        /// </summary>
        public GdalGrid()
          : base("GdalGrid", "GdalGrid",
              "This component creates a regular grid (raster) from the scattered data read from the vector datasource. " +
                "Input data will be interpolated to fill grid nodes with values, you can choose from various interpolation methods.  " +
                "Similar to the command line version, formatting for the list of options should be a single string of text with a space separating each term " +
                "where '-' should preceed the option parameter and the next item in the list should be that parameter's value.  " +
                "More information about conversion options can be found at https://gdal.org/en/stable/programs/gdal_grid.html.",
              "GIS Tools")
        {
        }

        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.tertiary; }
        }
        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Source datasource", "source", "File location for the source vector data.", GH_ParamAccess.item);
            pManager.AddTextParameter("Destination dataset", "dest", "File location for the destination raster data.", GH_ParamAccess.item);
            pManager.AddTextParameter("Options", "options", "String of options with a space separating each term. " +
                "For instance, to output a GeoTiff, the options string would be '-of GTiff'.  Or to set the pixel dimension of 500 by 500, use '-outsize 500 500'.", GH_ParamAccess.item);
            pManager[2].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Destination Info", "destInfo", "List of information about the destination datasource.", GH_ParamAccess.item);
            pManager.AddTextParameter("Destination File", "destFile", "File location of destination datasource.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string srcFileLocation = string.Empty;
            DA.GetData<string>(0, ref srcFileLocation);

            string dstFileLocation = string.Empty;
            DA.GetData<string>(1, ref dstFileLocation);

            string options = string.Empty;
            DA.GetData<string>(2, ref options);

            var re = new System.Text.RegularExpressions.Regex("(?<=\")[^\"]*(?=\")|[^\" ]+");
            string[] gridOptions = re.Matches(options).Cast<Match>().Select(m => m.Value).ToArray();

            string srcInfo = string.Empty;
            string dstInfo = string.Empty;
            string dstOutput = string.Empty;

            Heron.GdalConfiguration.ConfigureGdal();
            OSGeo.OGR.Ogr.RegisterAll();
            Heron.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Look here for more information about options:");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "https://gdal.org/en/stable/programs/gdal_grid.html");

            if (!string.IsNullOrEmpty(srcFileLocation))
            {
                using (Dataset src = Gdal.OpenEx(srcFileLocation, 0, null, null, null))
                {
                    if (src == null)
                    {
                        throw new Exception("Can't open source dataset: " + srcFileLocation);
                    }

                    if (!string.IsNullOrEmpty(dstFileLocation))
                    {
                        if (File.Exists(dstFileLocation))
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "The destination datasource already existed and has been overwritten.");
                        }

                        Dataset dst = Gdal.wrapper_GDALGrid(dstFileLocation, src, new GDALGridOptions(gridOptions), null, null);
                        dstInfo = Gdal.GDALInfo(dst, new GDALInfoOptions(null));
                        dst.Dispose();
                        dstOutput = dstFileLocation;
                    }

                    src.Dispose();
                }
            }

            DA.SetData(0, dstInfo);
            DA.SetData(1, dstOutput);
        }



        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.raster;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("6B386576-B132-44D4-B674-CC0A8C619BBF"); }
        }
    }
}