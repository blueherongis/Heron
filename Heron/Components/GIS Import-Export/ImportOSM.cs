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
    public class ImportOSM : HeronComponent
    {
        public ImportOSM()
          : base("Import OSM", "ImportOSM",
              "Import vector OpenStreetMap data clipped to a boundary. Nodes, Ways and Relations are organized onto their own branches in the output.",
              "GIS Import | Export")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            //pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve for vector data", GH_ParamAccess.item);
            pManager.AddTextParameter("OSM Data Location", "filePath", "File path for the OSM vector data input", GH_ParamAccess.item);
            //pManager.AddTextParameter("User Spatial Reference System", "userSRS", "Custom SRS", GH_ParamAccess.item, "WGS84");
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
            DA.GetData<string>("OSM Data Location", ref osmFilePath);

            //string userSRStext = "WGS84";
            //DA.GetData<string>(2, ref userSRStext);

            List<string> filterWords = new List<string>();
            DA.GetDataList<string>(2, filterWords);

            List<string> filterKeyValue = new List<string>();
            DA.GetDataList<string>(3, filterKeyValue);

            Transform xformToMetric = new Transform(scaleToMetric);
            Transform xformFromMetric = new Transform(scaleFromMetric);

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();
            RESTful.GdalConfiguration.ConfigureGdal();

            ///Set transform from input spatial reference to Heron spatial reference
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            OSGeo.OSR.SpatialReference osmSRS = new OSGeo.OSR.SpatialReference("");
            osmSRS.SetFromUserInput("WGS84");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + heronSRSInt;

            ///Apply EAP to HeronSRS
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
            Transform heronToUserSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);

            ///Set transforms between source and HeronSRS
            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(osmSRS, heronSRS);
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(heronSRS, osmSRS);


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
                maxM.Transform(heronToUserSRSTransform);
                max = Heron.Convert.OSRTransformPoint3dToPoint3d(maxM, revTransform);

                Point3d minM = boundary.GetBoundingBox(true).Corner(false, true, true);
                minM.Transform(heronToUserSRSTransform);
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
                boundsMin.Transform(userSRSToModelTransform);
                Point3d boundsMax = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d(maxlon, maxlat, 0), coordTransform);
                boundsMax.Transform(userSRSToModelTransform);

                recs = new Rectangle3d(Plane.WorldXY, boundsMin, boundsMax);
            }
            else 
            { 
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Cannot determine the extents of the OSM file. A 'bounds' element may not be present in the file. " +
                    "Try turning off clipping in this component's menu.");
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
                List<BuildingPart> buildingParts = new List<BuildingPart>();


                foreach (OsmSharp.OsmGeo osmGeo in filtered)
                {

                    //NODES
                    if (osmGeo.Type == OsmGeoType.Node)
                    {
                        OsmSharp.Node n = (OsmSharp.Node)osmGeo;
                        GH_Path nodesPath = new GH_Path(0, nodes);

                        //populate Fields and Values for each node
                        fieldNames.AppendRange(GetKeys(osmGeo), nodesPath);
                        fieldValues.AppendRange(GetValues(osmGeo), nodesPath);

                        //get geometry for node
                        //Point3d nPoint = Heron.Convert.WGSToXYZ(new Point3d((double)n.Longitude, (double)n.Latitude, 0));
                        Point3d nPoint = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)n.Longitude, (double)n.Latitude, 0), coordTransform);
                        nPoint.Transform(userSRSToModelTransform);
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
                        fieldNames.AppendRange(GetKeys(osmGeo), waysPath);
                        fieldValues.AppendRange(GetValues(osmGeo), waysPath);

                        //get polyline geometry for way
                        List<Point3d> wayNodes = new List<Point3d>();
                        foreach (long j in w.Nodes)
                        {
                            OsmSharp.Node n = (OsmSharp.Node)sourceMem.Get(OsmGeoType.Node, j);
                            //wayNodes.Add(Heron.Convert.WGSToXYZ(new Point3d((double)n.Longitude, (double)n.Latitude, 0)));
                            Point3d nPt = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)n.Longitude, (double)n.Latitude, 0), coordTransform);
                            nPt.Transform(userSRSToModelTransform);
                            wayNodes.Add(nPt);

                        }

                        PolylineCurve pL = new PolylineCurve(wayNodes);
                        if (pL.IsClosed)
                        {
                            //create base surface
                            Brep[] breps = Brep.CreatePlanarBreps(pL, DocumentTolerance());
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
                                //minVec.Transform(xformFromMetric);
                                if (minHeightWay > 0.0)
                                {
                                    var minHeightTranslate = Transform.Translation(minVec);
                                    pL.Transform(minHeightTranslate);
                                }

                                Vector3d hVec = new Vector3d(0, 0, GetBldgHeight(osmGeo) - minHeightWay);
                                //hVec.Transform(xformFromMetric);

                                Extrusion ex = Extrusion.Create(pL, hVec.Z, true);
                                IGH_GeometricGoo bldgGoo = GH_Convert.ToGeometricGoo(ex);

                                ///Save building parts for sorting later and remove part from geometry goo tree
                                if (w.Tags.ContainsKey("building:part"))
                                {
                                    BuildingPart bldgPart = new BuildingPart(pL, bldgGoo, fieldNames[waysPath], fieldValues[waysPath], osmGeo);
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
                        fieldNames.AppendRange(GetKeys(osmGeo), relationPath);
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
                                    //Point3d memPoint = Heron.Convert.WGSToXYZ(new Point3d((double)memN.Longitude, (double)memN.Latitude, 0));
                                    Point3d memPoint = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)memN.Longitude, (double)memN.Latitude, 0), coordTransform);
                                    memPoint.Transform(userSRSToModelTransform);
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
                                        //memNodes.Add(Heron.Convert.WGSToXYZ(new Point3d((double)memNode.Longitude, (double)memNode.Latitude, 0)));
                                        Point3d memPt = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d((double)memNode.Longitude, (double)memNode.Latitude, 0), coordTransform);
                                        memPt.Transform(userSRSToModelTransform);
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
                                //minVec.Transform(xformFromMetric);
                                var minHeightTranslate = Transform.Translation(minVec);
                                for (int i = 0; i < pLines.Count; i++)
                                {
                                    pLines[i].Transform(minHeightTranslate);
                                }
                            }
                            ///Create base surface
                            Brep[] breps = Brep.CreatePlanarBreps(pLines, DocumentTolerance());
                            geometryGoo.RemovePath(relationPath);

                            foreach (Brep b in breps)
                            {
                                geometryGoo.Append(new GH_Brep(b), relationPath);

                                ///Building massing
                                if (r.Tags.ContainsKey("building") || r.Tags.ContainsKey("building:part"))
                                {
                                    Vector3d hVec = new Vector3d(0, 0, GetBldgHeight(osmGeo) - minHeight);
                                    //hVec.Transform(xformFromMetric);

                                    ///Create extrusion from base surface
                                    buildingGoo.Append(new GH_Brep(Brep.CreateFromOffsetFace(b.Faces[0], hVec.Z, DocumentTolerance(), false, true)), relationPath);
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
                    BuildingPart bldgPart = buildingParts[partIndex];
                    Point3d partPoint = bldgPart.PartFootprint.PointAtStart;
                    partPoint.Z = 0;
                    bool replaceBuidingMass = false;
                    GH_Path mainBuildingMassPath = new GH_Path();
                    PolylineCurve massOutline = new PolylineCurve();

                    bool isRoof = bldgPart.PartOsmGeo.Tags.TryGetValue("roof:shape", out string isRoofString);
                    if (isRoof)
                    {
                        bldgPart.PartGoo = BldgPartToRoof(bldgPart);
                    }

                    foreach (KeyValuePair<PolylineCurve, GH_Path> pair in bldgOutlines)
                    {
                        PointContainment pc = pair.Key.Contains(partPoint, Plane.WorldXY, DocumentTolerance());
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

        public class BuildingPart
        {
            public PolylineCurve PartFootprint { get; set; }
            public IGH_GeometricGoo PartGoo { get; set; }
            public List<GH_String> PartFieldNames { get; set; }
            public List<GH_String> PartFieldValues { get; set; }
            public OsmGeo PartOsmGeo { get; set; }

            public BuildingPart(PolylineCurve pL, IGH_GeometricGoo partGoo, List<GH_String> partFieldNames, List<GH_String> partFieldValues, OsmGeo osmGeo)
            {
                this.PartFootprint = pL;
                this.PartGoo = partGoo;
                this.PartFieldNames = partFieldNames;
                this.PartFieldValues = partFieldValues;
                this.PartOsmGeo = osmGeo;
            }
        }

        public static IGH_GeometricGoo BldgPartToRoof(BuildingPart bldgPart)
        {
            IGH_GeometricGoo roof = bldgPart.PartGoo;
            PolylineCurve pL = bldgPart.PartFootprint; ///Already at min height
            
            bldgPart.PartOsmGeo.Tags.TryGetValue("roof:shape", out string roofShape);
            
            bldgPart.PartOsmGeo.Tags.TryGetValue("height", out string heightString);
            double height = GetHeightDimensioned(heightString);

            bldgPart.PartOsmGeo.Tags.TryGetValue("min_height", out string minHeightString);
            double min_height = GetHeightDimensioned(minHeightString);
            
            bldgPart.PartOsmGeo.Tags.TryGetValue("roof:height", out string roofHeightString);
            double roofHeight = GetHeightDimensioned(roofHeightString);

            double facadeHeight = height - roofHeight;
            ///Make sure there's a minium facade height for SF Transamerica Pyramid case
            if (facadeHeight <= 0) { facadeHeight = 2 * DocumentTolerance(); }
            
            bldgPart.PartOsmGeo.Tags.TryGetValue("roof:orientation", out string roofOrientationString);

            bldgPart.PartOsmGeo.Tags.TryGetValue("roof:direction", out string roofDirectionString);
            double roofDirection = System.Convert.ToDouble(roofDirectionString);
            Vector3d roofDirectionVector = Plane.WorldXY.YAxis;
            roofDirectionVector.Rotate(RhinoMath.ToRadians(-roofDirection), Plane.WorldXY.ZAxis);

            Line[] edges = pL.ToPolyline().GetSegments();
            Point3d centroid = AreaMassProperties.Compute(pL).Centroid;

            switch (roofShape)
            {
                case "pyramidal":
                    centroid.Z = height;
                    pL.TryGetPolyline(out Polyline pLPolyline);
                    Line[] pLLines = pLPolyline.GetSegments();
                    List<Brep> pyramidBrepList = Brep.CreatePlanarBreps(pL, DocumentTolerance()).ToList();

                    if (!string.IsNullOrEmpty(roofHeightString))
                    {
                        Plane facadeHeightPlane = Plane.WorldXY;
                        facadeHeightPlane.Translate(new Vector3d(0, 0, facadeHeight));
                        pLPolyline.Transform(Transform.PlanarProjection(facadeHeightPlane));

                        ///Creating individual faces seems to work better/cleaner than lofting curves
                        for (int i = 0; i < pLLines.Count(); i++)
                        {
                            Line bottomEdge = pLLines[i];
                            Line topEdge = bottomEdge;
                            topEdge.Transform(Transform.PlanarProjection(facadeHeightPlane));
                            pyramidBrepList.Add(Brep.CreateFromCornerPoints(bottomEdge.PointAt(0), bottomEdge.PointAt(1), topEdge.PointAt(1), topEdge.PointAt(0), DocumentTolerance()));
                        }
                    }

                    foreach (Line edge in pLPolyline.GetSegments())
                    {
                        pyramidBrepList.Add(Brep.CreateFromCornerPoints(edge.PointAt(0), centroid, edge.PointAt(1), DocumentTolerance()));
                    }

                    Brep[] pyramidBrep = Brep.CreateSolid(pyramidBrepList, DocumentTolerance());
                    if (pyramidBrep[0].IsSolid) { roof = GH_Convert.ToGeometricGoo(pyramidBrep[0]); }
                    break;

                case "dome":
                    double domeHeight = centroid.DistanceTo(pL.PointAtStart);
                    double baseHeight = height - min_height - roofHeight + (roofHeight - domeHeight);
                    
                    var topArc = new Point3d (centroid.X,centroid.Y,height);
                    var bottomArc = new Point3d(pL.PointAtStart.X, pL.PointAtStart.Y, pL.PointAtStart.Z + baseHeight);

                    Arc arc = new Arc(bottomArc, Vector3d.ZAxis, topArc);          

                    if (baseHeight > 0) 
                    {
                        Line podiumLine = new Line(pL.PointAtStart, bottomArc);
                        Curve revCurve = Curve.JoinCurves(new List<Curve>() { podiumLine.ToNurbsCurve(), arc.ToNurbsCurve() })[0];
                        var sweep = RevSurface.Create(revCurve, new Line(centroid, topArc));
                        roof = GH_Convert.ToGeometricGoo(sweep.ToBrep().CapPlanarHoles(DocumentTolerance()));
                    }
                    else 
                    {
                        var sweep = RevSurface.Create(arc.ToNurbsCurve(), new Line(centroid, topArc));
                        roof = GH_Convert.ToGeometricGoo(sweep.ToBrep().CapPlanarHoles(DocumentTolerance()));
                    }

                    break;
                    
                case "skillion":
                    Line frontEdge = new Line();
                    Line backEdge = new Line();
                    double frontAngleMin = RhinoMath.ToRadians(90);
                    double backAngleMin = RhinoMath.ToRadians(90);

                    foreach (Line edge in edges)
                    {
                        Point3d closestPt = edge.ClosestPoint(centroid, true);
                        Vector3d perpVector = closestPt - centroid;
                        double angleDifference = Vector3d.VectorAngle(roofDirectionVector, perpVector);
                        if (angleDifference < frontAngleMin)
                        {
                            frontEdge = edge;
                            frontAngleMin = angleDifference;
                        }
                        if (angleDifference > backAngleMin)
                        {
                            backEdge = edge;
                            backAngleMin = angleDifference;
                        }
                    }

                    Point3d backEdgeFrom = backEdge.From;
                    backEdgeFrom.Z = height;
                    Point3d backEdgeTo = backEdge.To;
                    backEdgeTo.Z = height;
                    Point3d frontEdgeFrom = frontEdge.From;
                    frontEdgeFrom.Z = facadeHeight;
                    Point3d frontEdgeTo = frontEdge.To;
                    frontEdgeTo.Z = facadeHeight;

                    List<Point3d> basePtList = new List<Point3d> { backEdge.From, backEdge.To, frontEdge.From, frontEdge.To, backEdge.From };
                    Polyline basePolyline = new Polyline(basePtList);
                    List<Point3d> topPtList = new List<Point3d> { backEdgeFrom, backEdgeTo, frontEdgeFrom, frontEdgeTo, backEdgeFrom };
                    Polyline topPolyline = new Polyline(topPtList);

                    ///Creating individual faces seems to work better/cleaner than lofting curves
                    List<Brep> skillionBreps = new List<Brep>();
                    Line[] baseLines = basePolyline.GetSegments();
                    Line[] topLines = topPolyline.GetSegments();
                    for (int i = 0; i < baseLines.Count(); i++)
                    {
                        Line bottomEdge = baseLines[i];
                        Line topEdge = topLines[i];
                        skillionBreps.Add(Brep.CreateFromCornerPoints(bottomEdge.PointAt(0), bottomEdge.PointAt(1), topEdge.PointAt(1), topEdge.PointAt(0), DocumentTolerance()));
                    }
                    Brep baseSkillion = Brep.CreateFromCornerPoints(backEdge.From, backEdge.To, frontEdge.From, frontEdge.To, DocumentTolerance());
                    Brep topSkillion = Brep.CreateFromCornerPoints(backEdgeFrom, backEdgeTo, frontEdgeFrom, frontEdgeTo, DocumentTolerance());

                    skillionBreps.Add(baseSkillion);
                    skillionBreps.Add(topSkillion);
                    Brep[] skillion = Brep.CreateSolid(skillionBreps, DocumentTolerance());
                    if (skillion.Count() > 0) { roof = GH_Convert.ToGeometricGoo(skillion[0]); }
                    
                    break;

                case "gabled":
                    ///TODO: Look into getting oriented bbox using front edge as orientation plane,
                    ///extrude gable roof profile from face of bbox, trim roof geo from footprint,
                    ///loft between footprint and trimmed roof edges and join everything.
                    
                    ///Need to simply polylines with colinear segments. Angle tolerance based on Notre-Dame de Paris case
                    //pL.Simplify(CurveSimplifyOptions.All, 0, RhinoMath.ToRadians(2)).TryGetPolyline(out Polyline pLSimplified);
                    Polyline pLSimplified = pL.ToPolyline();
                    pLSimplified.MergeColinearSegments(DocumentAngleTolerance()*5, true);

                    Line[] edgesSimplified = pLSimplified.GetSegments();
                    if (edgesSimplified.Count() != 4) { break; }
                    Line ridge = new Line();
                    Line eaveOne = new Line();
                    Line eaveTwo = new Line();
                    Polyline topGablePolyline = new Polyline();
                    List<Brep> gableBreps = new List<Brep>();

                    if ((edgesSimplified[0].Length > edgesSimplified[1].Length && roofOrientationString != "across") || ((edgesSimplified[0].Length < edgesSimplified[1].Length && roofOrientationString == "across")))
                    {
                        ridge = new Line(edgesSimplified[3].PointAt(0.5), edgesSimplified[1].PointAt(0.5));
                        ridge.FromZ = height;
                        ridge.ToZ = height;
                        eaveOne = edgesSimplified[0];
                        eaveOne.FromZ = facadeHeight;
                        eaveOne.ToZ = facadeHeight;
                        eaveTwo = edgesSimplified[2];
                        eaveTwo.Flip();
                        eaveTwo.FromZ = facadeHeight;
                        eaveTwo.ToZ = facadeHeight;
                        topGablePolyline = new Polyline { eaveOne.From, eaveOne.To, ridge.To, eaveTwo.To, eaveTwo.From, ridge.From, eaveOne.From };

                        Brep[] gableRoof = Brep.CreateFromLoft(new List<Curve> { eaveOne.ToNurbsCurve(), ridge.ToNurbsCurve(), eaveTwo.ToNurbsCurve() }, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
                        gableRoof[0].Faces.SplitKinkyFaces();
                        gableBreps.Add(gableRoof[0]);
                    }

                    if ((edgesSimplified[0].Length > edgesSimplified[1].Length && roofOrientationString == "across") || (edgesSimplified[0].Length < edgesSimplified[1].Length && roofOrientationString != "across"))
                    {
                        ridge = new Line(edgesSimplified[0].PointAt(0.5), edgesSimplified[2].PointAt(0.5));
                        ridge.FromZ = height;
                        ridge.ToZ = height;
                        eaveOne = edgesSimplified[1]; 
                        eaveOne.FromZ = facadeHeight;
                        eaveOne.ToZ = facadeHeight;
                        eaveTwo = edgesSimplified[3];
                        eaveTwo.Flip();
                        eaveTwo.FromZ = facadeHeight;
                        eaveTwo.ToZ = facadeHeight;
                        topGablePolyline = new Polyline { eaveTwo.From, ridge.From, eaveOne.From, eaveOne.To, ridge.To, eaveTwo.To, eaveTwo.From };

                        Brep[] gableRoof = Brep.CreateFromLoft(new List<Curve> { eaveOne.ToNurbsCurve(), ridge.ToNurbsCurve(), eaveTwo.ToNurbsCurve() }, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
                        gableRoof[0].Faces.SplitKinkyFaces();
                        gableBreps.Add(gableRoof[0]);
                    }

                    Brep[] gablewalls = Brep.CreateFromLoft(new List<Curve> { pLSimplified.ToPolylineCurve(), topGablePolyline.ToPolylineCurve() }, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
                    gablewalls[0].Faces.SplitKinkyFaces();
                    gablewalls[0].MergeCoplanarFaces(DocumentTolerance());
                    gableBreps.Add(gablewalls[0]);

                    Brep baseGable = Brep.CreateFromCornerPoints(edgesSimplified[0].From, edgesSimplified[0].To, edgesSimplified[2].From, edgesSimplified[2].To, DocumentTolerance());
                    gableBreps.Add(baseGable);
                    Brep[] gable = Brep.JoinBreps(gableBreps, DocumentTolerance());

                    if (gable[0].IsValid) { roof = GH_Convert.ToGeometricGoo(gable[0]); }
                    break;
                
                default:
                    break;
            }

            return roof;
        }

        private static List<GH_String> GetKeys(OsmGeo osmGeo)
        {
            List<GH_String> keys = new List<GH_String>();
            keys.Add(new GH_String("osm id"));
            if (osmGeo.Tags != null)
            {
                foreach (var t in osmGeo.Tags)
                {
                    keys.Add(new GH_String(t.Key));
                }
            }
            else
            {
                keys.Add(null);
            }
            return keys;
        }

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

        private static double scaleToMetric = Rhino.RhinoMath.UnitScale(RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);
        private static double scaleFromMetric = Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Meters, RhinoDoc.ActiveDoc.ModelUnitSystem);

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
            double keyHeightScaled = keyHeight * scaleFromMetric;
            return keyHeightScaled;
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
            return keyHeight*scaleFromMetric;
        }

        private static double GetBldgHeight(OsmSharp.OsmGeo osmGeo)
        {
            ///Height determination
            ///https://wiki.openstreetmap.org/wiki/Simple_3D_Buildings
            
            double defaultHeight = 2.0 * 3 * scaleFromMetric; //default number of floors (2) at 3 meters per floor
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
            get { return new Guid("437b4c39-08ef-459b-a863-3c2e8dc1ce17"); }
        }
    }
}