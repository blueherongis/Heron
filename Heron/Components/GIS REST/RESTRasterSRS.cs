using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace Heron
{
    public class RESTRasterSRS : HeronRasterPreviewComponent
    {
        //Class Constructor
        public RESTRasterSRS() : base("Get REST Raster", "RESTRaster", "Get raster imagery from ArcGIS REST Services.  " +
            "Use the SetSRS component to set the spatial reference system used by this component.", "GIS REST")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Resolution", "resolution", "Maximum resolution for images", GH_ParamAccess.item, 1024);
            pManager.AddTextParameter("Target Folder", "folderPath", "Folder to save image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item, "restRaster");
            pManager.AddTextParameter("REST URL", "URL", "ArcGIS REST Service website to query. Use the component \nmenu item \"Create REST Raster Source List\" for some examples.", GH_ParamAccess.item);
            pManager.AddTextParameter("Image Type", "imageType", "Image file type to download from the service.  " +
                "Some REST raster services have a range of available types including jpg, tif, png, png32, svg.", GH_ParamAccess.item, "jpg");
            pManager.AddBooleanParameter("Run", "run", "Go ahead and download imagery from the Service", GH_ParamAccess.item, false);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Image", "Image", "File location of downloaded image", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Image Frame", "imageFrame", "Bounding box of image for mapping to geometry", GH_ParamAccess.tree);
            pManager.AddTextParameter("REST Query", "RESTQuery", "Full text of REST query", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            int Res = -1;
            DA.GetData<int>("Resolution", ref Res);

            string folderPath = string.Empty;
            DA.GetData<string>("Target Folder", ref folderPath);
            if (!folderPath.EndsWith(@"\")) { folderPath = folderPath + @"\"; }

            string prefix = string.Empty;
            DA.GetData<string>("Prefix", ref prefix);

            string URL = string.Empty;
            DA.GetData<string>("REST URL", ref URL);
            if (URL.EndsWith(@"/")) { URL = URL + "export?"; }

            string imageType = string.Empty;
            DA.GetData<string>("Image Type", ref imageType);

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();
            RESTful.GdalConfiguration.ConfigureGdal();

            ///Set transform from input spatial reference to Heron spatial reference
            ///TODO: verify the userSRS is valid
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + heronSRSInt;

            ///Apply EAP to HeronSRS
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
            Transform heronToUserSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);


            GH_Structure<GH_String> mapList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> mapquery = new GH_Structure<GH_String>();
            GH_Structure<GH_Rectangle> imgFrame = new GH_Structure<GH_Rectangle>();

            FileInfo file = new FileInfo(folderPath);
            file.Directory.Create();

            string size = string.Empty;
            if (Res > 0)
            {
                size = "&size=" + Res + "%2C" + Res;
            }

            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path path = new GH_Path(i);

                ///Get image frame for given boundary
                BoundingBox imageBox = boundary[i].GetBoundingBox(false);
                imageBox.Transform(heronToUserSRSTransform);

                double proportion = imageBox.Diagonal.X / imageBox.Diagonal.Y;
                if (proportion > 1) { size = "&size=" + Res + "%2C" + Res / proportion; }
                else { size = "&size=" + Res * proportion + "%2C" + Res; }

                ///Make sure to have a rect for output
                Rectangle3d rect = new Rectangle3d();

                ///Query the REST service
                string restquery = URL +
                  ///legacy method for creating bounding box string
                  "bbox=" + imageBox.Min.X + "%2C" + imageBox.Min.Y + "%2C" + imageBox.Max.X + "%2C" + imageBox.Max.Y +
                  "&bboxSR=" + heronSRSInt +
                  size + //"&layers=&layerdefs=" +
                  "&imageSR=" + heronSRSInt + //"&transparent=false&dpi=&time=&layerTimeOptions=" +
                  "&format=" + imageType;
                string restqueryJSON = restquery + "&f=json";
                string restqueryImage = restquery + "&f=image";

                mapquery.Append(new GH_String(restqueryImage), path);

                string result = string.Empty;
                string imageTypeShort = imageType;

                if (run)
                {
                    ///Get extent of image from arcgis rest service as JSON
                    try
                    {
                        result = Heron.Convert.HttpToJson(restqueryJSON);

                        JObject jObj = JsonConvert.DeserializeObject<JObject>(result);
                        if (!jObj.ContainsKey("href"))
                        {
                            restqueryJSON = restqueryJSON.Replace("export?", "exportImage?");
                            restqueryImage = restqueryImage.Replace("export?", "exportImage?");
                            mapquery.RemovePath(path);
                            mapquery.Append(new GH_String(restqueryImage), path);
                            result = Heron.Convert.HttpToJson(restqueryJSON);
                            jObj = JsonConvert.DeserializeObject<JObject>(result);
                        }

                        if (jObj["extent"] != null)
                        {
                            Point3d extMin = new Point3d((double)jObj["extent"]["xmin"], (double)jObj["extent"]["ymin"], 0);
                            Point3d extMax = new Point3d((double)jObj["extent"]["xmax"], (double)jObj["extent"]["ymax"], 0);
                            rect = new Rectangle3d(Plane.WorldXY, extMin, extMax);
                            rect.Transform(userSRSToModelTransform);
                        }

                        ///Download image from source
                        ///Catch if JSON query throws an error
                        string imageQueryJSON = "";
                        if (jObj["href"] != null)
                        {
                            imageQueryJSON = jObj["href"].ToString();
                        }

                        ///Clean up file name for png32 png16 png8 geotiff tiff

                        if (imageTypeShort.EndsWith("32")) { imageTypeShort = imageTypeShort.Remove(imageTypeShort.LastIndexOf("32")); }
                        if (imageTypeShort.EndsWith("16")) { imageTypeShort = imageTypeShort.Remove(imageTypeShort.LastIndexOf("16")); }
                        if (imageTypeShort.EndsWith("8")) { imageTypeShort = imageTypeShort.Remove(imageTypeShort.LastIndexOf("8")); }
                        if (imageTypeShort.EndsWith("geotiff", true, null))
                        {
                            imageTypeShort = imageTypeShort.Remove(imageTypeShort.LastIndexOf("geotiff"));
                            imageTypeShort = imageTypeShort + "tif";
                        }
                        if (imageTypeShort.EndsWith("tiff", true, null))
                        {
                            imageTypeShort = imageTypeShort.Remove(imageTypeShort.LastIndexOf("tiff"));
                            imageTypeShort = imageTypeShort + "tif";
                        }

                        ///If the image link from the JSON response doesn't work, fallback to the original image REST query
                        ///Replaces the WebClient process from original RESTRaster component with HttpWebResponse
                        string imageDownloaded = "";

                        if (!String.IsNullOrEmpty(imageQueryJSON))
                        {
                            imageDownloaded = Heron.Convert.DownloadHttpImage(imageQueryJSON, folderPath + prefix + "_" + i + "." + imageTypeShort);
                            if (!String.IsNullOrEmpty(imageDownloaded))
                            {
                                imageDownloaded = Heron.Convert.DownloadHttpImage(restqueryImage, folderPath + prefix + "_" + i + "." + imageTypeShort);
                            }
                        }
                        else
                        {
                            imageDownloaded = Heron.Convert.DownloadHttpImage(restqueryImage, folderPath + prefix + "_" + i + "." + imageTypeShort);
                        }

                        if (!String.IsNullOrEmpty(imageDownloaded))
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, imageDownloaded);
                        }

                    }
                    catch (Exception e)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, e.Message);
                        DA.SetDataTree(2, mapquery);
                        return;
                    }


                    var bitmapPath = folderPath + prefix + "_" + i + "." + imageTypeShort;
                    mapList.Append(new GH_String(bitmapPath), path);

                    if (rect.IsValid)
                    {
                        imgFrame.Append(new GH_Rectangle(rect), path);
                        AddPreviewItem(bitmapPath, rect);
                    }
                }
            }

            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, mapquery);

        }




        private JObject rasterJson = JObject.Parse(Heron.Convert.GetEnpoints());

        /// <summary>
        /// Adds to the context menu an option to create a pre-populated list of common REST Raster sources
        /// </summary>
        /// <param name="menu"></param>
        /// https://discourse.mcneel.com/t/generated-valuelist-not-working/79406/6?u=hypar
        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            var rasterSourcesJson = rasterJson["REST Raster"].Select(x => x["source"]).Distinct();
            List<string> rasterSources = rasterSourcesJson.Values<string>().ToList();
            foreach (var src in rasterSourcesJson)
            {
                ToolStripMenuItem root = GH_DocumentObject.Menu_AppendItem(menu, "Create " + src.ToString() + " Source List", CreateRasterList);
                root.ToolTipText = "Click this to create a pre-populated list of some " + src.ToString() + " sources.";
                base.AppendAdditionalMenuItems(menu);
            }
        }

        /// <summary>
        /// Creates a value list pre-populated with possible accent colors and adds it to the Grasshopper Document, located near the component pivot.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void CreateRasterList(object sender, System.EventArgs e)
        {
            string source = sender.ToString();
            source = source.Replace("Create ", "");
            source = source.Replace(" Source List", "");

            GH_DocumentIO docIO = new GH_DocumentIO();
            docIO.Document = new GH_Document();

            ///Initialize object
            GH_ValueList vl = new GH_ValueList();

            ///Clear default contents
            vl.ListItems.Clear();

            foreach (var service in rasterJson["REST Raster"])
            {
                if (service["source"].ToString() == source)
                {
                    GH_ValueListItem vi = new GH_ValueListItem(service["service"].ToString(), String.Format("\"{0}\"", service["url"].ToString()));
                    vl.ListItems.Add(vi);
                }
            }

            ///Set component nickname
            vl.NickName = source;

            ///Get active GH doc
            GH_Document doc = OnPingDocument();
            if (docIO.Document == null) return;

            ///Place the object
            docIO.Document.AddObject(vl, false, 1);

            ///Get the pivot of the "URL" param
            PointF currPivot = Params.Input[4].Attributes.Pivot;

            ///Set the pivot of the new object
            vl.Attributes.Pivot = new PointF(currPivot.X - 400, currPivot.Y - 11);

            docIO.Document.SelectAll();
            docIO.Document.ExpireSolution();
            docIO.Document.MutateAllIds();
            IEnumerable<IGH_DocumentObject> objs = docIO.Document.Objects;
            doc.DeselectAll();
            doc.UndoUtil.RecordAddObjectEvent("Create REST Raster Source List", objs);
            doc.MergeDocument(docIO.Document);
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
            get { return new Guid("{4E3E3725-B8C0-4B2E-A488-DD19B213624E}"); }
        }
    }
}
