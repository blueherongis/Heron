using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using System.Linq;

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
          : base("ImportOSM", "ImportOSM",
              "Import vector OpenStreetMap data clipped to a boundary. Nodes, Ways and Relations are organized onto their own branches in the output.",
              "GIS Tools")
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

            Transform xformToMetric = new Transform(Rhino.RhinoMath.UnitScale(RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters));
            Transform xformFromMetric = new Transform(Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Meters, RhinoDoc.ActiveDoc.ModelUnitSystem));

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
                max = Heron.Convert.XYZToWGS(maxM);

                Point3d minM = boundary.GetBoundingBox(true).Corner(false, true, true);
                min = Heron.Convert.XYZToWGS(minM);
            }

            /// get extents (why is this not part of OsmSharp?)
            System.Xml.Linq.XDocument xdoc = System.Xml.Linq.XDocument.Load(osmFilePath);
            if (xdoc.Root.Element("bounds")!= null)
            {
                double minlat = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("minlat").Value);
                double minlon = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("minlon").Value);
                double maxlat = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("maxlat").Value);
                double maxlon = System.Convert.ToDouble(xdoc.Root.Element("bounds").Attribute("maxlon").Value);
                Point3d boundsMin = Heron.Convert.WGSToXYZ(new Point3d(minlon, minlat, 0));
                Point3d boundsMax = Heron.Convert.WGSToXYZ(new Point3d(maxlon, maxlat, 0));

                recs = new Rectangle3d(Plane.WorldXY, boundsMin, boundsMax);
            }
            else { AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Cannot determine the extents of the OSM file. A 'bounds' element may not be present in the file."); }


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
                        Point3d nPoint = Heron.Convert.WGSToXYZ(new Point3d((double)n.Longitude, (double)n.Latitude, 0));
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
                            wayNodes.Add(Heron.Convert.WGSToXYZ(new Point3d((double)n.Longitude, (double)n.Latitude, 0)));
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
                        if (w.Tags.ContainsKey("building") || w.Tags.ContainsKey("building:part"))
                        {
                            if (pL.IsClosed)
                            {
                                CurveOrientation orient = pL.ClosedCurveOrientation(Plane.WorldXY);
                                if (orient != CurveOrientation.CounterClockwise) pL.Reverse();

                                Vector3d hVec = new Vector3d(0, 0, GetBldgHeight(osmGeo));
                                hVec.Transform(xformFromMetric);

                                Extrusion ex = Extrusion.Create(pL, hVec.Z, true);
                                IGH_GeometricGoo bldgGoo = GH_Convert.ToGeometricGoo(ex);
                                buildingGoo.Append(bldgGoo, waysPath);
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
                                    Point3d memPoint = Heron.Convert.WGSToXYZ(new Point3d((double)memN.Longitude, (double)memN.Latitude, 0));
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
                                        memNodes.Add(Heron.Convert.WGSToXYZ(new Point3d((double)memNode.Longitude, (double)memNode.Latitude, 0)));
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
                        foreach (Curve pc in pLines)
                        {
                            if (!pc.IsClosed)
                            {
                                allClosed = false;
                            }
                        }

                        if (pLines.Count > 0 && allClosed)
                        {
                            //create base surface
                            Brep[] breps = Brep.CreatePlanarBreps(pLines, DocumentTolerance());
                            geometryGoo.RemovePath(relationPath);

                            foreach (Brep b in breps)
                            {
                                geometryGoo.Append(new GH_Brep(b), relationPath);

                                //building massing
                                if (r.Tags.ContainsKey("building") || r.Tags.ContainsKey("building:part"))
                                {
                                    Vector3d hVec = new Vector3d(0, 0, GetBldgHeight(osmGeo));
                                    hVec.Transform(xformFromMetric);

                                    //create extrusion from base surface
                                    buildingGoo.Append(new GH_Brep(Brep.CreateFromOffsetFace(b.Faces[0], hVec.Z, DocumentTolerance(), false, true)), relationPath);
                                }
                            }

                        }

                        //increment relations
                        relations++;

                    } ///end relation loop
                } ///end filtered loop
            } ///end osm source loop

            if (recs.IsValid) { DA.SetData(0, recs); }
            DA.SetDataTree(1, fieldNames);
            DA.SetDataTree(2, fieldValues);
            DA.SetDataTree(3, geometryGoo);
            DA.SetDataTree(4, buildingGoo);

        } ///end SolveInstance



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

        private static double GetBldgHeight(OsmSharp.OsmGeo osmGeo)
        {
            //height determination
            double defaultHeight = 2.0 * 3; //default number of floors (2) at 3 meters per floor
            double keyHeight = 0.0;

            //height from height key
            if (osmGeo.Tags.ContainsKey("height"))
            {
                string heightText = osmGeo.Tags.GetValue("height").Split(' ')[0]; //clear trailing m
                                                                                  //check if in feet
                if (heightText.Contains("'"))
                {
                    keyHeight = System.Convert.ToDouble(heightText.Split('\'')[0]) / 3.28084; //convert feet to meters
                }
                //if not feet assume meters
                else
                {
                    keyHeight = System.Convert.ToDouble(heightText);
                }

            }

            //height from building:levels key
            if (osmGeo.Tags.ContainsKey("building:levels"))
            {
                string levelsText = osmGeo.Tags.GetValue("building:levels");
                if (levelsText != null)
                {
                    double levelsDouble = 0;
                    Double.TryParse(levelsText, out levelsDouble);
                    if (levelsDouble > 0)
                    {
                        keyHeight = Math.Max(keyHeight, System.Convert.ToDouble(levelsText) * 3); //3 meters per floor
                    }
                }

            }

            double height = Math.Max(defaultHeight, keyHeight);
            return height;
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
            // First add our own field.
            writer.SetBoolean("Clipped", Clipped);
            // Then call the base class implementation.
            return base.Write(writer);
        }
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // First read our own field.
            Clipped = reader.GetBoolean("Clipped");
            // Then call the base class implementation.
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