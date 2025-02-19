using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using OSGeo.GDAL;
using OSGeo.OSR;
using Rhino.Geometry;
using System;
using System.Collections.Generic;


namespace Heron
{
    public class ImportTopoLite : HeronComponent
    {
        public ImportTopoLite() : base("Import Topo Lite", "ITL", "This is a basic version of the Import Topo component " +
            "which creates a topographic mesh from a raster file (IMG, HGT, ASCII, DEM, TIF, etc).  The data will be imported in the source's units " +
            "and spatial reference system, Heron's SetSRS and Rhino's EarthAnchorPoint will have no effect and the data could be far from Rhino's origin.  " +
            "Furthermore, a source's vertical units are typically in meters, so geodetic coordinate systems like WGS84 where horizontal units are in " +
            "degrees, will produce a wildly scaled mesh in the z dimension.  It is recommended to use a projected coordinate system like UTM.  " +
            "You can reproject your source's data from a geodetic to projected coordinate system using the GdalWarp component.", "GIS Import | Export")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("Topography Raster File", "topoFile", "Filepath(s) for the raster topography input", GH_ParamAccess.list);
            pManager[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Topography Mesh", "topoMesh", "Resultant topographic mesh from the source file", GH_ParamAccess.tree);
            pManager.AddRectangleParameter("Topography Extent", "topoExtent", "Bounding box for the entire source file", GH_ParamAccess.item);
            pManager.AddTextParameter("Topography Source Info", "topoInfo", "Raster info about topography source", GH_ParamAccess.item);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>(0, boundary);

            bool clip = false;
            if (boundary.Count > 0) { clip = true; }

            List<string> IMG_files = new List<string>();
            DA.GetDataList<string>(1, IMG_files);

            Heron.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();

            /// Allow stitching of multiple topo raster files to avoid gaps between
            string combinedVRT = "/vsimem/combined.tif";
            Dataset datasource = Gdal.BuildVRT(combinedVRT, IMG_files.ToArray(), null, null, null);
            
            //Dataset datasource = Gdal.Open(combinedVRT, Access.GA_ReadOnly);
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

            ///Get pixel size for vertex adjustment
            double pixelWidth = dsbox.Width / width;
            double pixelHeight = dsbox.Height / height;

            ///Declare trees
            GH_Structure<GH_Point> pointcloud = new GH_Structure<GH_Point>();
            GH_Structure<GH_Integer> rCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Integer> cCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Mesh> tMesh = new GH_Structure<GH_Mesh>();

            ///Ensure there is a boundary for the for loop when clip is false and no boundaries are input
            if (!clip && boundary.Count == 0) { boundary.Add(dsbox.ToNurbsCurve()); }

            for (int i = 0; i < boundary.Count; i++)
            {
                GH_Path path = new GH_Path(i);

                Curve clippingBoundary = boundary[i];

                if (!clip) { clippingBoundary = dsbox.ToNurbsCurve(); }

                string clippedTopoFile = "/vsimem/topoclipped.tif";

                if (!(dsbox.BoundingBox.Contains(clippingBoundary.GetBoundingBox(true).Min) && (dsbox.BoundingBox.Contains(clippingBoundary.GetBoundingBox(true).Max))) && clip)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more boundaries may be outside the bounds of the topo dataset.");
                }

                ///Offsets to mesh/boundary based on pixel size
                Point3d clipperMinPreAdd = clippingBoundary.GetBoundingBox(true).Corner(true, false, true);
                Point3d clipperMinPostAdd = new Point3d(clipperMinPreAdd.X, clipperMinPreAdd.Y, clipperMinPreAdd.Z);
                Point3d clipperMin = clipperMinPostAdd;

                Point3d clipperMaxPreAdd = clippingBoundary.GetBoundingBox(true).Corner(false, true, true);
                ///add/subtract pixel width if desired to get closer to boundary
                Point3d clipperMaxPostAdd = new Point3d();
                Point3d clipperMax = new Point3d();
                if (clip)
                {
                    clipperMaxPostAdd = new Point3d(clipperMaxPreAdd.X + pixelWidth, clipperMaxPreAdd.Y - pixelHeight, clipperMaxPreAdd.Z);
                    clipperMax = clipperMaxPostAdd;
                }
                else
                {
                    clipperMaxPostAdd = new Point3d(clipperMaxPreAdd.X, clipperMaxPreAdd.Y, clipperMaxPreAdd.Z);
                    clipperMax = clipperMaxPostAdd;
                }

                double lonWest = clipperMin.X;
                double lonEast = clipperMax.X;
                double latNorth = clipperMin.Y;
                double latSouth = clipperMax.Y;

                string spatialReference = string.Empty;
                sr.ExportToWkt(out spatialReference, null);

                var translateOptions = new List<string>
                {
                    "-of", "GTiff",
                    //"-a_nodata", "0", ///Fill no data values with 0 to avoid causing errors in creating the mesh
                    "-projwin_srs", spatialReference,
                    "-projwin", $"{lonWest}", $"{latNorth}", $"{lonEast}", $"{latSouth}"

                };

                bool fillNoData = false;
                bool noDataPresent = false;
                if (fillNoData)
                {
                    translateOptions.AddRange(new List<string> { "-a_nodata", "0"});
                }

                using (Dataset clippedDataset = Gdal.wrapper_GDALTranslate(clippedTopoFile, datasource, new GDALTranslateOptions(translateOptions.ToArray()), null, null))
                {
                    Band band = clippedDataset.GetRasterBand(1);
                    width = clippedDataset.RasterXSize;
                    height = clippedDataset.RasterYSize;
                    clippedDataset.GetGeoTransform(adfGeoTransform);

                    rCount.Append(new GH_Integer(height), path);
                    cCount.Append(new GH_Integer(width), path);
                    Mesh mesh = new Mesh();
                    List<Point3d> verts = new List<Point3d>();
                    
                    double[] bits = new double[width * height];
                    band.ReadRaster(0, 0, width, height, bits, width, height, 0, 0);

                    for (int col = 0; col < width; col++)
                    {
                        for (int row = 0; row < height; row++)
                        {
                            // equivalent to bits[col][row] if bits is 2-dimension array
                            double pixel = bits[col + row * width];
                            if (pixel < -9998 || Double.IsNaN(pixel) || pixel > 1000000)
                            {
                                if (fillNoData) { pixel = 0; }
                                else { pixel = -9999; }
                                noDataPresent = true;
                            }

                            double gcol = adfGeoTransform[0] + adfGeoTransform[1] * col + adfGeoTransform[2] * row;
                            double grow = adfGeoTransform[3] + adfGeoTransform[4] * col + adfGeoTransform[5] * row;

                            Point3d pt = new Point3d(gcol, grow, pixel);

                            verts.Add(pt);
                        }

                    }

                    /// Create mesh
                    mesh.Vertices.UseDoublePrecisionVertices = true;
                    mesh.Vertices.AddVertices(verts);

                    for (int u = 1; u < width; u++)
                    {
                        for (int v = 1; v < height; v++)
                        {
                            int vertex1 = v - 1 + (u - 1) * (height);
                            int vertex2 = v - 1 + u * (height);
                            int vertex3 = v - 1 + u * (height) + 1;
                            int vertex4 = v - 1 + (u - 1) * (height) + 1;
                            if (fillNoData || !noDataPresent)
                            {
                                mesh.Faces.AddFace(vertex1, vertex2, vertex3, vertex4);
                            }
                            else
                            {
                                if (!(mesh.Vertices.Point3dAt(vertex1).Z < -9998) &&
                                    !(mesh.Vertices.Point3dAt(vertex2).Z < -9998) &&
                                    !(mesh.Vertices.Point3dAt(vertex3).Z < -9998) &&
                                    !(mesh.Vertices.Point3dAt(vertex4).Z < -9998)
                                    )
                                {
                                    mesh.Faces.AddFace(vertex1, vertex2, vertex3, vertex4);
                                }
                            }
                        }
                    }

                    mesh.Flip(true, true, true);
                    mesh.Compact();
                    tMesh.Append(new GH_Mesh(mesh), path);

                    band.Dispose();
                }
                /// End using

                ///Clean up
                Gdal.Unlink("/vsimem/topoclipped.tif");
            }
            /// End clipping boundary loop

            datasource.Dispose();
            ///Clean up
            Gdal.Unlink("/vsimem/combined.tif");

            DA.SetDataTree(0, tMesh);
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
            get { return new Guid("72F1C308-ADB9-4638-87E6-D5CC0B4D4260"); }
        }
    }
}
