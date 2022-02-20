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
    public class MapboxTopo : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MapboxTopo class.
        /// </summary>
        public MapboxTopo() : base("Mapbox Topography", "MapboxTopo", "Get mesh topography from a Mapbox service. Requires a Mapbox Token.", "GIS API")
        {

        }

        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.secondary; }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {

            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for topography", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Zoom Level", "zoom", "Slippy map zoom level. Higher zoom level is higher resolution.", GH_ParamAccess.item);
            pManager.AddTextParameter("File Location", "filePath", "Folder to place topography image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for topography image file name", GH_ParamAccess.item);
            pManager.AddTextParameter("Mapbox Access Token", "mbToken", "Mapbox Access Token string for access to Mapbox resources. Or set an Environment Variable 'HERONMAPOXTOKEN' with your token as the string.", GH_ParamAccess.item, "");
            pManager.AddBooleanParameter("Run", "get", "Go ahead and download imagery from the service", GH_ParamAccess.item, false);

            pManager[3].Optional = true;

            Message = MapboxSource;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Image File", "Image", "File location of downloaded image", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Image Frame", "imageFrame", "Bounding box of image for mapping to geometry", GH_ParamAccess.tree);
            pManager.AddTextParameter("Tile Count", "tileCount", "Number of image tiles resulting from Mapbox query", GH_ParamAccess.tree);
            pManager.AddMeshParameter("Topography Mesh", "topoMesh", "Topography mesh generated from the Mapbox topography image file service", GH_ParamAccess.tree);
            //https://www.mapbox.com/help/how-attribution-works/
            //https://www.mapbox.com/api-documentation/#retrieve-an-html-slippy-map Retrieve TileJSON metadata
            pManager.AddTextParameter("Mapbox Attribution", "mbAtt", "Mapbox word mark and text attribution required by Mapbox", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>(0, boundary);

            int zoom = -1;
            DA.GetData<int>(1, ref zoom);

            string filePath = string.Empty;
            DA.GetData<string>(2, ref filePath);
            if (!filePath.EndsWith(@"\")) filePath = filePath + @"\";

            string prefix = string.Empty;
            DA.GetData<string>(3, ref prefix);
            if (prefix == string.Empty)
            {
                prefix = mbSource;
            }

            string URL = mbURL;
            //DA.GetData<string>(4, ref URL);


            string mbToken = string.Empty;
            DA.GetData<string>(4, ref mbToken);
            if (mbToken == string.Empty)
            {
                string hmbToken = System.Environment.GetEnvironmentVariable("HERONMAPBOXTOKEN");
                if (hmbToken != null)
                {
                    mbToken = hmbToken;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using Mapbox token stored in Environment Variable HERONMAPBOXTOKEN.");
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No Mapbox token is specified.  Please get a valid token from mapbox.com");
                    return;
                }
            }

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            GH_Structure<GH_String> mapList = new GH_Structure<GH_String>();
            GH_Structure<GH_Curve> imgFrame = new GH_Structure<GH_Curve>();
            GH_Structure<GH_String> tCount = new GH_Structure<GH_String>();
            GH_Structure<GH_Mesh> tMesh = new GH_Structure<GH_Mesh>();

            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path path = new GH_Path(i);
                int tileTotalCount = 0;
                int tileDownloadedCount = 0;

                ///Get image frame for given boundary
                if (!boundary[i].GetBoundingBox(true).IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                    return;
                }
                BoundingBox boundaryBox = boundary[i].GetBoundingBox(true);

                ///TODO: look into scaling boundary to get buffer tiles

                ///file path for final image
                string imgPath = filePath + prefix + "_" + i + ".png";

                //location of final image file
                mapList.Append(new GH_String(imgPath), path);

                //create cache folder for images
                string cacheLoc = filePath + @"HeronCache\";
                List<string> cachefilePaths = new List<string>();
                if (!Directory.Exists(cacheLoc))
                {
                    Directory.CreateDirectory(cacheLoc);
                }

                //tile bounding box array
                List<Point3d> boxPtList = new List<Point3d>();


                //get the tile coordinates for all tiles within boundary
                var ranges = Convert.GetTileRange(boundaryBox, zoom);
                List<List<int>> tileList = new List<List<int>>();
                var x_range = ranges.XRange;
                var y_range = ranges.YRange;

                if (x_range.Length > 100 || y_range.Length > 100)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "This tile range is too big (more than 100 tiles in the x or y direction). Check your units.");
                    return;
                }

                //cycle through tiles to get bounding box
                for (int y = (int)y_range.Min; y <= y_range.Max; y++)
                {
                    for (int x = (int)x_range.Min; x <= x_range.Max; x++)
                    {
                        //add bounding box of tile to list
                        boxPtList.AddRange(Convert.GetTileAsPolygon(zoom, y, x).ToList());
                        cachefilePaths.Add(cacheLoc + mbSource.Replace(" ", "") + zoom + x + y + ".png");
                        tileTotalCount = tileTotalCount + 1;
                    }
                }

                tCount.Insert(new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)"), path, 0);

                //bounding box of tile boundaries
                BoundingBox bboxPts = new BoundingBox(boxPtList);

                //convert bounding box to polyline
                List<Point3d> imageCorners = bboxPts.GetCorners().ToList();
                imageCorners.Add(imageCorners[0]);
                imgFrame.Append(new GH_Curve(new Rhino.Geometry.Polyline(imageCorners).ToNurbsCurve()), path);

                //tile range as string for (de)serialization of TileCacheMeta
                string tileRangeString = zoom.ToString()
                    + x_range[0].ToString()
                    + y_range[0].ToString()
                    + x_range[1].ToString()
                    + y_range[1].ToString();

                //check if the existing final image already covers the boundary. 
                //if so, no need to download more or reassemble the cached tiles.
                ///temporarily disable until how to tag images with meta data is figured out
                /*
                if (TileCacheMeta == tileRangeString && Convert.CheckCacheImagesExist(cachefilePaths))
                {
                    if (File.Exists(imgPath))
                    {
                        using (Bitmap imageT = new Bitmap(imgPath))
                        {
                            //System.Drawing.Imaging.PropertyItem prop = imageT.GetPropertyItem(40092);
                            //string imgComment = Encoding.Unicode.GetString(prop.Value);
                            string imgComment = imageT.GetCommentsFromImage();
                            //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, imgComment);
                            imageT.Dispose();
                            //check to see if tilerange in comments matches current tilerange
                            if (imgComment == (mbSource.Replace(" ", "") + tileRangeString))
                            {
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using existing topo image.");

                                Mesh eMesh = TopoMeshFromImage(imgPath, boundaryBox, zoom);
                                tMesh.Append(new GH_Mesh(eMesh), path);
                                continue;
                            }

                        }

                    }

                }
                */


                ///Query Mapbox URL
                ///download all tiles within boundary
                ///merge tiles into one bitmap

                ///API to query
                ///string mbURL = "https://api.mapbox.com/v4/mapbox.terrain-rgb/{z}/{x}/{y}@2x.pngraw?access_token=" + mbToken;
                string mbURLauth = mbURL + mbToken;


                ///Do the work of assembling image
                ///setup final image container bitmap
                int fImageW = ((int)x_range.Length + 1) * 512;
                int fImageH = ((int)y_range.Length + 1) * 512;
                Bitmap finalImage = new Bitmap(fImageW, fImageH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                int imgPosW = 0;
                int imgPosH = 0;

                if (run == true)
                {
                    using (Graphics g = Graphics.FromImage(finalImage))
                    {
                        g.Clear(Color.Black);
                        for (int y = (int)y_range.Min; y <= (int)y_range.Max; y++)
                        {
                            for (int x = (int)x_range.Min; x <= (int)x_range.Max; x++)
                            {
                                //create tileCache name 
                                string tileCache = mbSource.Replace(" ", "") + zoom + x + y + ".png";
                                string tileCahceLoc = cacheLoc + tileCache;

                                //check cache folder to see if tile image exists locally
                                if (File.Exists(tileCahceLoc))
                                {
                                    Bitmap tmpImage = new Bitmap(Image.FromFile(tileCahceLoc));
                                    //add tmp image to final
                                    g.DrawImage(tmpImage, imgPosW * 512, imgPosH * 512);
                                    tmpImage.Dispose();
                                }

                                else
                                {
                                    tileList.Add(new List<int> { zoom, y, x });
                                    string urlAuth = Convert.GetZoomURL(x, y, zoom, mbURLauth);
                                    System.Net.WebClient client = new System.Net.WebClient();
                                    client.DownloadFile(urlAuth, tileCahceLoc);
                                    Bitmap tmpImage = new Bitmap(Image.FromFile(tileCahceLoc));
                                    client.Dispose();

                                    //add tmp image to final
                                    g.DrawImage(tmpImage, imgPosW * 512, imgPosH * 512);
                                    tmpImage.Dispose();
                                    tileDownloadedCount = tileDownloadedCount + 1;
                                }

                                //increment x insert position, goes left to right
                                imgPosW++;
                            }
                            //increment y insert position, goes top to bottom
                            imgPosH++;
                            imgPosW = 0;

                        }
                        //garbage collection
                        g.Dispose();

                        //add tile range meta data to image comments
                        finalImage.AddCommentsToPNG(mbSource.Replace(" ", "") + tileRangeString);

                        //save out assembled image 
                        finalImage.Save(imgPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }

                //garbage collection
                finalImage.Dispose();

                //add to tile count total
                tCount.Insert(new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)"), path, 0);


                Mesh nMesh = TopoMeshFromImage(imgPath, boundaryBox, zoom);

                //mesh.Flip(true, true, true);
                tMesh.Append(new GH_Mesh(nMesh), path);

                //write out new tile range metadata for serialization
                TileCacheMeta = tileRangeString;

                
            }

            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, tCount);
            DA.SetDataTree(3, tMesh);
            DA.SetDataList(4, "copyright Mapbox");
            
        }

        ////////////////////////////////
        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private string tCacheMeta = string.Empty;
        private string mbSourceList = Convert.GetEnpoints();
        private string mbSource = JObject.Parse(Convert.GetEnpoints())["Mapbox Topo"][0]["service"].ToString();
        private string mbURL = JObject.Parse(Convert.GetEnpoints())["Mapbox Topo"][0]["url"].ToString();

        public string TileCacheMeta
        {
            get { return tCacheMeta; }
            set
            {
                tCacheMeta = value;
                //Message = tCacheMeta;
            }
        }

        public string MapboxSourceList
        {
            get { return mbSourceList; }
            set
            {
                mbSourceList = value;
            }
        }

        public string MapboxSource
        {
            get { return mbSource; }
            set
            {
                mbSource = value;
                Message = mbSource;
            }
        }

        public string MapboxURL
        {
            get { return mbURL; }
            set
            {
                mbURL = value;
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("TileCacheMeta", TileCacheMeta);
            writer.SetString("MapboxSourceList", MapboxSourceList);
            writer.SetString("MapboxSource", MapboxSource);
            writer.SetString("MapboxURL", MapboxURL);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            TileCacheMeta = reader.GetString("TileCacheMeta");
            MapboxSourceList = reader.GetString("MapboxSourceList");
            MapboxSource = reader.GetString("MapboxSource");
            MapboxURL = reader.GetString("MapboxURL");
            return base.Read(reader);
        }

        ///////////////////////////////


        public static Mesh TopoMeshFromImage (string imgPath, BoundingBox boundaryBox, int zoom)
        {


            //get the tile coordinates for all tiles within boundary
            var ranges = Convert.GetTileRange(boundaryBox, zoom);
            List<List<int>> tileList = new List<List<int>>();
            var x_range = ranges.XRange;
            var y_range = ranges.YRange;


            //set up cropped final topo image for mesh creation
            Point3d min = Heron.Convert.XYZToWGS(boundaryBox.Corner(true, false, true));
            Point3d max = Heron.Convert.XYZToWGS(boundaryBox.Corner(false, true, true));
            double ur = Heron.Convert.DegToNumPixel(min.Y, min.X, zoom)[1];
            double uc = Heron.Convert.DegToNumPixel(min.Y, min.X, zoom)[0];
            double lr = Heron.Convert.DegToNumPixel(max.Y, max.X, zoom)[1];
            double lc = Heron.Convert.DegToNumPixel(max.Y, max.X, zoom)[0];
            int pixelWidth = System.Convert.ToInt32((lc - uc) * 512) + 1;
            int pixelHeight = System.Convert.ToInt32((lr - ur) * 512) + 1;
            int rowOffset = System.Convert.ToInt32((ur - y_range[0]) * 512 - 1);
            int colOffset = System.Convert.ToInt32((uc - x_range[0]) * 512 - 1);
            System.Drawing.Rectangle rect = new System.Drawing.Rectangle(colOffset, rowOffset, pixelWidth, pixelHeight);

            Bitmap topoImage = new Bitmap(imgPath);
            //get out of memory error here sometimes....
            Bitmap cropped = topoImage.Clone(rect, topoImage.PixelFormat);
            topoImage.Dispose();

            //create vertices from topo image pixel values
            //https://help.openstreetmap.org/questions/747/given-a-latlon-how-do-i-find-the-precise-position-on-the-tile
            Mesh mesh = new Mesh();
            List<Point3d> verts = new List<Point3d>();

            for (int col = 0; col < pixelWidth; col++)
            {
                for (int row = 0; row < pixelHeight; row++)
                {
                    double pixelLon = uc + (double)col / 512;//divide by 512 bc high res
                    double pixelLat = ur + (double)row / 512;
                    List<double> pixelLonLat = Convert.NumToDegPixel(pixelLon, pixelLat, zoom);

                    System.Drawing.Color c = cropped.GetPixel(col, row);

                    //height = -10000 + ((R * 256 * 256 + G * 256 + B) * 0.1)
                    //https://www.mapbox.com/help/access-elevation-data/
                    double h = -10000 + ((c.R * 256 * 256 + c.G * 256 + c.B) * 0.1);
                    Point3d pt = new Point3d(pixelLonLat[1], pixelLonLat[0], h);

                    //xxx = uc - x_range[0];

                    verts.Add(Heron.Convert.WGSToXYZ(pt));
                }
            }

            //clear cropped topo image
            cropped.Dispose();

            //Create meshes
            mesh.Vertices.AddVertices(verts);

            for (int u = 1; u < (pixelWidth); u++)
            {
                for (int v = 1; v < (pixelHeight); v++)
                {
                    mesh.Faces.AddFace(v - 1 + (u - 1) * (pixelHeight), v - 1 + u * (pixelHeight), v - 1 + u * (pixelHeight) + 1, v - 1 + (u - 1) * (pixelHeight) + 1);
                }
            }
            return mesh;
        }


        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                //You can add image files to your project resources and access them like this:
                // return Resources.IconForThisComponent;
                return Properties.Resources.img;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("34C8AB5F-2D39-4F9B-8879-5F74C8CF89E3"); }
        }
    }
}