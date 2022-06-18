using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Net;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using GH_IO.Serialization;

using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;

using Newtonsoft.Json.Linq;


namespace Heron
{
    public class MapboxRaster : HeronRasterPreviewComponent
    {
        /// <summary>
        /// Initializes a new instance of the MapboxRaster class.
        /// </summary>
        public MapboxRaster() : base("Mapbox Raster", "MapboxRaster", "Get raster imagery from a Mapbox service. Requires a Mapbox Token.", "GIS API")
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

            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Zoom Level", "zoom", "Slippy map zoom level. Higher zoom level is higher resolution, but takes longer to download. Max zoom is typically 19.", GH_ParamAccess.item);
            pManager.AddTextParameter("Target folder", "folderPath", "Folder to place image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item);
            pManager.AddTextParameter("Mapbox Access Token", "mbToken", "Mapbox Access Token string for access to Mapbox resources. Or set an Environment Variable 'HERONMAPOXTOKEN' with your token as the string.", GH_ParamAccess.item, "");
            pManager.AddBooleanParameter("Run", "get", "Go ahead and download imagery from the service", GH_ParamAccess.item, false);

            pManager[3].Optional = true;

            if (tilesOut) { Message = MapboxSource + " (tiled output)"; }
            else { Message = MapboxSource; }

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

            /*
            ///Save for later development of warping
            ///Setup for warping
            RESTful.GdalConfiguration.ConfigureGdal();
            RESTful.GdalConfiguration.ConfigureOgr();

            OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
            userSRS.SetFromUserInput("WGS84");

            OSGeo.OSR.SpatialReference mapboxSRS = new OSGeo.OSR.SpatialReference("");
            mapboxSRS.SetFromUserInput("EPSG:3857");

            ///Set transform from input spatial reference to Rhino spatial reference
            OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
            rhinoSRS.SetWellKnownGeogCS("WGS84");

            ///This transform moves and scales the points required in going from userSRS to XYZ and vice versa
            //Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);
            Transform modelToUserSRSTransform = Heron.Convert.GetModelToUserSRSTransform(userSRS);
            Transform mapboxSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(mapboxSRS);
            Transform modelToMapboxSRSTransform = Heron.Convert.GetModelToUserSRSTransform(mapboxSRS);
            */

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

                if (!tilesOut)
                {
                    //location of final image file
                    mapList.Append(new GH_String(imgPath), path);
                }

                //create cache folder for images
                string cacheLoc = folderPath + @"HeronCache\";
                List<string> cacheFilePaths = new List<string>();
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

                List<Rectangle3d> tileRectangles = new List<Rectangle3d>();

                //cycle through tiles to get bounding box
                for (int y = (int)y_range.Min; y <= y_range.Max; y++)
                {
                    for (int x = (int)x_range.Min; x <= x_range.Max; x++)
                    {
                        //add bounding box of tile to list
                        List<Point3d> boxPts = Convert.GetTileAsPolygon(zoom, y, x).ToList();
                        boxPtList.AddRange(boxPts);
                        string cacheFilePath = cacheLoc + mbSource.Replace(" ", "") + zoom + "-" + x + "-" + y + ".jpg";
                        cacheFilePaths.Add(cacheFilePath);

                        tileTotalCount = tileTotalCount + 1;

                        if (tilesOut)
                        {
                            mapList.Append(new GH_String(cacheFilePath), path);
                            Rectangle3d tileRectangle = BBoxToRect(new BoundingBox(boxPts));
                            tileRectangles.Add(tileRectangle);
                            imgFrame.Append(new GH_Rectangle(tileRectangle), path);
                        }
                    }
                }

                tCount.Insert(new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)"), path, 0);

                //bounding box of tile boundaries
                BoundingBox bbox = new BoundingBox(boxPtList);

                var rect = BBoxToRect(bbox);
                if (!tilesOut)
                {
                    imgFrame.Append(new GH_Rectangle(rect), path);
                }

                ///tile range as string for (de)serialization of TileCacheMeta
                string tileRangeString = zoom.ToString()
                    + x_range[0].ToString()
                    + y_range[0].ToString()
                    + x_range[1].ToString()
                    + y_range[1].ToString();

                ///check if the existing final image already covers the boundary. 
                ///if so, no need to download more or reassemble the cached tiles.

                if ((TileCacheMeta == tileRangeString) && Convert.CheckCacheImagesExist(cacheFilePaths))
                {
                    if (File.Exists(imgPath) && !tilesOut)
                    {
                        using (Bitmap imageT = new Bitmap(imgPath))
                        {
                            ///getting commments currently only working for JPG
                            ///TODO: get this to work for any image type or
                            ///find another way to check if the cached image covers the boundary.
                            string imgComment = string.Empty;

                            ///Save for later development of warping
                            //if (!warped)
                            imgComment = imageT.GetCommentsFromJPG();

                            imageT.Dispose();

                            ///check to see if tilerange in comments matches current tilerange
                            if (imgComment == (mbSource.Replace(" ", "") + tileRangeString))
                            {
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using existing image.");
                                AddPreviewItem(imgPath, boundary[i], rect);
                                continue;
                            }

                        }

                    }

                    if (tilesOut)
                    {
                        for (int t = 0; t < cacheFilePaths.Count; t++)
                        {
                            if (File.Exists(cacheFilePaths[t]))
                            {
                                AddPreviewItem(cacheFilePaths[t], tileRectangles[t].ToNurbsCurve(), tileRectangles[t]);
                            }
                        }
                        continue;
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

                /*
                ///Save for later development of warping
                List<GCP> gcpList = new List<GCP>();
                */

                using (Graphics g = Graphics.FromImage(finalImage))
                {
                    g.Clear(Color.Black);
                    for (int y = (int)y_range.Min; y <= (int)y_range.Max; y++)
                    {
                        for (int x = (int)x_range.Min; x <= (int)x_range.Max; x++)
                        {
                            ///create tileCache name 
                            string tileCache = mbSource.Replace(" ", "") + zoom + "-" + x + "-" + y + ".jpg";
                            string tileCacheLoc = cacheLoc + tileCache;

                            /*
                            ///Save for later development of warping
                            ///Get GCPs for warping 
                            Point3d tileCorner = Heron.Convert.GetTileAsPolygon(zoom, y, x)[3];
                            var tileCorner3857 = Convert.Point3dToOgrPoint(tileCorner, modelToMapboxSRSTransform);
                            GCP gcp = new GCP(tileCorner3857.GetX(0), tileCorner3857.GetY(0), tileCorner3857.GetZ(0), imgPosW * 512, imgPosH * 512, tileCorner3857.ToString(), zoom + x + y + "");
                            gcpList.Add(gcp);
                            */

                            ///check cache folder to see if tile image exists locally
                            if (File.Exists(tileCacheLoc))
                            {
                                Bitmap tmpImage = new Bitmap(Image.FromFile(tileCacheLoc));
                                ///add tmp image to final
                                g.DrawImage(tmpImage, imgPosW * 512, imgPosH * 512);
                                tmpImage.Dispose();
                            }

                            else
                            {
                                tileList.Add(new List<int> { zoom, y, x });
                                string urlAuth = Convert.GetZoomURL(x, y, zoom, mbURLauth);

                                Bitmap tmpImage = new Bitmap(512, 512);

                                System.Net.WebClient client = new System.Net.WebClient();
                                if (run == true)
                                {
                                    try
                                    {
                                        client.DownloadFile(urlAuth, tileCacheLoc);
                                        tmpImage = new Bitmap(Image.FromFile(tileCacheLoc));
                                    }
                                    catch (WebException e)
                                    {
                                        using (Graphics tmp = Graphics.FromImage(tmpImage)) { tmp.Clear(Color.White); }
                                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, e.Message);
                                    }
                                }
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

                    ///Save for later development of warping
                    ///if (!warped)
                    ///save the image
                    finalImage.Save(imgPath, ImageFormat.Jpeg);
                }

                /*
                ///Save for later development of warping
                byte[] imageBuffer;
                string memFilename = "/vsimem/inmemfile";
                string memTranslated = "/vsimem/inmemfileTranslated";
                string memWarped = "/vsimem/inmemfileWarped";
                using (MemoryStream stream = new MemoryStream())
                {
                    finalImage.Save(stream, ImageFormat.Jpeg);
                    imageBuffer = stream.ToArray();
                }
                */

                //garbage collection
                finalImage.Dispose();

                /*
                ///Save for later development of warping
                Gdal.FileFromMemBuffer(memFilename, imageBuffer);
                Dataset gdalImage = Gdal.Open(memFilename, Access.GA_ReadOnly);
                var upperLeft3857 = Convert.Point3dToOgrPoint(rect.Corner(3), modelToMapboxSRSTransform);
                var lowerRight3857 = Convert.Point3dToOgrPoint(rect.Corner(1), modelToMapboxSRSTransform);
                var upperLeft4326 = Convert.Point3dToOgrPoint(rect.Corner(3), modelToUserSRSTransform);
                var lowerRight4326 = Convert.Point3dToOgrPoint(rect.Corner(1), modelToUserSRSTransform);
                List<string> translateOptions = new List<string> { "-a_srs", "EPSG:3857", 
                    "-r", "bilinear",
                    "-a_ullr", upperLeft3857.GetX(0).ToString(), upperLeft3857.GetY(0).ToString(), lowerRight3857.GetX(0).ToString(), lowerRight3857.GetY(0).ToString() };
                Dataset gdalTranslated = Gdal.wrapper_GDALTranslate(memTranslated, gdalImage, new GDALTranslateOptions(translateOptions.ToArray()), null, null);

                var wkt = gdalTranslated.GetProjection();
                //gdalTranslated.SetGCPs(gcpList.ToArray(), wkt);

                List<string> warpOptions = new List<string> { "-t_srs", "EPSG:4326", 
                    "-r", "bilinear", 
                    //"-multi", 
                    //"-wo", "NUM_THREADS=6", 
                    "-overwrite", 
                    //"-order", "1",
                    //"-tps",
                    "-te_srs", "EPSG:3857",
                    "-te", upperLeft3857.GetX(0).ToString(), lowerRight3857.GetY(0).ToString(), lowerRight3857.GetX(0).ToString(), upperLeft3857.GetY(0).ToString() };

                ///https://github.com/OSGeo/gdal/issues/813
                ///https://lists.osgeo.org/pipermail/gdal-dev/2017-February/046046.html
                ///Odd way to go about setting source dataset in parameters for Warp is a known issue

                var ptr = new[] { Dataset.getCPtr(gdalTranslated).Handle };
                var gcHandle = GCHandle.Alloc(ptr, GCHandleType.Pinned);
                try
                {
                    var dss = new SWIGTYPE_p_p_GDALDatasetShadow(gcHandle.AddrOfPinnedObject(), false, null);
                    Dataset gdalWarped = Gdal.wrapper_GDALWarpDestName(memWarped, 1, dss, new GDALWarpAppOptions(warpOptions.ToArray()), null, null);
                    var driver = Gdal.GetDriverByName("JPEG");
                    List<string> copyOptions = new List<string> { "QUALITY=95", "COMMENT=" + mbSource.Replace(" ", "") + tileRangeString};
                    var copy = driver.CreateCopy(imgPath, gdalWarped, 0, copyOptions.ToArray(), null, null);
                    copy.Dispose();
                    driver.Dispose();
                    gdalWarped.Dispose();
                }
                finally
                {
                    if (gcHandle.IsAllocated)
                        gcHandle.Free();
                }

                gdalImage.Dispose();
                gdalTranslated.Dispose();
                Gdal.Unlink(memFilename);
                Gdal.Unlink(memTranslated);
                Gdal.Unlink(memWarped);
                */

                if (!tilesOut)
                {
                    AddPreviewItem(imgPath, boundary[i], rect);
                }
                else
                {
                    for (int t = 0; t < cacheFilePaths.Count; t++)
                    {
                        if (File.Exists(cacheFilePaths[t]))
                        {
                            AddPreviewItem(cacheFilePaths[t], tileRectangles[t].ToNurbsCurve(), tileRectangles[t]);
                        }
                    }
                }

                //add to tile count total
                tCount.Insert(new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)"), path, 0);

                //write out new tile range metadata for serialization
                TileCacheMeta = tileRangeString;

            }

            List<string> mbAtts = new List<string> { "© Mapbox, © OpenStreetMap", "https://www.mapbox.com/about/maps/", "http://www.openstreetmap.org/copyright" };

            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, tCount);
            DA.SetDataList(3, mbAtts);

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

            ToolStripMenuItem item = Menu_AppendItem(menu, "Tiled output", Menu_TiledOutputChecked, true, TilesOut);
            item.ToolTipText = "If 'Tiled output' is selected, Image File and Image Frame will output each tile " +
                "that is used to build the assembled image instead of the assembled image itself.  " +
                "The tiled output will avoid distortions in the assembled image at lower zoom levels.";
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

        private void Menu_TiledOutputChecked(object sender, EventArgs e)
        {
            RecordUndoEvent("TilesOut");
            TilesOut = !TilesOut;
            ExpireSolution(true);
        }



        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private string tCacheMeta = string.Empty;
        private string mbSourceList = Convert.GetEnpoints();
        private string mbSource = JObject.Parse(Convert.GetEnpoints())["Mapbox Maps"][0]["service"].ToString();
        private string mbURL = JObject.Parse(Convert.GetEnpoints())["Mapbox Maps"][0]["url"].ToString();
        private bool tilesOut = false;

        public bool TilesOut
        {
            get { return tilesOut; }
            set
            {
                tilesOut = value;
                if (tilesOut) { Message = MapboxSource + " (tiled output)"; }
                else { Message = MapboxSource; }
            }
        }

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
            writer.SetBoolean("TilesOut", TilesOut);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            TileCacheMeta = reader.GetString("TileCacheMeta");
            MapboxSourceList = reader.GetString("MapboxSourceList");
            MapboxSource = reader.GetString("MapboxSource");
            MapboxURL = reader.GetString("MapboxURL");
            TilesOut = reader.GetBoolean("TilesOut");
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
            get { return new Guid("25A6F4C0-4A0F-4B13-8923-3875C8C51325"); }
        }
    }
}