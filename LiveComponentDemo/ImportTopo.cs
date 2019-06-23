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
    public class ImportTopo : GH_Component
    {
        //Class Constructor
        public ImportTopo() : base("Import Topo","ImportTopo","Create a topographic mesh from an IMG or HGT file clipped to a boundary","Heron","GIS Tools")
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

            OSGeo.GDAL.Gdal.AllRegister();

            //string memFilename = "/vsimem/inmemfile";
            //Gdal.FileFromMemBuffer(memFilename, imageBuffer);

            Dataset ds = Gdal.Open(IMG_file, Access.GA_ReadOnly);
            OSGeo.GDAL.Driver drv = ds.GetDriver();

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
            Point3d dsMin = new Point3d(oX, eY, 0);
            Point3d dsMax = new Point3d(eX, oY, 0);
            Rectangle3d dsbox = new Rectangle3d(Plane.WorldXY, ConvertToXYZ(dsMin), ConvertToXYZ(dsMax));

            //Declare trees
            GH_Structure<GH_Point> pointcloud = new GH_Structure<GH_Point>();
            GH_Structure<GH_Integer> rCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Integer> cCount = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Mesh> tMesh = new GH_Structure<GH_Mesh>();

            for (int i = 0; i < boundary.Count; i++)
            {
                if (dsbox.BoundingBox.Contains(boundary[i].GetBoundingBox(true).Min) && (dsbox.BoundingBox.Contains(boundary[i].GetBoundingBox(true).Max)))
                {
                    
                    Point3d min = ConvertToWSG(boundary[i].GetBoundingBox(true).Corner(true, false, true));
                    Point3d max = ConvertToWSG(boundary[i].GetBoundingBox(true).Corner(false, true, true));
                    GH_Path path = new GH_Path(i);

                    // http://gis.stackexchange.com/questions/46893/how-do-i-get-the-pixel-value-of-a-gdal-raster-under-an-ogr-point-without-numpy

                    double ur, uc, lr, lc;
                    Gdal.ApplyGeoTransform(invTransform, min.X, min.Y, out uc, out ur);
                    Gdal.ApplyGeoTransform(invTransform, max.X, max.Y, out lc, out lr);
                    int Urow = Convert.ToInt32(ur);
                    int Ucol = Convert.ToInt32(uc);
                    int Lrow = Convert.ToInt32(lr) + 1;
                    int Lcol = Convert.ToInt32(lc) + 1;
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

                            Point3d pt = new Point3d(gcol, grow, pixel);
                            verts.Add(ConvertToXYZ(pt));
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
                                vertsParallel[] = ConvertToXYZ(pt);
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

        //Conversion from WSG84 to Google/Bing from
        //http://alastaira.wordpress.com/2011/01/23/the-google-maps-bing-maps-spherical-mercator-projection/

        public static double ConvertLon(double lon, int spatRef)
        {
            double clon = lon;
            if (spatRef == 3857)
            {
                double y = Math.Log(Math.Tan((90 + lon) * Math.PI / 360)) / (Math.PI / 180);
                y = y * 20037508.34 / 180;
                clon = y;
            }
            return clon;
        }

        public static double ConvertLat(double lat, int spatRef)
        {
            double clat = lat;
            if (spatRef == 3857)
            {
                double x = lat * 20037508.34 / 180;
                clat = x;
            }
            return clat;
        }


        //Using Rhino's EarthAnchorPoint to Transform.  GetModelToEarthTransform() translates to WSG84.
        //https://github.com/gHowl/gHowlComponents/blob/master/gHowl/gHowl/GEO/XYZtoGeoComponent.cs
        //https://github.com/mcneel/rhinocommon/blob/master/dotnet/opennurbs/opennurbs_3dm_settings.cs  search for "model_to_earth"

        public static Point3d ConvertToWSG(Point3d xyz)
        {
            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);
            xyz = xyz * Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, UnitSystem.Meters);
            Point3d ptON = new Point3d(xyz.X, xyz.Y, xyz.Z);
            ptON = xf * ptON;
            return ptON;
        }

        public static Point3d ConvertToXYZ(Point3d wsg)
        {
            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);

            //http://www.grasshopper3d.com/forum/topics/matrix-datatype-in-rhinocommon
            //Thanks Andrew
            Transform Inversexf = new Transform();
            xf.TryGetInverse(out Inversexf);
            Point3d ptMod = new Point3d(wsg.X, wsg.Y, wsg.Z);
            ptMod = Inversexf * ptMod / Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, UnitSystem.Meters);
            return ptMod;
        }

        public static Point3d ConvertXY(double x, double y, int spatRef)
        {
            double lon = x;
            double lat = y;

            if (spatRef == 3857)
            {
                lon = (x / 20037508.34) * 180;
                lat = (y / 20037508.34) * 180;
                lat = 180 / Math.PI * (2 * Math.Atan(Math.Exp(lat * Math.PI / 180)) - Math.PI / 2);
            }

            Point3d coord = new Point3d();
            coord.X = lon;
            coord.Y = lat;

            return ConvertToXYZ(coord);
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
