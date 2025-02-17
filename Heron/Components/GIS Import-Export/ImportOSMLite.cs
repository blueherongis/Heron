using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using System.Linq;
using System.Text.RegularExpressions;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using GH_IO;
using GH_IO.Serialization;
using Rhino;
using Rhino.Geometry;

using OsmSharp;
using OsmSharp.Streams;
using OsmSharp.Complete;
using OsmSharp.Tags;


namespace Heron
{
    public class ImportOSMLite : HeronComponent
    {
        public ImportOSMLite()
          : base("Import OSM Lite", "IOL",
              "A lite version of ImportOSM that can run in a headless environment. Nodes, Ways and Relations are organized onto their own branches in the output.  " +
                "OSM's WGS84 coordinates are translated to Web Mercator (EPSG:3857).",
              "GIS Import | Export")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve for vector data", GH_ParamAccess.item);
            pManager.AddTextParameter("OSM Data Location", "filePath", "File path for the OSM vector data input", GH_ParamAccess.item);
            pManager.AddTextParameter("Filter Fields", "filterFields", "List of filter terms for OSM fields such as highway, route, building, etc.", GH_ParamAccess.list);
            pManager.AddTextParameter("Filter Field,Value", "filterFieldValue", "List of filter terms for OSM fields and values. Format Field,Value like 'addr:street,Main.'", GH_ParamAccess.list);

            pManager[0].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Extents", "extents", "Bounding box generated from 'bounds' in OSM file if present.", GH_ParamAccess.tree);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the vector data features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry contained in the feature", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Buildings", "buildings", "Building geometry given ways or relations with a 'building' or 'building:part' tag.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///Gather GHA inputs
            Curve boundary = null;
            DA.GetData<Curve>(0, ref boundary);

            string osmFilePath = string.Empty;
            DA.GetData<string>(1, ref osmFilePath);

            List<string> filterWords = new List<string>();
            DA.GetDataList<string>(2, filterWords);

            List<string> filterKeyValue = new List<string>();
            DA.GetDataList<string>(3, filterKeyValue);


            ///GDAL setup
            Heron.GdalConfiguration.ConfigureOgr();
            Heron.GdalConfiguration.ConfigureGdal();


            ///Set transforms between OSM's WGS84 and Web Mercator to get x y units in meters
            OSGeo.OSR.SpatialReference osmSRS = new OSGeo.OSR.SpatialReference("");
            osmSRS.SetFromUserInput("WGS84");
            OSGeo.OSR.SpatialReference webMercator = new OSGeo.OSR.SpatialReference("");
            webMercator.SetFromUserInput("EPSG:3857");
            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(osmSRS, webMercator);
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(webMercator, osmSRS);


            ///Declare trees
            Rectangle3d recs = new Rectangle3d();
            GH_Structure<GH_String> fieldNames = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fieldValues = new GH_Structure<GH_String>();
            GH_Structure<IGH_GeometricGoo> geometryGoo = new GH_Structure<IGH_GeometricGoo>();
            GH_Structure<IGH_GeometricGoo> buildingGoo = new GH_Structure<IGH_GeometricGoo>();


            Point3d max = new Point3d();
            Point3d min = new Point3d();
            if (boundary != null)
            {
                Point3d maxM = boundary.GetBoundingBox(true).Corner(true, false, true);
                max = Heron.Convert.OSRTransformPoint3dToPoint3d(maxM, revTransform);

                Point3d minM = boundary.GetBoundingBox(true).Corner(false, true, true);
                min = Heron.Convert.OSRTransformPoint3dToPoint3d(minM, revTransform);
            }

            /// get extents (why is this not part of OsmSharp?)
            System.Xml.Linq.XDocument xdoc = System.Xml.Linq.XDocument.Load(osmFilePath);
            if (xdoc.Root.Element("bounds")!= null)
            {
                double minlat = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("minlat").Value);
                double minlon = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("minlon").Value);
                double maxlat = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("maxlat").Value);
                double maxlon = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("maxlon").Value);
                Point3d boundsMin = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d(minlon, minlat, 0), coordTransform);
                Point3d boundsMax = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d(maxlon, maxlat, 0), coordTransform);

                recs = new Rectangle3d(Plane.WorldXY, boundsMin, boundsMax);
            }
            else 
            { 
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Cannot determine the extents of the OSM file. A 'bounds' element may not be present in the file. ");
            }


            using (var fileStreamSource = File.OpenRead(osmFilePath))
            {
                /// create a source.
                OsmSharp.Streams.XmlOsmStreamSource source = new OsmSharp.Streams.XmlOsmStreamSource(fileStreamSource);

                /// filter by bounding box
                OsmSharp.Streams.OsmStreamSource sourceClipped = source;
                if (clipped) { sourceClipped = source.FilterBox((float)max.X, (float)max.Y, (float)min.X, (float)min.Y, true); }

                /// create a dictionary of elements
                OsmSharp.Db.Impl.MemorySnapshotDb sourceMem = new OsmSharp.Db.Impl.MemorySnapshotDb(sourceClipped);

                /// filter the source
                var filtered = from osmGeos in sourceClipped
                               where osmGeos.Tags != null
                               select osmGeos;

                if (filterWords.Any())
                {
                    filtered = from osmGeos in filtered
                               where osmGeos.Tags.ContainsAnyKey(filterWords)
                               select osmGeos;
                }

                if (filterKeyValue.Any())
                {
                    List<Tag> tags = new List<Tag>();
                    foreach (string term in filterKeyValue)
                    {
                        string[] kv = term.Split(',');
                        Tag tag = new Tag(kv[0], kv[1]);
                        tags.Add(tag);
                    }
                    filtered = from osmGeos in filtered
                               where osmGeos.Tags.Intersect(tags).Any()
                               select osmGeos;
                }

                source.Dispose();

                /// loop over all objects and count them.
                int nodes = 0, ways = 0, relations = 0;
                Dictionary<PolylineCurve, GH_Path> bldgOutlines = new Dictionary<PolylineCurve, GH_Path>();
                List<ImportOSM.BuildingPart> buildingParts = new List<ImportOSM.BuildingPart>();


                foreach (OsmSharp.OsmGeo osmGeo in filtered)
                {

                    //NODES
                    if (osmGeo.Type == OsmGeoType.Node)
                    {
                        OsmSharp.Node n = (OsmSharp.Node)osmGeo;
                        GH_Path nodesPath = new GH_Path(0, nodes);

                        //populate Fields and Values for each node
                        fieldNames.AppendRange(ImportOSM.GetKeys(osmGeo), nodesPath);
                        fieldValues.AppendRange(GetValues(osmGeo), nodesPath);

                        //get geometry for node
                        Point3d nPoint = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)n.Longitude, (double)n.Latitude, 0), coordTransform);
                        geometryGoo.Append(new GH_Point(nPoint), nodesPath);

                        //increment nodes
                        nodes++;
                    }

                    ////////////////////////////////////////////////////////////
                    //WAYS
                    if (osmGeo.Type == OsmGeoType.Way)
                    {
                        OsmSharp.Way w = (OsmSharp.Way)osmGeo;
                        GH_Path waysPath = new GH_Path(1, ways);

                        //populate Fields and Values for each way
                        fieldNames.AppendRange(ImportOSM.GetKeys(osmGeo), waysPath);
                        fieldValues.AppendRange(GetValues(osmGeo), waysPath);

                        //get polyline geometry for way
                        List<Point3d> wayNodes = new List<Point3d>();
                        foreach (long j in w.Nodes)
                        {
                            OsmSharp.Node n = (OsmSharp.Node)sourceMem.Get(OsmGeoType.Node, j);
                            Point3d nPt = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)n.Longitude, (double)n.Latitude, 0), coordTransform);
                            wayNodes.Add(nPt);
                        }

                        PolylineCurve pL = new PolylineCurve(wayNodes);
                        if (pL.IsClosed)
                        {
                            //create base surface
                            Brep[] breps = Brep.CreatePlanarBreps(pL, HeadlessDocumentTolerance);
                            geometryGoo.Append(new GH_Brep(breps[0]), waysPath);
                        }
                        else { geometryGoo.Append(new GH_Curve(pL), waysPath); }

                        //building massing
                        if ((w.Tags.ContainsKey("building") || w.Tags.ContainsKey("building:part")))// && !w.Tags.ContainsKey("construction"))
                        {
                            if (pL.IsClosed)
                            {
                                ///Populate dictionary for sorting building parts later
                                if(w.Tags.ContainsKey("building")) { bldgOutlines.Add(pL, waysPath); }

                                CurveOrientation orient = pL.ClosedCurveOrientation(Plane.WorldXY);
                                if (orient != CurveOrientation.CounterClockwise) pL.Reverse();

                                ///Move polylines to min height
                                double minHeightWay = GetMinBldgHeight(osmGeo);
                                Vector3d minVec = new Vector3d(0, 0, minHeightWay);
                                if (minHeightWay > 0.0)
                                {
                                    var minHeightTranslate = Transform.Translation(minVec);
                                    pL.Transform(minHeightTranslate);
                                }

                                Vector3d hVec = new Vector3d(0, 0, GetBldgHeight(osmGeo) - minHeightWay);
                                
                                Extrusion ex = Extrusion.Create(pL, hVec.Z, true);
                                ///Catch rare issue where counterclockwise pL and positive Z still extrude negative.  Seems like a Rhino bug.
                                ///https://discourse.mcneel.com/t/heron-plugin-building-height-problem/164757/10
                                if (ex != null && ex.PathTangent.Z < 0) ex = Extrusion.Create(pL, -hVec.Z, true);
                                IGH_GeometricGoo bldgGoo = GH_Convert.ToGeometricGoo(ex);

                                ///Save building parts for sorting later and remove part from geometry goo tree
                                if (w.Tags.ContainsKey("building:part"))
                                {
                                    ImportOSM.BuildingPart bldgPart = new ImportOSM.BuildingPart(pL, bldgGoo, fieldNames[waysPath], fieldValues[waysPath], osmGeo);
                                    buildingParts.Add(bldgPart);
                                    fieldNames.RemovePath(waysPath);
                                    fieldValues.RemovePath(waysPath);
                                    geometryGoo.RemovePath(waysPath);
                                    ways = ways - 1;
                                }
                                else { buildingGoo.Append(bldgGoo, waysPath); }
                            }
                        }

                        //increment ways
                        ways++;
                    }
                    ///////////////////////////////////////////////////////////

                    //RELATIONS
                    if (osmGeo.Type == OsmGeoType.Relation)
                    {
                        OsmSharp.Relation r = (OsmSharp.Relation)osmGeo;
                        GH_Path relationPath = new GH_Path(2, relations);

                        //populate Fields and Values for each relation
                        fieldNames.AppendRange(ImportOSM.GetKeys(osmGeo), relationPath);
                        fieldValues.AppendRange(GetValues(osmGeo), relationPath);

                        List<Curve> pLines = new List<Curve>();

                        // start members loop
                        for (int mem = 0; mem < r.Members.Length; mem++)
                        {
                            GH_Path memberPath = new GH_Path(2, relations, mem);

                            OsmSharp.RelationMember rMem = r.Members[mem];
                            OsmSharp.OsmGeo rMemGeo = sourceMem.Get(rMem.Type, rMem.Id);

                            if (rMemGeo != null)
                            {
                                //get geometry for node
                                if (rMemGeo.Type == OsmGeoType.Node)
                                {
                                    long memNodeId = rMem.Id;
                                    OsmSharp.Node memN = (OsmSharp.Node)sourceMem.Get(rMem.Type, rMem.Id);
                                    Point3d memPoint = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)memN.Longitude, (double)memN.Latitude, 0), coordTransform);
                                    geometryGoo.Append(new GH_Point(memPoint), memberPath);
                                }

                                //get geometry for way
                                if (rMem.Type == OsmGeoType.Way)
                                {
                                    long memWayId = rMem.Id;

                                    OsmSharp.Way memWay = (OsmSharp.Way)rMemGeo;

                                    //get polyline geometry for way
                                    List<Point3d> memNodes = new List<Point3d>();
                                    foreach (long memNodeId in memWay.Nodes)
                                    {
                                        OsmSharp.Node memNode = (OsmSharp.Node)sourceMem.Get(OsmGeoType.Node, memNodeId);
                                        Point3d memPt = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)memNode.Longitude, (double)memNode.Latitude, 0), coordTransform);
                                        memNodes.Add(memPt);
                                    }

                                    PolylineCurve memPolyline = new PolylineCurve(memNodes);

                                    geometryGoo.Append(new GH_Curve(memPolyline.ToNurbsCurve()), memberPath);

                                    CurveOrientation orient = memPolyline.ClosedCurveOrientation(Plane.WorldXY);
                                    if (orient != CurveOrientation.CounterClockwise) memPolyline.Reverse();

                                    pLines.Add(memPolyline.ToNurbsCurve());
                                }

                                //get nested relations
                                if (rMem.Type == OsmGeoType.Relation)
                                {
                                    ///not sure if this is needed
                                }
                            }
                        }
                        //end members loop

                        bool allClosed = true;
                        var pLinesJoined = Curve.JoinCurves(pLines); ///try to join ways that may not already be closed ie SF City Hall
                        pLines = pLinesJoined.ToList();
                        foreach (Curve pc in pLines)
                        {
                            if (!pc.IsClosed)
                            {
                                allClosed = false;
                            }
                        }

                        if (pLines.Count > 0 && allClosed)
                        {
                            ///Move polylines to min height
                            double minHeight = GetMinBldgHeight(osmGeo);
                            if (minHeight > 0.0)
                            {
                                Vector3d minVec = new Vector3d(0, 0, minHeight);
                                var minHeightTranslate = Transform.Translation(minVec);
                                for (int i = 0; i < pLines.Count; i++)
                                {
                                    pLines[i].Transform(minHeightTranslate);
                                }
                            }
                            ///Create base surface
                            Brep[] breps = Brep.CreatePlanarBreps(pLines, HeadlessDocumentTolerance);
                            if (geometryGoo.PathExists(relationPath)) geometryGoo.RemovePath(relationPath);

                            if (breps != null)
                            {
                                foreach (Brep b in breps)
                                {
                                    geometryGoo.Append(new GH_Brep(b), relationPath);

                                    ///Building massing
                                    if (r.Tags.ContainsKey("building") || r.Tags.ContainsKey("building:part"))
                                    {
                                        Vector3d hVec = new Vector3d(0, 0, GetBldgHeight(osmGeo) - minHeight);

                                        ///Create extrusion from base surface
                                        buildingGoo.Append(new GH_Brep(Brep.CreateFromOffsetFace(b.Faces[0], hVec.Z, HeadlessDocumentTolerance, false, true)), relationPath);
                                    }
                                }
                            }
                        }

                        ///Increment relations
                        relations++;

                    } ///End relation loop
                } ///End filtered loop

                ///Add building parts to sub-branches under main building
                for (int partIndex = 0; partIndex < buildingParts.Count; partIndex++)
                {
                    ImportOSM.BuildingPart bldgPart = buildingParts[partIndex];
                    Point3d partPoint = bldgPart.PartFootprint.PointAtStart;
                    partPoint.Z = 0;
                    bool replaceBuidingMass = false;
                    GH_Path mainBuildingMassPath = new GH_Path();
                    PolylineCurve massOutline = new PolylineCurve();

                    bool isRoof = bldgPart.PartOsmGeo.Tags.TryGetValue("roof:shape", out string isRoofString);
                    if (isRoof)
                    {
                        bldgPart.PartGoo = ImportOSM.BldgPartToRoof(bldgPart);
                    }

                    foreach (KeyValuePair<PolylineCurve, GH_Path> pair in bldgOutlines)
                    {
                        PointContainment pc = pair.Key.Contains(partPoint, Plane.WorldXY, HeadlessDocumentTolerance);
                        if (pc != PointContainment.Outside)
                        {
                            ///Create new sub-branch
                            int numSubBranches = 0;
                            GH_Path partPath = pair.Value.AppendElement(numSubBranches);
                            while (buildingGoo.PathExists(partPath))
                            {
                                numSubBranches++;
                                partPath = pair.Value.AppendElement(numSubBranches);
                            }

                            ///Add data to sub-branch
                            fieldNames.AppendRange(bldgPart.PartFieldNames, partPath);
                            fieldValues.AppendRange(bldgPart.PartFieldValues, partPath);
                            buildingGoo.Append(bldgPart.PartGoo, partPath);

                            ///Remove the main building mass 
                            replaceBuidingMass = true;
                            mainBuildingMassPath = pair.Value;
                            massOutline = pair.Key;
                        }
                    }
                    ///Remove the main building mass
                    if (replaceBuidingMass)
                    {
                        buildingGoo.RemovePath(mainBuildingMassPath);
                        buildingGoo.Append(new GH_Curve (massOutline), mainBuildingMassPath);
                    }
                    else
                    {
                        GH_Path extrasPath = new GH_Path(3, partIndex);
                        buildingGoo.Append(bldgPart.PartGoo, extrasPath);
                        fieldNames.AppendRange(bldgPart.PartFieldNames, extrasPath);
                        fieldValues.AppendRange(bldgPart.PartFieldValues, extrasPath);
                    }
                }
            } ///end osm source loop

            if (recs.IsValid) { DA.SetData(0, recs); }
            DA.SetDataTree(1, fieldNames);
            DA.SetDataTree(2, fieldValues);
            DA.SetDataTree(3, geometryGoo);
            DA.SetDataTree(4, buildingGoo);

        } ///end SolveInstance



        public static double HeadlessDocumentTolerance = 0.001;
        public static double HeadlessDocumentAngleTolerance = 1.0;

        private static List<GH_String> GetValues(OsmGeo osmGeo)
        {
            List<GH_String> values = new List<GH_String>();

            values.Add(new GH_String(osmGeo.Id.ToString()));

            if (osmGeo.Tags != null)
            {
                foreach (var t in osmGeo.Tags)
                {
                    values.Add(new GH_String(t.Value));
                }
            }
            else
            {
                values.Add(null);
            }
            return values;
        }


        private static double GetHeightDimensioned (string heightText)
        {
            double keyHeight = 0.0;
            if (!string.IsNullOrEmpty(heightText))
            {
                heightText = heightText.Split(' ')[0]; //clear trailing m

                if (heightText.Contains("'")) //check if in feet
                {
                    keyHeight = System.Convert.ToDouble(heightText.Split('\'')[0]) / 3.28084; //convert feet to meters
                }
                //if not feet assume meters
                else if (heightText.Contains("m") || heightText.Contains("M")) //clear trailing m with no space
                {
                    string[] heightWithM = Regex.Split(heightText, @"[^\d]");
                    keyHeight = System.Convert.ToDouble(heightWithM[0]);
                }
                else
                {
                    keyHeight = System.Convert.ToDouble(heightText);
                }
            }
            return keyHeight;
        }

        private static double GetHeightLevels (string levelsText)
        {
            double keyHeight = 0.0;
            if (levelsText != null)
            {
                double levelsDouble = 0;
                Double.TryParse(levelsText, out levelsDouble);
                if (levelsDouble > 0)
                {
                    keyHeight = Math.Max(keyHeight, System.Convert.ToDouble(levelsText) * 3); //3 meters per floor
                }
            }
            return keyHeight;
        }

        private static double GetBldgHeight(OsmSharp.OsmGeo osmGeo)
        {
            ///Height determination
            ///https://wiki.openstreetmap.org/wiki/Simple_3D_Buildings
            
            double defaultHeight = 2.0 * 3; //default number of floors (2) at 3 meters per floor
            double keyHeightDimensioned = 0.0;
            double keyHeightLevels = 0.0;

            //height from height key
            if (osmGeo.Tags.ContainsKey("height"))
            {
                string heightText = osmGeo.Tags.GetValue("height");
                keyHeightDimensioned = GetHeightDimensioned(heightText);
            }

            //height from building:levels key
            if (osmGeo.Tags.ContainsKey("building:levels"))
            {
                string levelsText = osmGeo.Tags.GetValue("building:levels");
                keyHeightLevels = GetHeightLevels(levelsText);
            }
            double keyHeight = Math.Max(keyHeightDimensioned, keyHeightLevels);
            double height = Math.Max(defaultHeight, keyHeight);
            return height;
        }

        private static double GetMinBldgHeight(OsmSharp.OsmGeo osmGeo)
        {
            //height determination
            double keyHeight = 0.0;

            //height from height key
            if (osmGeo.Tags.ContainsKey("min_height"))
            {
                string heightText = osmGeo.Tags.GetValue("min_height");
                keyHeight = GetHeightDimensioned(heightText);
            }

            //height from building:levels key
            if (osmGeo.Tags.ContainsKey("building:min_level"))
            {
                string levelsText = osmGeo.Tags.GetValue("building:min_level");
                keyHeight = GetHeightLevels(levelsText);
            }

            return keyHeight;
        }

        /// <summary>
        /// Menu Items
        /// </summary>

        private bool clipped = true;
        public bool Clipped
        {
            get { return clipped; }
            set
            {
                clipped = value;
                if ((clipped))
                {
                    Message = "Clipped";
                }
                else
                {
                    Message = "Not Clipped";
                }
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean("Clipped", Clipped);
            return base.Write(writer);
        }
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            Clipped = reader.GetBoolean("Clipped");
            return base.Read(reader);
        }

        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            // Append the item to the menu, making sure it's always enabled and checked if Absolute is True.
            ToolStripMenuItem item = Menu_AppendItem(menu, "Clipped", Menu_ClippedClicked, true, Clipped);
            // Specifically assign a tooltip text to the menu item.
            item.ToolTipText = "When checked, the OSM data is clipped to the boundary input.";
        }
        private void Menu_ClippedClicked(object sender, EventArgs e)
        {
            RecordUndoEvent("Absolute");
            Clipped = !Clipped;
            ExpireSolution(true);
        }


        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.shp;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("A5C486F1-D614-4357-8CB3-A7DDCD637411"); }
        }
    }
}