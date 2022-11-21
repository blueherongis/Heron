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

namespace Heron
{
    public class GdalInfo : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the GdalTranslate class.
        /// </summary>
        public GdalInfo()
          : base("Gdal Info", "GI",
              "Gdalinfo program lists various information about a GDAL supported raster dataset. " +
                "More information about Gdalinfo options can be found at https://gdal.org/programs/gdalinfo.html.",
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
            pManager.AddTextParameter("Source dataset", "S", "File location for the source raster dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Options", "O", "String of options with a space separating each term. " +
                "For instance, to report histogram information for all bands, the options string would be '-hist'.", GH_ParamAccess.item);
            pManager[1].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Source Info", "I", "List of information about the source dataset.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string datasourceFileLocation = string.Empty;
            DA.GetData<string>(0, ref datasourceFileLocation);

            string options = string.Empty;
            DA.GetData<string>(1, ref options);

            var re = new System.Text.RegularExpressions.Regex("(?<=\")[^\"]*(?=\")|[^\" ]+");
            string[] infoOptions = re.Matches(options).Cast<Match>().Select(m => m.Value).ToArray();

            string datasourceInfo = string.Empty;
            string dstInfo = string.Empty;
            string dstOutput = string.Empty;

            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();
            ///Specific settings for getting WMS images
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES");
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_SKIP", "WMS");

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Look for more information about options at:");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "https://gdal.org/programs/gdalinfo.html");

            if (!string.IsNullOrEmpty(datasourceFileLocation))
            {
                //using (Dataset datasource = Gdal.OpenEx(datasourceFileLocation,0, null,null,null))
                using (Dataset datasource = Gdal.Open(datasourceFileLocation, Access.GA_ReadOnly))
                {
                    if (datasource == null)
                    {
                        throw new Exception("Can't open GDAL dataset: " + datasourceFileLocation);
                    }

                    datasourceInfo = Gdal.GDALInfo(datasource, new GDALInfoOptions(infoOptions.ToArray()));
                    datasource.Dispose();
                }
            }

            DA.SetData(0, datasourceInfo);
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
            get { return new Guid("35AF7648-AAAD-464F-8343-B7DA339788FB"); }
        }
    }
}