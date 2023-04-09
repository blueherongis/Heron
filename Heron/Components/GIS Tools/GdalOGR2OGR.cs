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
    public class GdalOgr2Ogr : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the GdalTranslate class.
        /// </summary>
        public GdalOgr2Ogr()
          : base("GdalOgr2Ogr", "GdalOgr2Ogr",
              "Manipulate vector data with the GDAL OGR2OGR program given a source dataset, a destination dataset and a list of options.  " +
                "Similar to the command line version, formatting for the list of options should be a single string of text with a space separating each term " +
                "where '-' should preceed the option parameter and the next item in the list should be that parameter's value.  " +
                "For instance, to convert a Shapefile to GeoJSON format the options string would be '-f geojson'.  To clip a large vector data set " +
                "to a boundary of upper left x (ulx), upper left y (uly), lower right x (lrx) and lower right y (lry), the options string " +
                "would be '-spat ulx uly lrx lry' where ulx, uly, lrx and lry are substituted with coordinate values.  " +
                "More information about conversion options can be found at https://gdal.org/programs/ogr2ogr.html.",
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
            pManager.AddTextParameter("Destination datasource", "dest", "File location for the destination vector data.", GH_ParamAccess.item);
            pManager.AddTextParameter("Options", "options", "String of options with a space separating each term. " +
                "For instance, to convert vector data from Shapefile format to GeoJSON, the options string would be '-f geojson'.", GH_ParamAccess.item);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Source Info", "sourceInfo", "List of information about the source datasource.", GH_ParamAccess.item);
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
            string[] ogr2ogrOptions = re.Matches(options).Cast<Match>().Select(m => m.Value).ToArray();

            string srcInfo = string.Empty;
            string dstInfo = string.Empty;
            string dstOutput = string.Empty;

            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.OGR.Ogr.RegisterAll();

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Look here for more information about options:");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "https://gdal.org/programs/ogr2ogr.html");

            if (!string.IsNullOrEmpty(srcFileLocation))
            {
                using (Dataset src = Gdal.OpenEx(srcFileLocation, 0, null, null, null))
                {
                    if (src == null)
                    {
                        throw new Exception("Can't open GDAL dataset: " + srcFileLocation);
                    }

                    if (!string.IsNullOrEmpty(dstFileLocation))
                    {
                        if (File.Exists(dstFileLocation))
                        {
                            if (options.Contains("-overwrite") || options.Contains("-append"))
                            {
                                if (options.Contains("-overwrite")) { File.Delete(dstFileLocation); }
                                Dataset dst = Gdal.wrapper_GDALVectorTranslateDestName(dstFileLocation, src, new GDALVectorTranslateOptions(ogr2ogrOptions), null, null);
                                dst.Dispose();
                                dstInfo = string.Join("\r\n", OGRInfo(dstFileLocation));
                                dstOutput = dstFileLocation;
                            }
                            else
                            {
                                dstInfo = string.Join("\r\n", OGRInfo(dstFileLocation));
                                dstOutput = dstFileLocation;
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, dstFileLocation + " already exists. Include '-overwrite' in options to replace it.");
                            }
                        }
                        else
                        {
                            Dataset dst = Gdal.wrapper_GDALVectorTranslateDestName(dstFileLocation, src, new GDALVectorTranslateOptions(ogr2ogrOptions), null, null);
                            dst.Dispose();
                            dstInfo = string.Join("\r\n", OGRInfo(dstFileLocation));
                            dstOutput = dstFileLocation;
                        }

                    }

                    src.Dispose();
                    srcInfo = string.Join("\r\n", OGRInfo(srcFileLocation));
                }
            }

            DA.SetData(0, srcInfo);
            DA.SetData(1, dstInfo);
            DA.SetData(2, dstOutput);
        }


        public static List<string> OGRInfo (string datasourceFileLocation)
        {
            List<string> info = new List<string>();
            Ogr.RegisterAll();
            DataSource ds = Ogr.Open(datasourceFileLocation, 0);
            if (ds == null)
            {
                info.Add("Couldn not open vector data source.");
                return info;
            }

            OSGeo.OGR.Driver drv = ds.GetDriver();
            if (drv == null)
            {
                info.Add("Could not find driver to open vector data source.");
                return info;
            }

            info.Add("Using driver: " + drv.GetName());

            ///Iterating through layers
            for (int iLayer = 0; iLayer < ds.GetLayerCount(); iLayer++)
            {
                Layer layer = ds.GetLayerByIndex(iLayer);
                if (layer == null)
                {
                    info.Add("Could not find layers in the vector data source.");
                    return info;
                }
                FeatureDefn def = layer.GetLayerDefn();
                info.Add("Layer name: " + def.GetName());
                info.Add("Feature count: " + layer.GetFeatureCount(1));
                Envelope ext = new Envelope();
                layer.GetExtent(ext, 1);
                info.Add("Extent: " + ext.MinX + ", " + ext.MinY + ", " + ext.MaxX + ", " + ext.MaxY);

                ///Reading the spatial reference
                OSGeo.OSR.SpatialReference sr = layer.GetSpatialRef();
                string srs_wkt = string.Empty;
                if (sr != null) { sr.ExportToPrettyWkt(out srs_wkt, 1); }
                else { srs_wkt = "(unknow)"; }
                info.Add("Layer SRS WKT: " + srs_wkt);

                ///Reading the fields
                info.Add("Field Names (type): ");
                for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
                {
                    FieldDefn fdef = def.GetFieldDefn(iAttr);
                    info.Add(fdef.GetName() + " (" +
                        fdef.GetFieldTypeName(fdef.GetFieldType()) + ")");
                }
            }
            ds.Dispose();

            return info;
        }

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
            get { return new Guid("A8639E93-009D-4094-87E6-D34959C73849"); }
        }
    }
}