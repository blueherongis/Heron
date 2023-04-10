using Grasshopper.Kernel;
using OSGeo.GDAL;
using System;
using System.IO;

namespace Heron
{
    public class GdalFillNoData : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the GdalTranslate class.
        /// </summary>
        public GdalFillNoData()
          : base("Gdal Fill No Data", "GFND",
              "Fill raster regions that have no data by interpolation from edges with the GDAL FillNoData program. " +
                "More information can be found at https://gdal.org/programs/gdal_fillnodata.html.",
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
            pManager.AddTextParameter("Destination dataset", "D", "File location for the destination dataset.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Max Distance", "MD", "The maximum distance (in pixels) that the algorithm will search out for values to interpolate. " +
                "The default is 100 pixels.", GH_ParamAccess.item, 100);
            pManager.AddIntegerParameter("Smooth Iterations", "SI", "The number of 3x3 average filter smoothing iterations to run after the interpolation to dampen artifacts. " +
                "The default is zero smoothing iterations.", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Band", "B", "The band to operate on, by default the first band is operated on.", GH_ParamAccess.item, 1);
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Destination File", "D", "File location of destination datasource.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string datasourceFileLocation = string.Empty;
            DA.GetData<string>(0, ref datasourceFileLocation);

            string dstFileLocation = string.Empty;
            DA.GetData<string>(1, ref dstFileLocation);

            int maxDistance = 100;
            DA.GetData<int>(2, ref maxDistance);

            int smoothIterations = 0;
            DA.GetData<int>(3, ref smoothIterations);

            int band = 1;
            DA.GetData<int>(4, ref band);

            string dstOutput = string.Empty;


            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Look for more information about options at:");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "https://gdal.org/programs/gdal_fillnodata.html");

            if (!string.IsNullOrEmpty(datasourceFileLocation))
            {
                using (Dataset datasource = Gdal.Open(datasourceFileLocation, Access.GA_ReadOnly))
                {
                    if (datasource == null)
                    {
                        throw new Exception("Can't open GDAL dataset: " + datasourceFileLocation);
                    }

                    if (!string.IsNullOrEmpty(dstFileLocation))
                    {
                        if(File.Exists(dstFileLocation))
                        {
                            File.Delete(dstFileLocation);
                        }
                        OSGeo.GDAL.Driver drv = datasource.GetDriver();
                        Dataset dst = drv.CreateCopy(dstFileLocation, datasource, 0, null, null, null);
                        int filled = Gdal.FillNodata(dst.GetRasterBand(band), null, (double)maxDistance, smoothIterations, null, null, null);
                        dst.Dispose();
                        dstOutput = dstFileLocation;
                    }
                    datasource.Dispose();
                }
            }

            DA.SetData(0, dstOutput);

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
            get { return new Guid("0E773376-BB1B-42DA-8366-D6273BDF8755"); }
        }
    }
}
