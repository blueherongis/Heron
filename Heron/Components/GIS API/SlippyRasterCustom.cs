
using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Net;
using System.Windows.Forms;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using GH_IO.Serialization;

using Newtonsoft.Json.Linq;
using Rhino.DocObjects;
using Rhino.Render;
using Rhino;

namespace Heron
{
    public class SlippyRasterCustom : HeronRasterPreviewComponent
    {
        /// <summary>
        /// Initializes a new instance of the SlippyRaster class.
        /// </summary>
        public SlippyRasterCustom()
          : base("Slippy Raster Custom", "SRC", "Get raster imagery from a tile-based map service of your choosing. The service URL must include {Z} {X} and {Y} to be valid. ", "GIS API")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {

            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Zoom Level", "zoom", "Slippy map zoom level. Higher zoom level is higher resolution, but takes longer to download. Max zoom is typically 19.", GH_ParamAccess.item);
            pManager.AddTextParameter("Folder Path", "folderPath", "Folder to place image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item,"CustomSlippy");
            pManager.AddTextParameter("Slippy Access Header", "userAgent", "A user-agent header is sometimes required for access to Slippy resources, especially OSM. This can be any string.", GH_ParamAccess.item, "");
            pManager.AddTextParameter("Custom Slippy Map URL", "url", "A slippy map service URL which must include '{z}' '{x}' and '{y} placeholders (lower case)'.  " +
                "For example https://tile.openstreetmap.org/{z}/{x}/{y}.png", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "get", "Go ahead and download imagery from the service", GH_ParamAccess.item, false);

            if (tilesOut) { Message = "Tiled Output"; }

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Image File", "Image", "File location of downloaded image", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Image Frame", "imageFrame", "Bounding box of image for mapping to geometry", GH_ParamAccess.tree);
            pManager.AddTextParameter("Tile Count", "tileCount", "Number of image tiles resulting from Slippy query", GH_ParamAccess.tree);

            pManager.AddTextParameter("Slippy Attribution", "slippyAtt", "Include the slippy word mark and text attribution as required by the service", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList(0, boundary);

            int zoom = -1;
            DA.GetData(1, ref zoom);

            string folderPath = string.Empty;
            DA.GetData(2, ref folderPath);
            if (!Directory.Exists(folderPath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Folder " + folderPath + " does not exist.  Using your system's temp folder " + Path.GetTempPath() + " instead.");
                folderPath = Path.GetTempPath();
            }

            string prefix = string.Empty;
            DA.GetData(3, ref prefix);

            string userAgent = string.Empty;
            DA.GetData(4, ref userAgent);

            string URL = "";
            DA.GetData(5, ref URL);
            if (!URL.Contains("{z}")|| !URL.Contains("{x}") || !URL.Contains("{y}"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The URL does not contain the proper {z} {x} and/or {y} formatting (lower case).  " +
                    "Make sure the URL looks something like https://tile.openstreetmap.org/{z}/{x}/{y}.png");
                return;
            }

            string tileImageFormat = ".png";
            var finalImageFormat = System.Drawing.Imaging.ImageFormat.Png;
            int tileImageResolution = 256;

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            GH_Structure<GH_String> mapList = new GH_Structure<GH_String>();
            GH_Structure<GH_Rectangle> imgFrame = new GH_Structure<GH_Rectangle>();
            GH_Structure<GH_String> tCount = new GH_Structure<GH_String>();

            ///Reset lists for baking
            rects = new List<Rectangle3d>();
            bounds = new List<Curve>();
            bitmapPaths = new List<string>();


            for (int i = 0; i < boundary.Count; i++)
            {
                GH_Path path = new GH_Path(i);
                int tileTotalCount = 0;
                int tileDownloadedCount = 0;


                ///Get image frame for given boundary and  make sure it's valid
                if (!boundary[i].GetBoundingBox(true).IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                    return;
                }
                BoundingBox boundaryBox = boundary[i].GetBoundingBox(true);

                ///TODO: look into scaling boundary to get buffer tiles

                ///File path for final image
                string imgPath = Path.Combine(folderPath, prefix + "_" + i + "." + finalImageFormat.ToString().ToLower());

                if (!tilesOut)
                {
                    ///Location of final image file
                    mapList.Append(new GH_String(imgPath), path);
                }

                ///Create cache folder for images
                string cacheLoc = Path.Combine(folderPath, "HeronCache");
                string slippyImageTileRange = Path.Combine(cacheLoc, prefix + "_" + i + ".txt");
                List<string> cacheFilePaths = new List<string>();
                if (!Directory.Exists(cacheLoc))
                {
                    Directory.CreateDirectory(cacheLoc);
                }

                ///Tile bounding box array
                List<Point3d> boxPtList = new List<Point3d>();

                ///Get the tile coordinates for all tiles within boundary
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

                ///Cycle through tiles to get bounding box
                for (int y = (int)y_range.Min; y <= y_range.Max; y++)
                {
                    for (int x = (int)x_range.Min; x <= x_range.Max; x++)
                    {
                        ///Add bounding box of tile to list
                        List<Point3d> boxPts = Convert.GetTileAsPolygon(zoom, y, x).ToList();
                        boxPtList.AddRange(Convert.GetTileAsPolygon(zoom, y, x).ToList());
                        string cacheFilePath = Path.Combine(cacheLoc, prefix.Replace(" ", "") + "Tile" + zoom + "-" + x + "-" + y + tileImageFormat);
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

                ///Bounding box of tile boundaries
                BoundingBox bbox = new BoundingBox(boxPtList);

                var rect = BBoxToRect(bbox);
                if (!tilesOut)
                {
                    imgFrame.Append(new GH_Rectangle(rect), path);
                }

                ///Tile range as string for (de)serialization of TileCacheMeta
                string tileRangeString = zoom.ToString()
                    + x_range[0].ToString()
                    + y_range[0].ToString()
                    + x_range[1].ToString()
                    + y_range[1].ToString();

                ///Check if the existing final image already covers the boundary. 
                ///If so, no need to download more or reassemble the cached tiles.
                if ((TileCacheMeta == tileRangeString) && Convert.CheckCacheImagesExist(cacheFilePaths))
                {
                    if (File.Exists(imgPath) && !tilesOut)
                    {
                        using (StreamReader sr = File.OpenText(slippyImageTileRange))
                        {
                            string imgComment = sr.ReadToEnd();

                            ///check to see if tilerange in comments matches current tilerange
                            if (imgComment == (prefix.Replace(" ", "") + tileRangeString))
                            {
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using existing image.");
                                AddPreviewItem(imgPath, boundary[i], rect);

                                ///For baking
                                bitmapPaths.Add(imgPath);
                                bounds.Add(boundary[i]);
                                rects.Add(rect);

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
                                
                                ///For baking
                                bitmapPaths.Add(cacheFilePaths[t]);
                                bounds.Add(tileRectangles[t].ToNurbsCurve());
                                rects.Add(tileRectangles[t]);
                            }
                        }
                        continue;
                    }

                }



                ///Query Slippy URL
                ///download all tiles within boundary
                ///merge tiles into one bitmap
                ///API to query


                ///Do the work of assembling image
                ///setup final image container bitmap
                int fImageW = ((int)x_range.Length + 1) * tileImageResolution;
                int fImageH = ((int)y_range.Length + 1) * tileImageResolution;
                Bitmap finalImage = new Bitmap(fImageW, fImageH);


                int imgPosW = 0;
                int imgPosH = 0;

                using (Graphics g = Graphics.FromImage(finalImage))
                {
                    g.Clear(Color.Black);
                    for (int y = (int)y_range.Min; y <= (int)y_range.Max; y++)
                    {
                        for (int x = (int)x_range.Min; x <= (int)x_range.Max; x++)
                        {
                            ///Create tileCache name 
                            string tileCache = prefix.Replace(" ", "") + "Tile" + zoom + "-" + x + "-" + y + tileImageFormat;
                            string tileCacheLoc = Path.Combine(cacheLoc, tileCache);

                            ///Check cache folder to see if tile image exists locally
                            if (File.Exists(tileCacheLoc))
                            {
                                Bitmap tmpImage = new Bitmap(Image.FromFile(tileCacheLoc));
                                ///Add tmp image to final
                                g.DrawImage(tmpImage, imgPosW * tileImageResolution, imgPosH * tileImageResolution);
                                tmpImage.Dispose();
                            }

                            else
                            {
                                tileList.Add(new List<int> { zoom, y, x });
                                string urlAuth = Convert.GetZoomURL(x, y, zoom, URL);

                                Bitmap tmpImage = new Bitmap(tileImageResolution, tileImageResolution);
                                System.Net.WebClient client = new System.Net.WebClient();

                                ///Insert header if required
                                client.Headers.Add("user-agent", userAgent);
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

                                ///Add tmp image to final
                                g.DrawImage(tmpImage, imgPosW * tileImageResolution, imgPosH * tileImageResolution);
                                tmpImage.Dispose();
                                tileDownloadedCount = tileDownloadedCount + 1;
                            }

                            ///Increment x insert position, goes left to right
                            imgPosW++;
                        }
                        ///Increment y insert position, goes top to bottom
                        imgPosH++;
                        imgPosW = 0;

                    }
                    ///Garbage collection
                    g.Dispose();

                    ///Add tile range meta data to image comments
                    if (File.Exists(slippyImageTileRange)) File.Delete(slippyImageTileRange);
                    using (StreamWriter sw = File.CreateText(slippyImageTileRange))
                    {
                        sw.Write((prefix.Replace(" ", "") + tileRangeString));
                        sw.Dispose();
                    }

                    ///Save the image
                    finalImage.Save(imgPath, System.Drawing.Imaging.ImageFormat.Png);
                }

                ///Garbage collection
                finalImage.Dispose();

                if (!tilesOut)
                {
                    AddPreviewItem(imgPath, boundary[i], rect);
                    
                    ///For baking
                    bitmapPaths.Add(imgPath);
                    bounds.Add(boundary[i]);
                    rects.Add(rect);
                }
                else
                {
                    for (int t = 0; t < cacheFilePaths.Count; t++)
                    {
                        if (File.Exists(cacheFilePaths[t]))
                        {
                            AddPreviewItem(cacheFilePaths[t], tileRectangles[t].ToNurbsCurve(), tileRectangles[t]);
                            
                            ///For baking
                            bitmapPaths.Add(cacheFilePaths[t]);
                            bounds.Add(tileRectangles[t].ToNurbsCurve());
                            rects.Add(tileRectangles[t]);
                        }
                    }
                }

                //add to tile count total
                tCount.Insert(new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)"), path, 0);

                //write out new tile range metadata for serialization
                TileCacheMeta = tileRangeString;

            }

            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, tCount);
            ///Add copyright info here
            DA.SetDataList(3, "");

        }

        /////////////////////////





        ///Menu items
        ///https://www.grasshopper3d.com/forum/topics/closing-component-popup-side-bars-when-clicking-outside-the-form
        ///


        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem item = Menu_AppendItem(menu, "Tiled output", Menu_TiledOutputChecked, true, TilesOut);
            item.ToolTipText = "If 'Tiled output' is selected, Image File and Image Frame will output each tile " +
                "that is used to build the assembled image instead of the assembled image itself.  " +
                "The tiled output will avoid distortions in the assembled image at lower zoom levels.";

            ToolStripMenuItem bakePreview = new ToolStripMenuItem("Bake Preview");
            bakePreview.ToolTipText = "Bake this component's preview image(s) to the current layer in Rhino.  A new material is created with each bake.";
            bakePreview.Click += BakePreview;
            menu.Items.Add(bakePreview);

            base.AppendAdditionalComponentMenuItems(menu);
        }

        private void Menu_TiledOutputChecked(object sender, EventArgs e)
        {
            RecordUndoEvent("TilesOut");
            TilesOut = !TilesOut;
            ExpireSolution(true);
        }

        ///Add the ability to bake the preview
        private List<Rectangle3d> rects = new List<Rectangle3d>();
        private List<Curve> bounds = new List<Curve>();
        private List<string> bitmapPaths = new List<string>();
        private void BakePreview(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            var rhinoDoc = RhinoDoc.ActiveDoc;
            var undoRecord = rhinoDoc.BeginUndoRecord("Bake Heron preview mesh and material from Grasshopper");

            for (int r = 0; r < rects.Count; r++)
            {
                ///Add a unique material to the document based on the preview bitmap
                string matName = "Heron_" + Path.GetFileNameWithoutExtension(bitmapPaths[r]) + "_Preview";
                int increment = rhinoDoc.Materials.Where(s => s.Name.Contains(matName)).Count();
                matName = matName + "-" + increment;
                string previewPath = Path.Combine(Path.GetDirectoryName(bitmapPaths[r]),
                    Path.GetFileNameWithoutExtension(bitmapPaths[r]) + "_Preview-" + increment +
                    Path.GetExtension(bitmapPaths[r]));
                File.Copy(bitmapPaths[r], previewPath, true);

                int matIndex = rhinoDoc.Materials.Add();
                Material previewMat = rhinoDoc.Materials[matIndex];
                previewMat.Name = matName;
                previewMat.SetBitmapTexture(previewPath);
                bool worked = rhinoDoc.Materials.Modify(previewMat, matIndex, true);

                ///Add the preview mesh to the rhino doc
                var rect = rects[r];

                var mesh = Mesh.CreateFromPlanarBoundary(rect.ToNurbsCurve(), MeshingParameters.FastRenderMesh, 0.1);
                TextureMapping tm = TextureMapping.CreatePlaneMapping(rect.Plane, rect.X, rect.Y, new Interval(-1, 1));
                mesh.SetTextureCoordinates(tm, Transform.Identity, true);

                var att = new ObjectAttributes();
                att.MaterialIndex = matIndex;
                att.MaterialSource = ObjectMaterialSource.MaterialFromObject;

                Guid guid = rhinoDoc.Objects.AddMesh(mesh, att);
            }
            rhinoDoc.EndUndoRecord(undoRecord);
        }


        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private string tCacheMeta = string.Empty;
        private bool tilesOut = false;

        public bool TilesOut
        {
            get { return tilesOut; }
            set
            {
                tilesOut = value;
                if (tilesOut) { Message = "Tiled Output"; }
                else { Message = ""; }
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

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("TileCacheMeta", TileCacheMeta);
            writer.SetBoolean("TilesOut", TilesOut);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            TileCacheMeta = reader.GetString("TileCacheMeta");
            TilesOut = reader.GetBoolean("TilesOut");
            return base.Read(reader);
        }


        /////////////////////////

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override Bitmap Icon
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
            get { return new Guid("CD972333-9375-44C8-A5DE-AB58CA893512"); }
        }
    }
}