using System;
using System.Collections.Generic;
using System.Runtime;

using Grasshopper.Kernel;
using Rhino.Geometry;

using OSGeo.GDAL;
using OSGeo.OSR;
using OSGeo.OGR;

namespace Heron
{
    public class GdalTranslate : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the GdalTranslate class.
        /// </summary>
        public GdalTranslate()
          : base("GdalTranslate", "GdalTranslate",
              "Manipulate raster data with the GDAL Translate program given a source dataset, a destination dataset and a list of options.  " +
                "Formatting for the list of options should be a single string of text with a space separating each term " +
                "where '-' should preceed the option parameter and the next item in the list should be that parameter's value.  " +
                "For instance, to convert raster data to PNG format the options string would be '-of PNG'.  To clip a large raster data set " +
                "to a boundary of upper left x (ulx), upper left y (uly), lower right x (lrx) and lower right y (lry), the options string " +
                "would be '-projwin ulx uly lrx lry' where ulx, uly, lrx and lry are substituted with coordinate values.  " +
                "More information about translate options can be found at https://gdal.org/programs/gdal_translate.html.",
              "GIS Tools")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Source dataset", "src", "File location for the source raster dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Destination dataset", "dst", "File location for the destination dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Options", "options", "String of options with a space separating each term. " +
                "For instance, to convert raster data to PNG format the Options string would be '-of PNG'.", GH_ParamAccess.item);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Source Info", "srcInfo", "List of information about the source dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Destination Info", "dstInfo", "List of information about the destination dataset.", GH_ParamAccess.item);
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

            string[] translateOptions = options.Split(' ');

            string srcInfo = string.Empty;
            string dstInfo = string.Empty;

            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();
            ///Specific settings for getting WMS images
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES");
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_SKIP", "WMS");

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Look for more information about options at:");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "https://gdal.org/programs/gdal_translate.html");

            if (!string.IsNullOrEmpty(srcFileLocation))
            {
                Dataset src = Gdal.Open(srcFileLocation, Access.GA_ReadOnly);
                srcInfo = Gdal.GDALInfo(src, null);

                if (!string.IsNullOrEmpty(dstFileLocation))
                {
                    Dataset dst = Gdal.wrapper_GDALTranslate(dstFileLocation, src, new GDALTranslateOptions(translateOptions), null, null);
                    dstInfo = Gdal.GDALInfo(dst, null);
                    dst.Dispose();
                }
                src.Dispose();
            }

            DA.SetData(0, srcInfo);
            DA.SetData(1, dstInfo);
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
            get { return new Guid("c9056659-a5f8-4cc0-89f3-0e2f22b08afc"); }
        }
    }
}