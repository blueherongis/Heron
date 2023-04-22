using System;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.InteropServices;
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
    public class GdalWarp : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the GdalTranslate class.
        /// </summary>
        public GdalWarp()
          : base("GdalWarp", "GdalWarp",
              "Manipulate raster data with the GDAL Warp program given a source dataset, a destination dataset and a list of options.  " +
                "Formatting for the list of options should be a single string of text with a space separating each term " +
                "where '-' should preceed the option parameter and the next item in the list should be that parameter's value.  " +
                "For instance, to warp a raster dataset to WGS84, the options string would be '-t_srs EPSG:4326'  " +
                "More information about warp options can be found at https://gdal.org/programs/gdalwarp.html.",
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
            pManager.AddTextParameter("Source dataset", "source", "File location for the source raster dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Destination dataset", "dest", "File location for the destination dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Options", "options", "String of options with a space separating each term. " +
                "For instance, to warp an dataset to WGS84, the options string would be '-t_srs EPSG:4326'.", GH_ParamAccess.item);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Source Info", "sourceInfo", "List of information about the source dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Destination Info", "destInfo", "List of information about the destination dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Destination File", "destFile", "File location of destination datasource.", GH_ParamAccess.item);
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

            string options = string.Empty;
            DA.GetData<string>(2, ref options);

            var re = new System.Text.RegularExpressions.Regex("(?<=\")[^\"]*(?=\")|[^\" ]+");
            string[] translateOptions = re.Matches(options).Cast<Match>().Select(m => m.Value).ToArray();

            string datasourceInfo = string.Empty;
            string dstInfo = string.Empty;
            string dstOutput = string.Empty;


            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Look for more information about options at:");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "https://gdal.org/programs/gdalwarp.html");

            if (!string.IsNullOrEmpty(datasourceFileLocation))
            {
                using (Dataset datasource = Gdal.Open(datasourceFileLocation, Access.GA_ReadOnly))
                {
                    if (datasource == null)
                    {
                        throw new Exception("Can't open GDAL dataset: " + datasourceFileLocation);
                    }

                    SpatialReference sr = new SpatialReference(datasource.GetProjection());

                    ///Check if SRS needs to be converted from ESRI format to WKT to avoid error:
                    ///"No translation for Lambert_Conformal_Conic to PROJ.4 format is known."
                    ///https://gis.stackexchange.com/questions/128266/qgis-error-6-no-translation-for-lambert-conformal-conic-to-proj-4-format-is-kn
                    SpatialReference srEsri = sr;
                    srEsri.MorphFromESRI();
                    string projEsri = string.Empty;
                    srEsri.ExportToWkt(out projEsri, null);

                    ///If no SRS exists, check Ground Control Points SRS
                    SpatialReference srGCP = new SpatialReference(datasource.GetGCPProjection());
                    string projGCP = string.Empty;
                    srGCP.ExportToWkt(out projGCP, null);

                    if (!string.IsNullOrEmpty(projEsri))
                    {
                        datasource.SetProjection(projEsri);
                        sr = srEsri;
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) morphed form ESRI format.");
                    }
                    else if (!string.IsNullOrEmpty(projGCP))
                    {
                        datasource.SetProjection(projGCP);
                        sr = srGCP;
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) set from Ground Control Points (GCPs).");
                    }
                    else
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) is unknown or unsupported.  " +
                            "Try setting the SRS with the GdalWarp component using -t_srs EPSG:4326 for the option input.");
                        //sr.SetWellKnownGeogCS("WGS84");
                    }

                    ///Get info about image
                    List<string> infoOptions = new List<string> {
                    "-stats"
                    };
                    datasourceInfo = Gdal.GDALInfo(datasource, new GDALInfoOptions(infoOptions.ToArray()));

                    if (!string.IsNullOrEmpty(dstFileLocation))
                    {
                        if (string.IsNullOrEmpty(options) && File.Exists(dstFileLocation))
                        {
                            Dataset dst = Gdal.Open(dstFileLocation, Access.GA_ReadOnly);
                            dstInfo = Gdal.GDALInfo(dst, null);
                            dst.Dispose();
                            dstOutput = dstFileLocation;
                        }
                        else
                        {
                            ///https://github.com/OSGeo/gdal/issues/813
                            ///https://lists.osgeo.org/pipermail/gdal-dev/2017-February/046046.html
                            ///Odd way to go about setting source dataset in parameters for Warp is a known issue

                            var ptr = new[] { Dataset.getCPtr(datasource).Handle };
                            var gcHandle = GCHandle.Alloc(ptr, GCHandleType.Pinned);
                            try
                            {
                                //var dss = new SWIGTYPE_p_p_GDALDatasetShadow(gcHandle.AddrOfPinnedObject(), false, null);
                                //Dataset dst = Gdal.wrapper_GDALWarpDestName(dstFileLocation, 1, dss, new GDALWarpAppOptions(translateOptions), null, null);
                                Dataset[] dss = new Dataset[1];
                                dss[0] = Gdal.Open(datasourceFileLocation, Access.GA_ReadOnly);
                                Dataset dst = Gdal.Warp(dstFileLocation, dss, new GDALWarpAppOptions(translateOptions), null, null);
                                if (dst == null)
                                {
                                    throw new Exception("GdalWarp failed: " + Gdal.GetLastErrorMsg());
                                }

                                dstInfo = Gdal.GDALInfo(dst, new GDALInfoOptions(infoOptions.ToArray()));
                                dst.Dispose();
                                dstOutput = dstFileLocation;
                            }
                            finally
                            {
                                if (gcHandle.IsAllocated)
                                    gcHandle.Free();
                            }

                        }
                    }
                    datasource.Dispose();
                }
            }

            DA.SetData(0, datasourceInfo);
            DA.SetData(1, dstInfo);
            DA.SetData(2, dstOutput);

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
            get { return new Guid("7C3A7B33-7647-4DB9-A193-F31803974BBF"); }
        }
    }
}