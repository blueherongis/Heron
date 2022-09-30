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
    public class ImportSHP : HeronComponent
    {
        //Class Constructor
        public ImportSHP() : base("Import SHP", "ImportSHP", "Import a Shapefile clipped to a boundary", "GIS Tools")
        {

        }

        ///Retiring this component in favor of ImportVector
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.hidden; }
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("Shapefile Location", "shpLocation", "Filepath for the *.shp input", GH_ParamAccess.item);
            pManager[0].Optional = true;

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("LayerName", "layerName", "Shapefile Layer Name", GH_ParamAccess.item);
            pManager.AddIntegerParameter("shpFeatureCount", "shpFeatureCount", "Total number of features in the Shapefile", GH_ParamAccess.list);
            pManager.AddCurveParameter("shpExtent", "shpExtent", "Bounding box of all Shapefile features", GH_ParamAccess.item);
            pManager.AddTextParameter("shpSpatRef", "shpSpatRef", "Spatial Reference of the input Shapefile", GH_ParamAccess.item);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the Shapefile features", GH_ParamAccess.list);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddPointParameter("FeaturePoints", "featurePoints", "Point geometry describing each feature", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {

            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>(0, boundary);

            string shpfilePath = string.Empty;
            DA.GetData<string>("Shapefile Location", ref shpfilePath);

            ////int SRef = 3857;
            //GdalConfiguration.ConfigureOgr();
            //GdalConfiguration.ConfigureGdal();

            OSGeo.OGR.Driver drv = OSGeo.OGR.Ogr.GetDriverByName("ESRI Shapefile");
            OSGeo.OGR.DataSource ds = OSGeo.OGR.Ogr.Open(shpfilePath, 0);

            List<OSGeo.OGR.Layer> layerset = new List<OSGeo.OGR.Layer>();
            List<int> fc = new List<int>();

            for (int iLayer = 0; iLayer < ds.GetLayerCount(); iLayer++)
            {
                OSGeo.OGR.Layer layer = ds.GetLayerByIndex(iLayer);

                if (layer == null)
                {
                    Console.WriteLine("FAILURE: Couldn't fetch advertised layer " + iLayer);
                    System.Environment.Exit(-1);
                }
                long count = layer.GetFeatureCount(1);
                int featureCount = System.Convert.ToInt32(count);
                fc.Add(featureCount);
                layerset.Add(layer);
            }

            //Get OGR envelope of Shapefile
            OSGeo.OGR.Envelope ext = new OSGeo.OGR.Envelope();
            layerset[0].GetExtent(ext, 1);
            Point3d extMin = new Point3d();
            Point3d extMax = new Point3d();
            extMin.X = ext.MinX;
            extMin.Y = ext.MinY;
            extMax.X = ext.MaxX;
            extMax.Y = ext.MaxY;

            OSGeo.OSR.SpatialReference sr = layerset[0].GetSpatialRef();

            OSGeo.OSR.SpatialReference dst = new OSGeo.OSR.SpatialReference("");
            dst.SetWellKnownGeogCS("WGS84");

            //Get the spatial refernce of the input Shapefile
            string sRef;
            //sr.ExportToWkt(out sRef);

            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(sr, dst);
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(dst, sr);

            //Get bounding box of data in Shapefile
            double[] extMinPT = new double[3] { extMin.X, extMin.Y, extMin.Z };
            double[] extMaxPT = new double[3] { extMax.X, extMax.Y, extMax.Z };
            coordTransform.TransformPoint(extMinPT);
            coordTransform.TransformPoint(extMaxPT);
            Point3d extPTmin = new Point3d(extMinPT[0], extMinPT[1], extMinPT[2]);
            Point3d extPTmax = new Point3d(extMaxPT[0], extMaxPT[1], extMaxPT[2]);
            Rectangle3d rec = new Rectangle3d(Plane.WorldXY, Heron.Convert.WGSToXYZ(extPTmin), Heron.Convert.WGSToXYZ(extPTmax));

            //Declare trees
            GH_Structure<GH_String> fset = new GH_Structure<GH_String>();
            GH_Structure<GH_Point> gset = new GH_Structure<GH_Point>();
            GH_Structure<GH_String> layname = new GH_Structure<GH_String>();
            OSGeo.OGR.FeatureDefn def = layerset[0].GetLayerDefn();

            //Loop through input boundaries
            for (int i = 0; i < boundary.Count; i++)
            {
                if (rec.BoundingBox.Contains(boundary[i].GetBoundingBox(true).Min) && (rec.BoundingBox.Contains(boundary[i].GetBoundingBox(true).Max)))
                {

                    //Create bounding box for clipping geometry
                    Point3d min = Heron.Convert.XYZToWGS(boundary[i].GetBoundingBox(true).Min);
                    Point3d max = Heron.Convert.XYZToWGS(boundary[i].GetBoundingBox(true).Max);
                    double[] minpT = new double[3];
                    double[] maxpT = new double[3];

                    minpT[0] = min.X;
                    minpT[1] = min.Y;
                    minpT[2] = min.Z;
                    maxpT[0] = max.X;
                    maxpT[1] = max.Y;
                    maxpT[2] = max.Z;
                    revTransform.TransformPoint(minpT);
                    revTransform.TransformPoint(maxpT);

                    OSGeo.OGR.Geometry bbox = OSGeo.OGR.Geometry.CreateFromWkt("POLYGON((" + min.X + " " + min.Y + ", " + min.X + " " + max.Y + ", " + max.X + " " + max.Y + ", " + max.X + " " + min.Y + ", " + min.X + " " + min.Y + "))");
                    OSGeo.OGR.Geometry ebbox = OSGeo.OGR.Geometry.CreateFromWkt("POLYGON((" + minpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + minpT[1] + "))");

                    //Clip Shapefile
                    //http://pcjericks.github.io/py-gdalogr-cookbook/vector_layers.html
                    OSGeo.OGR.Layer clipped_layer = layerset[0];
                    clipped_layer.SetSpatialFilter(ebbox);

                    //Loop through geometry
                    OSGeo.OGR.Feature feat;
                    def = clipped_layer.GetLayerDefn();

                    int m = 0;
                    while ((feat = layerset[0].GetNextFeature()) != null)
                    {
                        if (feat.GetGeometryRef() != null)
                        {

                            //Get geometry points and field values
                            OSGeo.OGR.Geometry geom = feat.GetGeometryRef();
                            OSGeo.OGR.Geometry sub_geom;

                            //Start get points if open polylines and points
                            for (int gpc = 0; gpc < geom.GetPointCount(); gpc++)
                            { //Loop through geometry points
                                double[] pT = new double[3];
                                pT[0] = geom.GetX(gpc);
                                pT[1] = geom.GetY(gpc);
                                pT[2] = geom.GetZ(gpc);
                                if (Double.IsNaN(geom.GetZ(gpc)))
                                {
                                    pT[2] = 0;
                                }
                                coordTransform.TransformPoint(pT);

                                Point3d pt3D = new Point3d();
                                pt3D.X = pT[0];
                                pt3D.Y = pT[1];
                                pt3D.Z = pT[2];

                                gset.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3D)), new GH_Path(i, m));
                                //End loop through geometry points

                                // Get Feature Values
                                if (fset.PathExists(new GH_Path(i, m)))
                                {
                                    fset.get_Branch(new GH_Path(i, m)).Clear();
                                }
                                for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                {
                                    OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                    if (feat.IsFieldSet(iField))
                                    {
                                        fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, m));
                                    }
                                    else
                                    {
                                        fset.Append(new GH_String("null"), new GH_Path(i, m));
                                    }
                                }
                                //End Get Feature Values
                            }
                            //End getting points if open polylines or points

                            //Start getting points if closed polylines and multipolygons
                            for (int gi = 0; gi < geom.GetGeometryCount(); gi++)
                            {
                                sub_geom = geom.GetGeometryRef(gi);
                                List<Point3d> geom_list = new List<Point3d>();

                                for (int ptnum = 0; ptnum < sub_geom.GetPointCount(); ptnum++)
                                {
                                    //Loop through geometry points
                                    double[] pT = new double[3];
                                    pT[0] = sub_geom.GetX(ptnum);
                                    pT[1] = sub_geom.GetY(ptnum);
                                    pT[2] = sub_geom.GetZ(ptnum);
                                    coordTransform.TransformPoint(pT);

                                    Point3d pt3D = new Point3d();
                                    pt3D.X = pT[0];
                                    pt3D.Y = pT[1];
                                    pt3D.Z = pT[2];

                                    gset.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3D)), new GH_Path(i, m, gi));
                                    //End loop through geometry points

                                    // Get Feature Values
                                    if (fset.PathExists(new GH_Path(i, m)))
                                    {
                                        fset.get_Branch(new GH_Path(i, m)).Clear();
                                    }
                                    for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                    {
                                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                        if (feat.IsFieldSet(iField))
                                        {
                                            fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, m));
                                        }
                                        else
                                        {
                                            fset.Append(new GH_String("null"), new GH_Path(i, m));
                                        }
                                    }
                                    //End Get Feature Values
                                }
                                //End getting points from closed polylines
                            }
                            m++;
                        }
                        feat.Dispose();
                    }
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more boundaries may be outside the bounds of the Shapefile dataset.");
                    //return;
                }
            }

            //Get the field names
            List<string> fieldnames = new List<string>();
            for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
            {
                OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iAttr);
                fieldnames.Add(fdef.GetNameRef());
            }

            DA.SetData(0, def.GetName());
            DA.SetDataList(1, fc);
            DA.SetData(2, rec);
            //DA.SetData(3, sRef);
            DA.SetDataList(4, fieldnames);
            DA.SetDataTree(5, fset);
            DA.SetDataTree(6, gset);

        }



        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.shp;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{b92addc7-022f-4fb8-8f07-8c9db1e1b3b4}"); }
        }
    }
}
