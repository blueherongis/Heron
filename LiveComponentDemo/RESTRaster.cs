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

using Newtonsoft.Json.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Serialization;

namespace Heron
{
    public class RESTRaster : GH_Component
    {
        //Class Constructor
        public RESTRaster() : base("Get REST Raster","RESTRaster","Get raster imagery from ArcGIS REST Services","Heron","GIS REST")
        { 
        
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Resolution", "resolution", "Maximum resolution for images", GH_ParamAccess.item);
            pManager.AddTextParameter("File Location", "fileLocation", "Folder to place image files", GH_ParamAccess.item);
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item);
            pManager.AddTextParameter("REST URL", "URL", "ArcGIS REST Service website to query", GH_ParamAccess.item);
            pManager.AddBooleanParameter("run", "get", "Go ahead and download imagery from the Service", GH_ParamAccess.item, false);
            
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("image", "Image", "File location of downloaded image", GH_ParamAccess.tree);
            pManager.AddCurveParameter("imageFrame", "imageFrame", "Bounding box of image for mapping to geometry", GH_ParamAccess.tree);
            pManager.AddTextParameter("RESTQuery", "RESTQuery", "Full text of REST query", GH_ParamAccess.tree);
            
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            int Res = -1;
            DA.GetData<int>("Resolution", ref Res);

            string fileloc = "";
            DA.GetData<string>("File Location", ref fileloc);

            string prefix = "";
            DA.GetData<string>("Prefix", ref prefix);

            string URL = "";
            DA.GetData<string>("REST URL", ref URL);

            bool run = false;
            DA.GetData<bool>("run", ref run);

            int SRef = 3857;


            GH_Structure<GH_String> mapList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> mapquery = new GH_Structure<GH_String>();
            GH_Structure<GH_ObjectWrapper> imgFrame = new GH_Structure<GH_ObjectWrapper>();

            FileInfo file = new FileInfo(fileloc);
            file.Directory.Create();

            string size = "";
            if (Res != 0)
            {
                size = "&size=" + Res + "%2C" + Res;
            }

            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path path = new GH_Path(i);

                //Get image frame for given boundary
                BoundingBox imageBox = bbox(boundary[i]);

                Point3d min = ConvertWSG(imageBox.Min);
                Point3d max = ConvertWSG(imageBox.Max);
                List<Point3d> imageCorners = imageBox.GetCorners().ToList();
                imageCorners.Add(imageCorners[0]);
                Polyline bpoly = new Polyline(imageCorners);

                imgFrame.Append(new GH_ObjectWrapper(bpoly), path);

                //Query the REST service

                string restquery = URL +
                  "bbox=" + ConvertLat(min.X, SRef) + "%2C" + ConvertLon(min.Y, SRef) + "%2C" + ConvertLat(max.X, SRef) + "%2C" + ConvertLon(max.Y, SRef) +
                  "&bboxSR=" + SRef +
                  size + //"&layers=&layerdefs=" +
                  "&imageSR=" + SRef + //"&transparent=false&dpi=&time=&layerTimeOptions=" +
                  "&format=jpg&f=image";
                if (run)
                {
                    System.Net.WebClient webClient = new System.Net.WebClient();
                    webClient.DownloadFile(restquery, fileloc + prefix + "_" + i + ".jpg");
                    webClient.Dispose();
                }
                mapList.Append(new GH_String(fileloc + prefix +"_" + i + ".jpg"), path);
                mapquery.Append(new GH_String(restquery), path);
            }

            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, mapquery);


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

        public static Point3d ConvertWSG(Point3d xyz)
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

        public static BoundingBox bbox(Curve crv)
        {
            double bWidth = crv.GetBoundingBox(true).GetEdges()[0].Length;
            double bHeight = crv.GetBoundingBox(true).GetEdges()[1].Length;
            double side;
            if (bWidth > bHeight)
            {
                side = bWidth;
            }
            else
            {
                side = bHeight;
            }

            Vector3d mVec = new Vector3d(side, side, 0);

            return new BoundingBox(crv.GetBoundingBox(true).Min, (crv.GetBoundingBox(true).Min + mVec));
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
            get { return new Guid("{EB41AAA3-C9DA-42DE-8B58-D4A1CBDADCC8}"); }
        }
    }
}
