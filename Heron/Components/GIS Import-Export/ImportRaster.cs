using Grasshopper.Kernel;
using OSGeo.GDAL;
using OSGeo.OSR;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.IO;



namespace Heron
{
    public class ImportRaster : HeronRasterPreviewComponent
    {
        //Class Constructor
        public ImportRaster() : base("Import Raster", "ImportRaster", "Import georeferenced raster data.", "GIS Import | Export")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for raster data", GH_ParamAccess.item);
            pManager.AddTextParameter("Raster Location", "rasterLocation", "File path for the raster data.", GH_ParamAccess.item);
            pManager.AddTextParameter("Clipped Location", "clippedLocation", "Output folder path for the clipped raster data and preview image.", GH_ParamAccess.item, Path.GetTempPath());
            pManager[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("rasterInfo", "rasterInfo", "List of information about the source dataset.", GH_ParamAccess.item);
            pManager.AddRectangleParameter("rasterExtent", "rasterExtent", "Bounding box for the raster data.", GH_ParamAccess.item);
            pManager.AddTextParameter("clippedRaster", "clippedRaster", "File path for the raster data clipped to the boundary.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve boundary = null;
            DA.GetData<Curve>(0, ref boundary);

            string sourceFileLocation = string.Empty;
            DA.GetData<string>(1, ref sourceFileLocation);

            string clippedLocation = string.Empty;
            DA.GetData<string>(2, ref clippedLocation);

            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();
            ///Specific settings for getting WMS images
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES");
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_SKIP", "WMS");

            ///Read in the raster data
            Dataset datasource = Gdal.Open(sourceFileLocation, Access.GA_ReadOnly);
            OSGeo.GDAL.Driver drv = datasource.GetDriver();

            string srcInfo = string.Empty;

            if (datasource == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The raster datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                return;
            }


            ///Get the spatial reference of the input raster file and set to WGS84 if not known
            ///Set up transform from source to WGS84
            OSGeo.OSR.SpatialReference sr = new SpatialReference(Osr.SRS_WKT_WGS84_LAT_LONG);

            if (datasource.GetProjection() == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) is missing.  SRS set automatically set to WGS84.");
            }

            else
            {
                sr = new SpatialReference(datasource.GetProjection());

                if (sr.Validate() != 0)
                {
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
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) is unknown or unsupported.  SRS assumed to be WGS84." +
                            "Try setting the SRS with the GdalWarp component using -t_srs EPSG:4326 for the option input.");
                        sr.SetWellKnownGeogCS("WGS84");
                    }

                }
                else { AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Data source SRS: EPSG:" + sr.GetAttrValue("AUTHORITY", 1)); }
            }

            ///Get info about image
            List<string> infoOptions = new List<string> {
                    "-stats"
                    };
            srcInfo = Gdal.GDALInfo(datasource, new GDALInfoOptions(infoOptions.ToArray()));

            ///Get HeronSRS
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + heronSRSInt;

            ///Apply EAP to HeronSRS
            Transform heronToUserSRSTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
            Transform userSRSToHeronTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);

            //OSGeo.OSR.SpatialReference dst = new OSGeo.OSR.SpatialReference("");
            //dst.SetWellKnownGeogCS("WGS84");
            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(sr, heronSRS);
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(heronSRS, sr);

            double[] adfGeoTransform = new double[6];
            double[] invTransform = new double[6];
            datasource.GetGeoTransform(adfGeoTransform);
            Gdal.InvGeoTransform(adfGeoTransform, invTransform);
            Band band = datasource.GetRasterBand(1);

            int width = datasource.RasterXSize;
            int height = datasource.RasterYSize;

            ///Dataset bounding box
            double oX = adfGeoTransform[0] + adfGeoTransform[1] * 0 + adfGeoTransform[2] * 0;
            double oY = adfGeoTransform[3] + adfGeoTransform[4] * 0 + adfGeoTransform[5] * 0;
            double eX = adfGeoTransform[0] + adfGeoTransform[1] * width + adfGeoTransform[2] * height;
            double eY = adfGeoTransform[3] + adfGeoTransform[4] * width + adfGeoTransform[5] * height;

            ///Transform to WGS84. 
            ///TODO: Allow for UserSRS
            double[] extMinPT = new double[3] { oX, eY, 0 };
            double[] extMaxPT = new double[3] { eX, oY, 0 };
            coordTransform.TransformPoint(extMinPT);
            coordTransform.TransformPoint(extMaxPT);
            Point3d dsMin = new Point3d(extMinPT[0], extMinPT[1], extMinPT[2]);
            Point3d dsMax = new Point3d(extMaxPT[0], extMaxPT[1], extMaxPT[2]);

            ///Get bounding box for entire raster data
            //Rectangle3d datasourceBBox = new Rectangle3d(Plane.WorldXY, Heron.Convert.WGSToXYZ(dsMin), Heron.Convert.WGSToXYZ(dsMax));
            dsMin.Transform(heronToUserSRSTransform);
            dsMax.Transform(heronToUserSRSTransform);
            Rectangle3d datasourceBBox = new Rectangle3d(Plane.WorldXY, dsMin, dsMax);

            ///https://gis.stackexchange.com/questions/312440/gdal-translate-bilinear-interpolation
            ///set output to georeferenced tiff as a catch-all

            string clippedRasterFile = clippedLocation + Path.GetFileNameWithoutExtension(sourceFileLocation) + "_clipped.tif";
            string previewPNG = clippedLocation + Path.GetFileNameWithoutExtension(sourceFileLocation) + "_preview.png";
            if (sourceFileLocation.Contains("http://") || sourceFileLocation.Contains("https://"))
            {
                previewPNG = clippedLocation + "ImportRaster_preview.png";
                clippedRasterFile = clippedLocation + "ImportRaster_clipped.tif";
            }

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Original Resolution: " + datasource.RasterXSize.ToString() + "x" + datasource.RasterYSize.ToString());

            if (boundary != null)
            {

                Point3d clipperMin = boundary.GetBoundingBox(true).Corner(true, false, true);
                Point3d clipperMax = boundary.GetBoundingBox(true).Corner(false, true, true);
                clipperMin.Transform(userSRSToHeronTransform);
                clipperMax.Transform(userSRSToHeronTransform);


                double lonWest = clipperMin.X;
                double lonEast = clipperMax.X;
                double latNorth = clipperMin.Y;
                double latSouth = clipperMax.Y;

                ///GDALTranslate should also be its own component with full control over options
                var translateOptions = new[]
                {
                    "-of", "GTiff",
                    //"-a_nodata", "0",
                    "-projwin_srs", HeronSRS.Instance.SRS,
                    "-projwin", $"{lonWest}", $"{latNorth}", $"{lonEast}", $"{latSouth}"
                };

                using (Dataset clippedDataset = Gdal.wrapper_GDALTranslate(clippedRasterFile, datasource, new GDALTranslateOptions(translateOptions), null, null))
                {
                    Dataset previewDataset = Gdal.wrapper_GDALTranslate(previewPNG, clippedDataset, null, null, null);

                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clipped Resolution: " + clippedDataset.RasterXSize.ToString() + "x" + datasource.RasterYSize.ToString());

                    ///clean up
                    clippedDataset.FlushCache();
                    clippedDataset.Dispose();
                    previewDataset.FlushCache();
                    previewDataset.Dispose();

                    AddPreviewItem(previewPNG, BBoxToRect(boundary.GetBoundingBox(true)));
                }

            }

            else
            {
                Dataset previewDataset = Gdal.wrapper_GDALTranslate(previewPNG, datasource, null, null, null);

                ///clean up
                previewDataset.FlushCache();
                previewDataset.Dispose();

                AddPreviewItem(previewPNG, datasourceBBox);
            }

            ///clean up
            datasource.FlushCache();
            datasource.Dispose();

            DA.SetData(0, srcInfo);
            DA.SetData(1, datasourceBBox);
            DA.SetData(2, clippedRasterFile);

        }



        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.raster;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{6834C93A-5FC3-40AE-A7C3-153E96232990}"); }
        }
    }
}
