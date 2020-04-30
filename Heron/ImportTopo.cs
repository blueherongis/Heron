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
        public ImportTopo() : base("Import Topo", "ImportTopo", "Create a topographic mesh from an IMG, HGT, ASCII file clipped to a boundary", "GIS Tools")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("IMG Location", "imgLocation", "Filepath for the *.img or *.hgt input", GH_ParamAccess.item);
            pManager[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("topoMesh", "topoMesh", "Resultant topographic mesh from IMG or HGT file", GH_ParamAccess.tree);
            pManager.AddRectangleParameter("topoExtent", "topoExtent", "Bounding box for the entire IMG or HGT file", GH_ParamAccess.item);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>(0, boundary);

            string IMG_file = "";
            DA.GetData<string>("IMG Location", ref IMG_file);
            /*  
              //Does not work with HGT files
             * 
              byte[] imageBuffer;

              using (FileStream fs = new FileStream(IMG_file, FileMode.Open, FileAccess.Read))
              {
                  using (BinaryReader br = new BinaryReader(fs))
                  {
                      long numBytes = new FileInfo(IMG_file).Length;
                      imageBuffer = br.ReadBytes((int)numBytes);
                      br.Close();
                      fs.Close();
                  }
              }
              */

            RESTful.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();

            //string memFilename = "/vsimem/inmemfile";
            //Gdal.FileFromMemBuffer(memFilename, imageBuffer);

            Dataset ds = Gdal.Open(IMG_file, Access.GA_ReadOnly);
            OSGeo.GDAL.Driver drv = ds.GetDriver();


            if (ds == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                return;
            }


            ///Get the spatial reference of the input raster file and set to WGS84 if not known
            ///Set up transform from source to WGS84
            OSGeo.OSR.SpatialReference sr = new SpatialReference(Osr.SRS_WKT_WGS84);
            if (ds.GetProjection() == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Coordinate Reference System (CRS) is missing.  CRS set automatically set to WGS84.");
            }

            else
            {
                sr = new SpatialReference(ds.GetProjection());
                if (sr.Validate() != 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Coordinate Reference System (CRS) is unknown or unsupported.  CRS set automatically set to WGS84.");
                    sr.SetWellKnownGeogCS("WGS84");
                }

                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Data source SRS: EPSG:" + sr.GetAttrValue("AUTHORITY", 1));
                }
            }

            //OSGeo.OSR.SpatialReference sr = new SpatialReference(ds.GetProjection());
            OSGeo.OSR.SpatialReference dst = new OSGeo.OSR.SpatialReference("");
            dst.SetWellKnownGeogCS("WGS84");
            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(sr, dst);
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(dst, sr);

            double[] adfGeoTransform = new double[6];
            double[] invTransform = new double[6];
            ds.GetGeoTransform(adfGeoTransform);
            Gdal.InvGeoTransform(adfGeoTransform, invTransform);
            Band band = ds.GetRasterBand(1);

            int width = ds.RasterXSize;
            int height = ds.RasterYSize;

            //Dataset bounding box
            double oX = adfGeoTransform[0] + adfGeoTransform[1] * 0 + adfGeoTransform[2] * 0;
            double oY = adfGeoTransform[3] + adfGeoTransform[4] * 0 + adfGeoTransform[5] * 0;
            double eX = adfGeoTransform[0] + adfGeoTransform[1] * width + adfGeoTransform[2] * height;
            double eY = adfGeoTransform[3] + adfGeoTransform[4] * width + adfGeoTransform[5] * height;

            ///Transform to WGS84
            double[] extMinPT = new double[3] { oX, eY, 0 };
            double[] extMaxPT = new double[3] { eX, oY, 0 };
            coordTransform.TransformPoint(extMinPT);
            coordTransform.TransformPoint(extMaxPT);
            Point3d dsMin = new Point3d(extMinPT[0], extMinPT[1], extMinPT[2]);
            Point3d dsMax = new Point3d(extMaxPT[0], extMaxPT[1], extMaxPT[2]);

            //Point3d dsMin = new Point3d(oX, eY, 0);
            //Point3d dsMax = new Point3d(eX, oY, 0);
            Rectangle3d dsbox = new Rectangle3d(Plane.WorldXY, Heron.Convert.ToXYZ(dsMin), Heron.Convert.ToXYZ(dsMax));

            //Declare trees
            GH_Structure<GH_Point> pointcloud = new GH_Structure<GH_Point>();
            GH_Structure<GH_Integer> rCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Integer> cCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Mesh> tMesh = new GH_Structure<GH_Mesh>();

            for (int i = 0; i < boundary.Count; i++)
            {
                if (dsbox.BoundingBox.Contains(boundary[i].GetBoundingBox(true).Min) && (dsbox.BoundingBox.Contains(boundary[i].GetBoundingBox(true).Max)))
                {

                    Point3d min = Heron.Convert.ToWGS(boundary[i].GetBoundingBox(true).Corner(true, false, true));
                    Point3d max = Heron.Convert.ToWGS(boundary[i].GetBoundingBox(true).Corner(false, true, true));

                    ///Transform to source SRS
                    double[] minR = new double[3] { min.X, min.Y, min.Z };
                    double[] maxR = new double[3] { max.X, max.Y, max.Z };
                    revTransform.TransformPoint(minR);
                    revTransform.TransformPoint(maxR);

                    GH_Path path = new GH_Path(i);

                    // http://gis.stackexchange.com/questions/46893/how-do-i-get-the-pixel-value-of-a-gdal-raster-under-an-ogr-point-without-numpy

                    double ur, uc, lr, lc;

                    Gdal.ApplyGeoTransform(invTransform, minR[0], minR[1], out uc, out ur);
                    Gdal.ApplyGeoTransform(invTransform, maxR[0], maxR[1], out lc, out lr);
                    //Gdal.ApplyGeoTransform(invTransform, min.X, min.Y, out uc, out ur);
                    //Gdal.ApplyGeoTransform(invTransform, max.X, max.Y, out lc, out lr);

                    int Urow = System.Convert.ToInt32(ur);
                    int Ucol = System.Convert.ToInt32(uc);
                    int Lrow = System.Convert.ToInt32(lr) + 1;
                    int Lcol = System.Convert.ToInt32(lc) + 1;
                    rCount.Append(new GH_Integer(Lrow - Urow), path);
                    cCount.Append(new GH_Integer(Lcol - Ucol), path);
                    Mesh mesh = new Mesh();
                    List<Point3d> verts = new List<Point3d>();
                    //var vertsParallel = new System.Collections.Concurrent.ConcurrentDictionary<double[][], Point3d>(Environment.ProcessorCount, ((Urow - 1) * (Lrow - 1)));

                    double[] bits = new double[width * height];
                    band.ReadRaster(0, 0, width, height, bits, width, height, 0, 0);

                    for (int col = Ucol; col < Lcol; col++)
                    {
                        for (int row = Urow; row < Lrow; row++)
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
                            Point3d pt = new Point3d(wgsPT[0], wgsPT[1], wgsPT[2]);

                            //Point3d pt = new Point3d(gcol, grow, pixel);
                            verts.Add(Heron.Convert.ToXYZ(pt));
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
                            mesh.Faces.AddFace(v - 1 + (u - 1) * (Lrow - Urow), v - 1 + u * (Lrow - Urow), v - 1 + u * (Lrow - Urow) + 1, v - 1 + (u - 1) * (Lrow - Urow) + 1);
                            //(k - 1 + (j - 1) * num2, k - 1 + j * num2, k - 1 + j * num2 + 1, k - 1 + (j - 1) * num2 + 1)
                        }
                    }
                    //mesh.Flip(true, true, true);
                    tMesh.Append(new GH_Mesh(mesh), path);
                }

                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more boundaries may be outside the bounds of the topo dataset.");
                    //return;
                }
            }
            DA.SetDataTree(0, tMesh);
            DA.SetData(1, dsbox);
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
            get { return new Guid("{E941555F-26ED-4F74-BEB3-B4E9182454F4}"); }
        }
    }
}
