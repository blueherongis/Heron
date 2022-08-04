using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json.Linq;
using OSGeo.OGR;
using OSGeo.OSR;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;


namespace Heron
{
    public class MapboxVector : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MapboxTopo class.
        /// </summary>
        public MapboxVector() : base("Mapbox Vector", "MapboxVector", "Get vector data from a Mapbox service. Requires a Mapbox Token.", "GIS API")
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

            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for Mapbox vector tiles", GH_ParamAccess.item);
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
            pManager.AddCurveParameter("Extents", "extents", "Bounding box of all the vector data features", GH_ParamAccess.item);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the vector data features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry contained in the feature", GH_ParamAccess.tree);
            //https://www.mapbox.com/help/how-attribution-works/
            //https://www.mapbox.com/api-documentation/#retrieve-an-html-slippy-map Retrieve TileJSON metadata
            pManager.AddTextParameter("Mapbox Attribution", "mbAtt", "Mapbox word mark and text attribution required by Mapbox", GH_ParamAccess.list);
            pManager.AddTextParameter("Geometry Type", "geoType", "Type of geometry contained in the feature", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Buildings", "buildings", "Geometry of buildings contained in the feature", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Tile Extents", "tiles", "Map tile boundaries for each Mapbox vector tile", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve boundary = null;
            DA.GetData<Curve>(0, ref boundary);

            int zoom = -1;
            DA.GetData<int>(1, ref zoom);

            string filePath = string.Empty;
            DA.GetData<string>(2, ref filePath);
            if (!filePath.EndsWith(@"\")) filePath = filePath + @"\";

            string prefix = string.Empty;
            DA.GetData<string>(3, ref prefix);
            if (prefix == "")
            {
                prefix = mbSource;
            }

            string URL = mbURL;

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


            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();
            RESTful.GdalConfiguration.ConfigureGdal();


            GH_Curve imgFrame;
            GH_String tCount;
            GH_Structure<GH_String> fnames = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fvalues = new GH_Structure<GH_String>();
            GH_Structure<IGH_GeometricGoo> gGoo = new GH_Structure<IGH_GeometricGoo>();
            GH_Structure<GH_String> gtype = new GH_Structure<GH_String>();
            GH_Structure<IGH_GeometricGoo> gGooBuildings = new GH_Structure<IGH_GeometricGoo>();



            int tileTotalCount = 0;
            int tileDownloadedCount = 0;

            ///Get image frame for given boundary
            if (!boundary.GetBoundingBox(true).IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                return;
            }
            BoundingBox boundaryBox = boundary.GetBoundingBox(true);

            //create cache folder for vector tiles
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
            var x_range = ranges.XRange;
            var y_range = ranges.YRange;

            if (x_range.Length > 100 || y_range.Length > 100)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "This tile range is too big (more than 100 tiles in the x or y direction). Check your units.");
                return;
            }

            ///cycle through tiles to get bounding box
            List<Polyline> tileExtents = new List<Polyline>();
            List<double> tileHeight = new List<double>();
            List<double> tileWidth = new List<double>();

            for (int y = (int)y_range.Min; y <= y_range.Max; y++)
            {
                for (int x = (int)x_range.Min; x <= x_range.Max; x++)
                {
                    //add bounding box of tile to list for translation
                    Polyline tileExtent = Heron.Convert.GetTileAsPolygon(zoom, y, x);
                    tileExtents.Add(tileExtent);
                    tileWidth.Add(tileExtent[0].DistanceTo(tileExtent[1]));
                    tileHeight.Add(tileExtent[1].DistanceTo(tileExtent[2]));

                    boxPtList.AddRange(tileExtent.ToList());
                    cachefilePaths.Add(cacheLoc + mbSource.Replace(" ", "") + zoom + "-" + x + "-" + y + ".mvt");
                    tileTotalCount = tileTotalCount + 1;
                }
            }

            tCount = new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)");

            ///bounding box of tile boundaries
            BoundingBox bboxPts = new BoundingBox(boxPtList);

            ///convert bounding box to polyline
            List<Point3d> imageCorners = bboxPts.GetCorners().ToList();
            imageCorners.Add(imageCorners[0]);
            imgFrame = new GH_Curve(new Rhino.Geometry.Polyline(imageCorners).ToNurbsCurve());

            ///tile range as string for (de)serialization of TileCacheMeta
            string tileRangeString = "Tile range for zoom " + zoom.ToString() + ": "
                + x_range[0].ToString() + "-"
                + y_range[0].ToString() + " to "
                + x_range[1].ToString() + "-"
                + y_range[1].ToString();
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, tileRangeString);

            ///Query Mapbox URL
            ///download all tiles within boundary

            ///API to query
            string mbURLauth = mbURL + mbToken;

            if (run == true)
            {
                for (int y = (int)y_range.Min; y <= (int)y_range.Max; y++)
                {
                    for (int x = (int)x_range.Min; x <= (int)x_range.Max; x++)
                    {
                        //create tileCache name 
                        string tileCache = mbSource.Replace(" ", "") + zoom + "-" + x + "-" + y + ".mvt";
                        string tileCacheLoc = cacheLoc + tileCache;

                        //check cache folder to see if tile image exists locally
                        if (File.Exists(tileCacheLoc))
                        {

                        }

                        else
                        {
                            string urlAuth = Heron.Convert.GetZoomURL(x, y, zoom, mbURLauth);
                            System.Net.WebClient client = new System.Net.WebClient();
                            client.DownloadFile(urlAuth, tileCacheLoc);
                            client.Dispose();

                            ///https://gdal.org/development/rfc/rfc59.1_utilities_as_a_library.html
                            ///http://osgeo-org.1560.x6.nabble.com/gdal-dev-How-to-convert-shapefile-to-geojson-using-c-bindings-td5390953.html#a5391028
                            ///ogr2ogr is slow
                            //OSGeo.GDAL.Dataset httpDS = OSGeo.GDAL.Gdal.OpenEx("MVT:"+urlAuth,4,null,null,null);
                            //var transOptions = new OSGeo.GDAL.GDALVectorTranslateOptions(new[] { "-s_srs","EPSG:3857", "-t_srs", "EPSG:4326","-skipfailures" });
                            //var transDS = OSGeo.GDAL.Gdal.wrapper_GDALVectorTranslateDestName(mvtLoc + zoom + "-" + x + "-" + y , httpDS, transOptions, null, null);
                            //httpDS.Dispose();
                            //transDS.Dispose();

                            tileDownloadedCount = tileDownloadedCount + 1;
                        }
                    }
                }
            }

            //add to tile count total
            tCount = new GH_String(tileTotalCount + " tiles (" + tileDownloadedCount + " downloaded / " + (tileTotalCount - tileDownloadedCount) + " cached)");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, tCount.ToString());


            ///Build a VRT file
            ///https://stackoverflow.com/questions/55386597/gdal-c-sharp-wrapper-for-vrt-doesnt-write-a-vrt-file

            //string vrtFile = cacheLoc + "mapboxvector.vrt";
            //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, vrtFile);
            //var vrtOptions = new OSGeo.GDAL.GDALBuildVRTOptions(new[] { "-overwrite" });
            //var vrtDataset = OSGeo.GDAL.Gdal.wrapper_GDALBuildVRT_names(vrtFile, cachefilePaths.ToArray(), vrtOptions, null, null);
            //vrtDataset.Dispose();


            ///Set transform from input spatial reference to Rhino spatial reference
            ///TODO: look into adding a step for transforming to CRS set in SetCRS 
            OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
            rhinoSRS.SetWellKnownGeogCS("WGS84");

            ///TODO: verify the userSRS is valid
            ///TODO: use this as override of global SetSRS
            OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
            //userSRS.SetFromUserInput(userSRStext);
            userSRS.SetFromUserInput("WGS84");


            OSGeo.OSR.SpatialReference sourceSRS = new SpatialReference("");
            sourceSRS.SetFromUserInput("EPSG:3857");

            ///These transforms move and scale in order to go from userSRS to XYZ and vice versa
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);
            Transform modelToUserSRSTransform = Heron.Convert.GetModelToUserSRSTransform(userSRS);
            Transform sourceToModelSRSTransform = Heron.Convert.GetUserSRSToModelTransform(sourceSRS);
            Transform modelToSourceSRSTransform = Heron.Convert.GetModelToUserSRSTransform(sourceSRS);

            //OSGeo.GDAL.Driver gdalOGR = OSGeo.GDAL.Gdal.GetDriverByName("VRT");
            //var ds = OSGeo.GDAL.Gdal.OpenEx(vrtFile, 4, ["VRT","MVT"], null, null);

            ///Establish known layers by which to organize the data
            List<string> layerNameList = new List<string> { "admin", "aeroway","building","landuse_overlay","landuse","motorway_junction",
                "road","structure","water","waterway","airport_label","housenum_label","natural_label","place_label","poi_label","transit_stop_label" };

            int t = 0;

            foreach (string mvtTile in cachefilePaths)
            {

                OSGeo.OGR.Driver drv = OSGeo.OGR.Ogr.GetDriverByName("MVT");
                OSGeo.OGR.DataSource ds = OSGeo.OGR.Ogr.Open("MVT:" + mvtTile, 0);
                string[] mvtOptions = new[] { "CLIP", "NO" };
                //OSGeo.OGR.DataSource ds = drv.Open(mvtTile, 0);

                if (ds == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                    return;
                }

                ///Morph raw mapbox tile points to geolocated tile
                Vector3d moveDir = tileExtents[t].ElementAt(0) - new Point3d(0, 0, 0);
                Transform move = Transform.Translation(moveDir);
                Transform scale = Transform.Scale(Plane.WorldXY, tileWidth[t] / 4096, tileHeight[t] / 4096, 1);
                Transform scaleMove = Transform.Multiply(move, scale);

                for (int iLayer = 0; iLayer < ds.GetLayerCount(); iLayer++)
                {
                    OSGeo.OGR.Layer layer = ds.GetLayerByIndex(iLayer);

                    long count = layer.GetFeatureCount(1);
                    int featureCount = System.Convert.ToInt32(count);
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Layer #" + iLayer + " " + layer.GetName() + " has " + featureCount + " features");

                    if (!layerNameList.Any(s => layer.GetName().Contains(s)))
                    {
                        layerNameList.Add(layer.GetName());
                    }

                    int layerIndex = layerNameList.IndexOf(layer.GetName());

                    ///Filter by layer name from menu
                    if (layerFilterName == layer.GetName() || layerFilterName == "No Filter")
                    {

                        OSGeo.OGR.FeatureDefn def = layer.GetLayerDefn();

                        ///Get the field names
                        List<string> fieldnames = new List<string>();
                        if (fnames.get_Branch(new GH_Path(layerIndex)) == null)
                        {
                            for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
                            {
                                OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iAttr);
                                fnames.Append(new GH_String(fdef.GetNameRef()), new GH_Path(layerIndex, t));
                            }
                        }

                        ///Loop through geometry
                        OSGeo.OGR.Feature feat;

                        bool hasValues = false;
                        int m = 0;
                        ///error "Self-intersection at or near point..." when zoom gets below 12 for water
                        ///this is an issue with the way mvt simplifies geometries at lower zoom levels and is a known problem
                        ///TODO: look into how to fix invalid geom and return to the typical while loop iterating method
                        //while ((feat = layer.GetNextFeature()) != null)

                        while (true)
                        {
                            try
                            {
                                feat = layer.GetNextFeature();
                            }
                            catch
                            {

                                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Some features had invalid geometry and were skipped.");
                                continue;
                            }

                            if (feat == null)
                            {
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Some features in Layer #{iLayer} {layer.GetName()} had no or invalid geometry and were skipped.");
                                break;
                            }


                            OSGeo.OGR.Geometry geom = feat.GetGeometryRef();

                            ///reproject geometry to WGS84 and userSRS
                            ///TODO: look into using the SetCRS global variable here

                            gtype.Append(new GH_String(geom.GetGeometryName()), new GH_Path(layerIndex, t, m));
                            Transform tr = scaleMove; // new Transform(1);

                            if (feat.GetGeometryRef() != null)
                            {

                                ///Convert GDAL geometries to IGH_GeometricGoo
                                foreach (IGH_GeometricGoo gMorphed in Heron.Convert.OgrGeomToGHGoo(geom, tr))
                                {
                                    //gMorphed.Morph(morph);
                                    gGoo.Append(gMorphed, new GH_Path(layerIndex, t, m));

                                }

                                if (layer.GetName() == "building")
                                {
                                    if (feat.GetFieldAsString(def.GetFieldIndex("extrude")) == "true")
                                    {
                                        double unitsConversion = Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);
                                        double height = System.Convert.ToDouble(feat.GetFieldAsString(def.GetFieldIndex("height"))) / unitsConversion;
                                        double min_height = System.Convert.ToDouble(feat.GetFieldAsString(def.GetFieldIndex("min_height"))) / unitsConversion;
                                        bool underground = System.Convert.ToBoolean(feat.GetFieldAsString(def.GetFieldIndex("underground")));

                                        if (geom.GetGeometryType() == wkbGeometryType.wkbPolygon)
                                        {
                                            Extrusion bldg = Heron.Convert.OgrPolygonToExtrusion(geom, tr, height, min_height, underground);
                                            IGH_GeometricGoo bldgGoo = GH_Convert.ToGeometricGoo(bldg);
                                            gGooBuildings.Append(bldgGoo, new GH_Path(layerIndex, t, m));
                                        }

                                        if (geom.GetGeometryType() == wkbGeometryType.wkbMultiPolygon)
                                        {
                                            List<Extrusion> bldgs = Heron.Convert.OgrMultiPolyToExtrusions(geom, tr, height, min_height, underground);
                                            foreach (Extrusion bldg in bldgs)
                                            {
                                                IGH_GeometricGoo bldgGoo = GH_Convert.ToGeometricGoo(bldg);
                                                gGooBuildings.Append(bldgGoo, new GH_Path(layerIndex, t, m));
                                            }
                                        }
                                    }
                                }

                                /// Get Feature Values
                                if (fvalues.PathExists(new GH_Path(layerIndex, t, m)))
                                {
                                    //fvalues.get_Branch(new GH_Path(iLayer, t, m)).Clear();
                                }

                                for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                {
                                    OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                    if (feat.IsFieldSet(iField))
                                    {
                                        fvalues.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(layerIndex, t, m));
                                    }
                                    else
                                    {
                                        fvalues.Append(new GH_String("null"), new GH_Path(layerIndex, t, m));
                                    }

                                }

                                ///If there are value for the layer's fields then we can return true
                                hasValues = true;

                            }
                            m++;
                            geom.Dispose();
                            feat.Dispose();

                        }///end while loop through features

                        ///Some Mapbox tiles seem to have features with fields but no values or geometry, so we need to discard these layers to avoid a data tree mismatch
                        if (!hasValues)
                        {
                            fnames.RemovePath(new GH_Path(layerIndex, t));
                        }

                    }///end layer by name

                    layer.Dispose();

                }///end loop through layers

                ds.Dispose();
                t++;

            }///end loop through mvt tiles

            //write out new tile range metadata for serialization
            TileCacheMeta = tileRangeString;

            List<string> mbAtts = new List<string> { "© Mapbox, © OpenStreetMap", "https://www.mapbox.com/about/maps/", "http://www.openstreetmap.org/copyright" };

            DA.SetData(0, imgFrame);
            DA.SetDataTree(1, fnames);
            DA.SetDataTree(2, fvalues);
            DA.SetDataTree(3, gGoo);
            DA.SetDataList(4, mbAtts);
            DA.SetDataTree(5, gtype);
            DA.SetDataTree(6, gGooBuildings);
            DA.SetDataList(7, tileExtents);

        }


        ///Menu items
        ///https://www.grasshopper3d.com/forum/topics/closing-component-popup-side-bars-when-clicking-outside-the-form
        ///

        private bool IsServiceSelected(string serviceString)
        {
            return serviceString.Equals(mbSource);
        }
        private bool IsLayerSelected(string layerString)
        {
            return layerString.Equals(layerFilterName);
        }
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {

            MapboxVector.Menu_AppendSeparator(menu);

            if (mbSourceList == "")
            {
                mbSourceList = Convert.GetEnpoints();
            }

            JObject mbJson = JObject.Parse(mbSourceList);

            ToolStripMenuItem root = new ToolStripMenuItem("Pick Mapbox Vector Service...");

            foreach (var service in mbJson["Mapbox Vector"])
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

            ///Add layer types to menu
            List<string> layerNameList = new List<string> { "No Filter", "admin", "aeroway","building","landuse_overlay","landuse","motorway_junction",
                "road","structure","water","waterway","airport_label","housenum_label","natural_label","place_label","poi_label","transit_stop_label" };

            ToolStripMenuItem layerRoot = new ToolStripMenuItem("Filter By Layer...");

            foreach (var lName in layerNameList)
            {
                ToolStripMenuItem layer = new ToolStripMenuItem(lName);
                layer.Tag = lName;
                layer.Checked = IsLayerSelected(lName);
                layer.Click += LayerItemOnClick;
                layerRoot.DropDownItems.Add(layer);
            }

            menu.Items.Add(layerRoot);

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
            Message = mbSource + " | " + layerFilterName;

            ExpireSolution(true);
        }

        private void LayerItemOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string code = (string)item.Tag;
            if (IsLayerSelected(code))
                return;

            RecordUndoEvent("LayerFilterName");

            layerFilterName = code;
            Message = mbSource + " | " + layerFilterName;

            ExpireSolution(true);
        }

        ////////////////////////////////
        ///Sticky parameters
        ///https://developer.rhino3d.com/api/grasshopper/html/5f6a9f31-8838-40e6-ad37-a407be8f2c15.htm
        ///

        private string tCacheMeta = string.Empty;
        private string mbSourceList = Convert.GetEnpoints();
        private string mbSource = JObject.Parse(Convert.GetEnpoints())["Mapbox Vector"][0]["service"].ToString();
        private string mbURL = JObject.Parse(Convert.GetEnpoints())["Mapbox Vector"][0]["url"].ToString();
        private string layerFilterName = "No Filter";

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
        public string LayerFilterName
        {
            get { return layerFilterName; }
            set
            {
                layerFilterName = value;
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("TileCacheMeta", TileCacheMeta);
            writer.SetString("MapboxSourceList", MapboxSourceList);
            writer.SetString("MapboxSource", MapboxSource);
            writer.SetString("MapboxURL", MapboxURL);
            writer.SetString("LayerFilterName", LayerFilterName);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            TileCacheMeta = reader.GetString("TileCacheMeta");
            MapboxSourceList = reader.GetString("MapboxSourceList");
            MapboxSource = reader.GetString("MapboxSource");
            MapboxURL = reader.GetString("MapboxURL");
            LayerFilterName = reader.GetString("LayerFilterName");
            return base.Read(reader);
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
                return Properties.Resources.shp;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("3c62dda0-3162-4041-88a3-074739c34711"); }
        }
    }
}