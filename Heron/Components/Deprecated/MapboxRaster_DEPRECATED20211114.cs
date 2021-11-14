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

using Newtonsoft.Json.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Serialization;

namespace Heron
{
    public class MapboxRaster_DEPRECATED20211114 : HeronRasterPreviewComponent
    {
        /// <summary>
        /// Initializes a new instance of the MapboxRaster class.
        /// </summary>
        public MapboxRaster_DEPRECATED20211114() : base("Mapbox Raster", "MapboxRaster", "Get raster imagery from a Mapbox service", "GIS API")
        {
        }

        ///Retireing this component to add tiled output
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.hidden; }
        }



        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {

            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Zoom Level", "zoom", "Slippy map zoom level. Higher zoom level is higher resolution, but takes longer to download. Max zoom is typically 19.", GH_ParamAccess.item, 14);
            pManager.AddTextParameter("Target folder", "folderPath", "Folder to place image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item);
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

            string folderPath = string.Empty;
            DA.GetData<string>(2, ref folderPath);
            if (!folderPath.EndsWith(@"\")) folderPath = folderPath + @"\";

            string prefix = string.Empty;
            DA.GetData<string>(3, ref prefix);
            if (prefix == "")
            {
                prefix = mbSource;
            }

            string URL = mbURL;
            //DA.GetData<string>(4, ref URL);

            ///get a valid mapbox token to send along with query
            string mbToken = string.Empty;
            DA.GetData<string>(4, ref mbToken);
            if (mbToken == "")
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
            GH_Structure<GH_Rectangle> imgFrame = new GH_Structure<GH_Rectangle>();
            GH_Structure<GH_String> tCount = new GH_Structure<GH_String>();


            for (int i = 0; i < boundary.Count; i++)
            {
                GH_Path path = new GH_Path(i);
                int tileTotalCount = 0;
                int tileDownloadedCount = 0;


                //Get image frame for given boundary and  make sure it's valid
                if (!boundary[i].GetBoundingBox(true).IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                    return;
                }
                BoundingBox boundaryBox = boundary[i].GetBoundingBox(true);

                ///TODO: look into scaling boundary to get buffer tiles

                ///file path for final image
                string imgPath = folderPath + prefix + "_" + i + ".jpg";

                //location of final image file
                mapList.Append(new GH_String(imgPath), path);

                //create cache folder for images
                string cacheLoc = folderPath + @"HeronCache\";
                List<string> cachefolderPaths = new List<string>();
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
                        cachefolderPaths.Add(cacheLoc + mbSource.Replace(" ", "") + zoom + x + y + ".jpg");
                        tileTotalCount = tileTotalCount + 1;
                    }
                }

                tCount.Insert(new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)"), path, 0);

                //bounding box of tile boundaries
                BoundingBox bbox = new BoundingBox(boxPtList);

                var rect = BBoxToRect(bbox);
                imgFrame.Append(new GH_Rectangle(rect), path);

                AddPreviewItem(imgPath, boundary[i], rect);

                ///tile range as string for (de)serialization of TileCacheMeta
                string tileRangeString = zoom.ToString()
                    + x_range[0].ToString()
                    + y_range[0].ToString()
                    + x_range[1].ToString()
                    + y_range[1].ToString();

                ///check if the existing final image already covers the boundary. 
                ///if so, no need to download more or reassemble the cached tiles.
                if ((TileCacheMeta == tileRangeString) && Convert.CheckCacheImagesExist(cachefolderPaths))
                {
                    if (File.Exists(imgPath))
                    {
                        using (Bitmap imageT = new Bitmap(imgPath))
                        {
                            ///getting commments currently only working for JPG
                            ///TODO: get this to work for any image type or
                            ///find another way to check if the cached image covers the boundary.
                            string imgComment = imageT.GetCommentsFromJPG();

                            imageT.Dispose();

                            ///check to see if tilerange in comments matches current tilerange
                            if (imgComment == (mbSource.Replace(" ", "") + tileRangeString))
                            {
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using existing image.");
                                continue;
                            }

                        }

                    }

                }



                ///Query Mapbox URL
                ///download all tiles within boundary
                ///merge tiles into one bitmap
                ///API to query
                string mbURLauth = mbURL + mbToken;


                ///Do the work of assembling image
                ///setup final image container bitmap
                int fImageW = ((int)x_range.Length + 1) * 512;
                int fImageH = ((int)y_range.Length + 1) * 512;
                Bitmap finalImage = new Bitmap(fImageW, fImageH);


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
                                ///create tileCache name 
                                string tileCache = mbSource.Replace(" ", "") + zoom + x + y + ".jpg";
                                string tileCahceLoc = cacheLoc + tileCache;

                                ///check cache folder to see if tile image exists locally
                                if (File.Exists(tileCahceLoc))
                                {
                                    Bitmap tmpImage = new Bitmap(Image.FromFile(tileCahceLoc));
                                    ///add tmp image to final
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

                                    ///add tmp image to final
                                    g.DrawImage(tmpImage, imgPosW * 512, imgPosH * 512);
                                    tmpImage.Dispose();
                                    tileDownloadedCount = tileDownloadedCount + 1;
                                }

                                ///increment x insert position, goes left to right
                                imgPosW++;
                            }
                            ///increment y insert position, goes top to bottom
                            imgPosH++;
                            imgPosW = 0;

                        }
                        ///garbage collection
                        g.Dispose();

                        ///add tile range meta data to image comments
                        finalImage.AddCommentsToJPG(mbSource.Replace(" ", "") + tileRangeString);

                        ///save the image
                        finalImage.Save(imgPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                }

                //garbage collection
                finalImage.Dispose();


                //add to tile count total
                tCount.Insert(new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)"), path, 0);

                //write out new tile range metadata for serialization
                TileCacheMeta = tileRangeString;

                //AddPreviewItem(imgPath, boundary[i], rect);

            }


            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, tCount);
            DA.SetDataList(3, "copyright Mapbox");

        }

        /////////////////////////





        ///Menu items
        ///https://www.grasshopper3d.com/forum/topics/closing-component-popup-side-bars-when-clicking-outside-the-form
        ///

        private bool IsServiceSelected(string serviceString)
        {
            return serviceString.Equals(mbSource);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {

            if (mbSourceList == "")
            {
                mbSourceList = Convert.GetEnpoints();
            }

            JObject mbJson = JObject.Parse(mbSourceList);

            ToolStripMenuItem root = new ToolStripMenuItem("Pick Mapbox Raster Service");

            foreach (var service in mbJson["Mapbox Maps"])
            {
                string sName = service["service"].ToString();

                ToolStripMenuItem serviceName = new ToolStripMenuItem(sName);
                serviceName.Tag = sName;
                serviceName.Checked = IsServiceSelected(sName);
                //serviceName.ToolTipText = "Service description goes here";
                serviceName.Click += ServiceItemOnClick;

                root.DropDownItems.Add(serviceName);
            }

            menu.Items.Add(root);

            base.AppendAdditionalComponentMenuItems(menu);

        }

        private void ServiceItemOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string code = (string)item.Tag;
            if (IsServiceSelected(code))
                return;

            RecordUndoEvent("MapboxSource");
            RecordUndoEvent("MapboxURL");


            mbSource = code;
            mbURL = JObject.Parse(mbSourceList)["Mapbox Maps"].SelectToken("[?(@.service == '" + mbSource + "')].url").ToString();
            Message = mbSource;

            ExpireSolution(true);
        }





        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private string tCacheMeta = string.Empty;
        private string mbSourceList = Convert.GetEnpoints();
        private string mbSource = JObject.Parse(Convert.GetEnpoints())["Mapbox Maps"][0]["service"].ToString();
        private string mbURL = JObject.Parse(Convert.GetEnpoints())["Mapbox Maps"][0]["url"].ToString();


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


        /////////////////////////

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                //You can add image files to your project resources and access them like this:
                // return Resources.IconForThisComponent;
                return Properties.Resources.raster;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("F8F9EBA4-26A7-4105-8282-8CF5252A7E03"); }
        }
    }
}