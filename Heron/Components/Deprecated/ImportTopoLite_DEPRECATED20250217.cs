using Grasshopper.Kernel;
using OSGeo.GDAL;
using OSGeo.OSR;
using Rhino.Geometry;
using System;
using System.Collections.Generic;


namespace Heron
{
    public class ImportTopoLite_DEPRECATED20250217_OBSOLETE : HeronComponent
    {
        //Class Constructor
        public ImportTopoLite_DEPRECATED20250217_OBSOLETE() : base("Import Topo Lite", "ITL", "This is a basic version of the Import Topo component " +
            "which creates a topographic mesh from a raster file (IMG, HGT, ASCII, DEM, TIF, etc).  The data will be imported in the source's units " +
            "and spatial reference system, Heron's SetSRS and Rhino's EarthAnchorPoint will have no effect and the data could be far from Rhino's origin.  " +
            "Furthermore, a source's vertical units are typically in meters, so geodetic coordinate systems like WGS84 where horizontal units are in " +
            "degrees, will produce a wildly scaled mesh in the z dimension.  It is recommended to use a projected coordinate system like UTM.  " +
            "You can reproject your source's data from a geodetic to projected coordinate system using the GdalWarp component.", "GIS Import | Export")
        {

        }
        ///Retiring this component to add clipping curve input 
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.hidden; }
        }
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Topography Raster File", "topoFile", "Filepath for the raster topography input", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Topography Mesh", "topoMesh", "Resultant topographic mesh from the source file", GH_ParamAccess.item);
            pManager.AddRectangleParameter("Topography Extent", "topoExtent", "Bounding box for the entire source file", GH_ParamAccess.item);
            pManager.AddTextParameter("Topography Source Info", "topoInfo", "Raster info about topography source", GH_ParamAccess.item);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string IMG_file = string.Empty;
            DA.GetData<string>(0, ref IMG_file);

            Heron.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();

            Dataset datasource = Gdal.Open(IMG_file, Access.GA_ReadOnly);
            OSGeo.GDAL.Driver drv = datasource.GetDriver();


            if (datasource == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                return;
            }

            ///Get info about image
            string srcInfo = string.Empty;
            List<string> infoOptions = new List<string> {
                    "-stats"
                    };
            srcInfo = Gdal.GDALInfo(datasource, new GDALInfoOptions(infoOptions.ToArray()));


            ///Get the spatial reference of the input raster file
            OSGeo.OSR.SpatialReference sr = new SpatialReference(Osr.SRS_WKT_WGS84_LAT_LONG);
            if (datasource.GetProjection() == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Coordinate Reference System (CRS) is missing.  Failed to create a mesh.");
                return;
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
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Spatial Reference System (SRS) is unknown or unsupported.  " +
                            "Failed to create a mesh.");
                        return;
                    }
                }

                else
                {
                    //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Data source SRS: EPSG:" + sr.GetAttrValue("AUTHORITY", 1));
                }
            }


            double[] adfGeoTransform = new double[6];
            datasource.GetGeoTransform(adfGeoTransform);

            int width = datasource.RasterXSize;
            int height = datasource.RasterYSize;

            ///Dataset bounding box.  Get before using statement in case using fails.
            double oX = adfGeoTransform[0] + adfGeoTransform[1] * 0 + adfGeoTransform[2] * 0;
            double oY = adfGeoTransform[3] + adfGeoTransform[4] * 0 + adfGeoTransform[5] * 0;
            double eX = adfGeoTransform[0] + adfGeoTransform[1] * width + adfGeoTransform[2] * height;
            double eY = adfGeoTransform[3] + adfGeoTransform[4] * width + adfGeoTransform[5] * height;

            Point3d dsMin = new Point3d(oX, eY, 0);
            Point3d dsMax = new Point3d(eX, oY, 0);

            Rectangle3d dsbox = new Rectangle3d(Plane.WorldXY, dsMin, dsMax);


            ///Declare trees
            string clippedTopoFile = "/vsimem/topoclipped.tif";
            Mesh mesh = new Mesh();

            var translateOptions = new[]
            {
                    "-of", "GTiff",
                    "-a_nodata", "0", ///Fill no data values with 0 to avoid causing errors in creating the mesh
            };

            using (Dataset clippedDataset = Gdal.wrapper_GDALTranslate(clippedTopoFile, datasource, new GDALTranslateOptions(translateOptions), null, null))
            {
                Band band = clippedDataset.GetRasterBand(1);
                width = clippedDataset.RasterXSize;
                height = clippedDataset.RasterYSize;
                clippedDataset.GetGeoTransform(adfGeoTransform);

                List<Point3d> verts = new List<Point3d>();

                double[] bits = new double[width * height];
                band.ReadRaster(0, 0, width, height, bits, width, height, 0, 0);

                for (int col = 0; col < width; col++)
                {
                    for (int row = 0; row < height; row++)
                    {
                        // equivalent to bits[col][row] if bits is 2-dimension array
                        double pixel = bits[col + row * width];
                        if (pixel < -10000)
                        {
                            pixel = 0;
                        }

                        double gcol = adfGeoTransform[0] + adfGeoTransform[1] * col + adfGeoTransform[2] * row;
                        double grow = adfGeoTransform[3] + adfGeoTransform[4] * col + adfGeoTransform[5] * row;

                        Point3d pt = new Point3d(gcol, grow, pixel);

                        verts.Add(pt);
                    }

                }

                //Create mesh
                mesh.Vertices.AddVertices(verts);

                for (int u = 1; u < width; u++)
                {
                    for (int v = 1; v < height; v++)
                    {
                        mesh.Faces.AddFace(v - 1 + (u - 1) * (height), v - 1 + u * (height), v - 1 + u * (height) + 1, v - 1 + (u - 1) * (height) + 1);
                    }
                }

                mesh.Flip(true, true, true);

                band.Dispose();
            }

            ///Clean up
            Gdal.Unlink("/vsimem/topoclipped.tif");
            datasource.Dispose();

            DA.SetData(0, mesh);
            DA.SetData(1, dsbox);
            DA.SetData(2, srcInfo);
        }



        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.img;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{577EB6B8-CF48-4D73-AB49-3DB41DEA11EE}"); }
        }
    }
}
