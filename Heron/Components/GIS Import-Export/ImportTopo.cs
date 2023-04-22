using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;
using GH_IO;
using GH_IO.Serialization;
using OSGeo.GDAL;
using OSGeo.OSR;
using OSGeo.OGR;



namespace Heron
{
    public class ImportTopo : HeronComponent
    {
        //Class Constructor
        public ImportTopo() : base("Import Topo", "ImportTopo", "Create a topographic mesh from a raster file (IMG, HGT, ASCII, DEM, TIF, etc) clipped to a boundary", "GIS Import | Export")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("Topography Raster File", "topoFile", "Filepath for the raster topography input", GH_ParamAccess.item);
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

            string IMG_file = string.Empty;
            DA.GetData<string>(1, ref IMG_file);

            RESTful.GdalConfiguration.ConfigureGdal();
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


            ///Get the spatial reference of the input raster file and set to WGS84 if not known
            ///Set up transform from source to WGS84
            OSGeo.OSR.SpatialReference sr = new SpatialReference(Osr.SRS_WKT_WGS84_LAT_LONG);
            if (datasource.GetProjection() == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Coordinate Reference System (CRS) is missing.  CRS set automatically set to WGS84.");
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

                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Data source SRS: EPSG:" + sr.GetAttrValue("AUTHORITY", 1));
                }
            }
            ///Get HeronSRS
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + heronSRSInt;

            ///Apply EAP to HeronSRS
            Transform heronToUserSRSTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
            Transform userSRSToHeronTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);

            ///Set transforms between source and HeronSRS
            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(sr, heronSRS);
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(heronSRS, sr);

            double[] adfGeoTransform = new double[6];
            double[] invTransform = new double[6];
            datasource.GetGeoTransform(adfGeoTransform);
            Gdal.InvGeoTransform(adfGeoTransform, invTransform);

            int width = datasource.RasterXSize;
            int height = datasource.RasterYSize;

            ///Dataset bounding box
            double oX = adfGeoTransform[0] + adfGeoTransform[1] * 0 + adfGeoTransform[2] * 0;
            double oY = adfGeoTransform[3] + adfGeoTransform[4] * 0 + adfGeoTransform[5] * 0;
            double eX = adfGeoTransform[0] + adfGeoTransform[1] * width + adfGeoTransform[2] * height;
            double eY = adfGeoTransform[3] + adfGeoTransform[4] * width + adfGeoTransform[5] * height;

            ///Transform to Heron SRS
            double[] extMinPT = new double[3] { oX, eY, 0 };
            double[] extMaxPT = new double[3] { eX, oY, 0 };
            coordTransform.TransformPoint(extMinPT);
            coordTransform.TransformPoint(extMaxPT);
            Point3d dsMin = new Point3d(extMinPT[0], extMinPT[1], extMinPT[2]);
            Point3d dsMax = new Point3d(extMaxPT[0], extMaxPT[1], extMaxPT[2]);

            dsMin.Transform(heronToUserSRSTransform);
            dsMax.Transform(heronToUserSRSTransform);
            Rectangle3d dsbox = new Rectangle3d(Plane.WorldXY, dsMin, dsMax);


            double pixelWidth = dsbox.Width / width;
            double pixelHeight = dsbox.Height / height;

            ///Declare trees
            GH_Structure<GH_Point> pointcloud = new GH_Structure<GH_Point>();
            GH_Structure<GH_Integer> rCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Integer> cCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Mesh> tMesh = new GH_Structure<GH_Mesh>();

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
                clipperMinPostAdd.Transform(userSRSToHeronTransform);
                Point3d clipperMin = clipperMinPostAdd;

                Point3d clipperMaxPreAdd = clippingBoundary.GetBoundingBox(true).Corner(false, true, true);
                ///add/subtract pixel width if desired to get closer to boundary
                Point3d clipperMaxPostAdd = new Point3d();
                Point3d clipperMax = new Point3d();
                if (clip) 
                { 
                    clipperMaxPostAdd = new Point3d(clipperMaxPreAdd.X + pixelWidth, clipperMaxPreAdd.Y - pixelHeight, clipperMaxPreAdd.Z);
                    clipperMaxPostAdd.Transform(userSRSToHeronTransform);
                    clipperMax = clipperMaxPostAdd;
                }
                else 
                { 
                    clipperMaxPostAdd = new Point3d(clipperMaxPreAdd.X, clipperMaxPreAdd.Y, clipperMaxPreAdd.Z);
                    clipperMaxPostAdd.Transform(userSRSToHeronTransform);
                    clipperMax = clipperMaxPostAdd;
                }

                double lonWest = clipperMin.X;
                double lonEast = clipperMax.X;
                double latNorth = clipperMin.Y;
                double latSouth = clipperMax.Y;

                var translateOptions = new[]
                {
                    "-of", "GTiff",
                    "-a_nodata", "0",
                    "-projwin_srs", HeronSRS.Instance.SRS,
                    "-projwin", $"{lonWest}", $"{latNorth}", $"{lonEast}", $"{latSouth}"
                };

                using (Dataset clippedDataset = Gdal.wrapper_GDALTranslate(clippedTopoFile, datasource, new GDALTranslateOptions(translateOptions), null, null))
                {
                    ///Correct vertical units. Typically, vertical datum is in meters even if horizontal is feet.
                    var linearUnits = sr.GetLinearUnits();
                    var verticalCorrection = 1.0;
                    if (sr.IsVertical() == 0) { verticalCorrection = 1 / linearUnits; }
                     
                    Band band = clippedDataset.GetRasterBand(1);
                    width = clippedDataset.RasterXSize;
                    height = clippedDataset.RasterYSize;
                    clippedDataset.GetGeoTransform(adfGeoTransform);
                    Gdal.InvGeoTransform(adfGeoTransform, invTransform);

                    rCount.Append(new GH_Integer(height), path);
                    cCount.Append(new GH_Integer(width), path);
                    Mesh mesh = new Mesh();
                    List<Point3d> verts = new List<Point3d>();
                    //var vertsParallel = new System.Collections.Concurrent.ConcurrentDictionary<double[][], Point3d>(Environment.ProcessorCount, ((Urow - 1) * (Lrow - 1)));

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

                            ///convert to WGS84
                            double[] wgsPT = new double[3] { gcol, grow, pixel };
                            coordTransform.TransformPoint(wgsPT);
                            Point3d pt = new Point3d(wgsPT[0], wgsPT[1], wgsPT[2]*verticalCorrection);

                            //verts.Add(Heron.Convert.WGSToXYZ(pt));
                            pt.Transform(heronToUserSRSTransform);
                            verts.Add(pt);

                        }

                        /*Parallel.For(Urow, Lrow - 1, rowP =>
                            {
                                // equivalent to bits[col][row] if bits is 2-dimension array
                                double pixel = bits[col + rowP * width];
                                if (pixel < -10000)
                                {
                                    pixel = 0;
                                }

                                double gcol = adfGeoTransform[0] + adfGeoTransform[1] * col + adfGeoTransform[2] * rowP;
                                double grow = adfGeoTransform[3] + adfGeoTransform[4] * col + adfGeoTransform[5] * rowP;

                                Point3d pt = new Point3d(gcol, grow, pixel);
                                vertsParallel[] = Heron.Convert.ToXYZ(pt);
                            });
                         * */

                    }

                    //Create meshes
                    //non Parallel
                    mesh.Vertices.AddVertices(verts);
                    //Parallel
                    //mesh.Vertices.AddVertices(vertsParallel.Values);

                    for (int u = 1; u < cCount[path][0].Value; u++)
                    {
                        for (int v = 1; v < rCount[path][0].Value; v++)
                        {
                            mesh.Faces.AddFace(v - 1 + (u - 1) * (height), v - 1 + u * (height), v - 1 + u * (height) + 1, v - 1 + (u - 1) * (height) + 1);
                            //(k - 1 + (j - 1) * num2, k - 1 + j * num2, k - 1 + j * num2 + 1, k - 1 + (j - 1) * num2 + 1)
                        }
                    }

                    //mesh.Flip(true, true, true);
                    tMesh.Append(new GH_Mesh(mesh), path);

                    band.Dispose();
                }
                Gdal.Unlink("/vsimem/topoclipped.tif");

            }

            datasource.Dispose();

            DA.SetDataTree(0, tMesh);
            DA.SetData(1, dsbox);
            DA.SetData(2, srcInfo);
        }



        private bool clip = true;
        public bool Clip
        {
            get { return clip; }
            set
            {
                clip = value;
                if ((clip))
                {
                    Message = "Clipped";
                }
                else
                {
                    Message = "Not Clipped";
                }
            }
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem item = Menu_AppendItem(menu, "Clip topography to boundary", Menu_ClipClicked, true, Clip);
            item.ToolTipText = "Clip topography with the boundary curve input.  If unchecked, the entire raster topography dataset will be created.";
        }

        private void Menu_ClipClicked(object sender, EventArgs e)
        {
            RecordUndoEvent("Clip");
            Clip = !Clip;
            ExpireSolution(true);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean("Clip", Clip);
            return base.Write(writer);
        }
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            Clip = reader.GetBoolean("Clip");
            return base.Read(reader);
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
            get { return new Guid("{48DC69A0-DA95-4629-A6F5-A813D87D9187}"); }
        }
    }
}
