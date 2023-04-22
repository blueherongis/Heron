using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using OSGeo.OSR;
using OSGeo.OGR;
using Rhino.Geometry;
using Rhino.DocObjects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Heron
{
    public class ImportVectorSRS : HeronComponent
    {
        //Class Constructor
        public ImportVectorSRS() : base("Import Vector", "ImportVector", "Import vector GIS data clipped to a boundary, including SHP, GeoJSON, OSM, KML, MVT and GDB folders.  " +
            "Use the SetSRS component to set the spatial reference system used by this component.", "GIS Import | Export")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("Vector Data Location", "filePath", "File path for the vector data input", GH_ParamAccess.item);
            pManager[0].Optional = true;
            Message = HeronSRS.Instance.SRS;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Data Extents", "extents", "Bounding box of all Shapefile features given the userSRS", GH_ParamAccess.tree);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the vector data features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry contained in the feature", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {

            ///Gather GHA inputs
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            ///Create polygon for clipping geometry
            Mesh boundaryMesh = new Mesh();
            bool boundaryNotNull = true, boundaryValid = true, boundaryClosed = true;
            if (boundary.Count > 0 && clipIt)
            {
                ///Check if boundaries are closed and valid 
                foreach (var b in boundary)
                {
                    if (b == null)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more clipping boundaries is null. Make sure all boundaries are closed and valid.");
                        boundaryNotNull = false;
                    }
                    else if (!b.IsValid)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping boundary is not valid.");
                        boundaryValid = false;
                    }
                    else if (!b.IsClosed)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping boundaries must be closed curves.");
                        boundaryClosed = false;
                    }
                }
                if (boundaryNotNull && boundaryValid && boundaryClosed)
                {
                    Brep[] boundarySurface = Brep.CreatePlanarBreps(boundary, DocumentTolerance());
                    foreach (var srf in boundarySurface)
                    {
                        boundaryMesh.Append(Mesh.CreateFromBrep(srf, MeshingParameters.Default));
                    }
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Unable to create clipping boundary from input curves.");
                }
            }

            string shpFilePath = string.Empty;
            DA.GetData<string>("Vector Data Location", ref shpFilePath);

            ///GDAL setup
            ///Some preliminary testing has been done to read SHP, GeoJSON, OSM, KML, MVT, GML and GDB
            ///It can be spotty with KML, MVT and GML and doesn't throw informative errors.  Likely has to do with getting a valid CRS and 
            ///TODO: resolve errors with reading KML, MVT, GML.
            RESTful.GdalConfiguration.ConfigureOgr();
            string userSRStext = HeronSRS.Instance.SRS;


            ///Set transform from input spatial reference to Heron spatial reference
            ///TODO: verify the userSRS is valid
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int sourceSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + sourceSRSInt;

            ///Apply EAP to HeronSRS
            Transform userSRSToHeronSRSTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
            Transform heronSRSToUserSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);



            DataSource dataSource = CreateDataSourceSRS(shpFilePath);
            List<OSGeo.OGR.Layer> layerSet = GetLayersSRS(dataSource);

            ///TODO: Look into translating data to HeronSRS first with Ogr2Ogr
            ///Create virtual datasource to be converted later
            ///Using geojson as a flexible base file type which can be converted later with ogr2ogr
            //OSGeo.OGR.Driver memDriver = Ogr.GetDriverByName("GeoJSON");
            //DataSource ds = memDriver.CreateDataSource("/vsimem/out.geojson", null);
            //string options = "-overwrite -t_srs " + HeronSRS.Instance.SRS;
            //var re = new System.Text.RegularExpressions.Regex("(?<=\")[^\"]*(?=\")|[^\" ]+");
            //string[] ogr2ogrOptions = re.Matches(options).Cast<Match>().Select(m => m.Value).ToArray();

            ///Declare trees
            GH_Structure<GH_Curve> featureExtents = new GH_Structure<GH_Curve>();
            GH_Structure<GH_String> fieldNames = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fieldValues = new GH_Structure<GH_String>();
            GH_Structure<IGH_GeometricGoo> featureGoo = new GH_Structure<IGH_GeometricGoo>();



            ///Setup for conversion of ogr geometry to gh geometry
            ///Reserve one processor for GUI
            int totalMaxConcurrancy = System.Environment.ProcessorCount - 1;
            ConcurrentDictionary<GH_Path, OSGeo.OGR.Geometry> geomDict = new ConcurrentDictionary<GH_Path, OSGeo.OGR.Geometry>();
            ConcurrentDictionary<GH_Path, List<IGH_GeometricGoo>> gooDict = new ConcurrentDictionary<GH_Path, List<IGH_GeometricGoo>>();



            ///Loop through each layer. Layers usually occur in Geodatabase GDB format. SHP usually has only one layer.
            for (int iLayer = 0; iLayer < dataSource.GetLayerCount(); iLayer++)
            {
                OSGeo.OGR.Layer ogrLayer = dataSource.GetLayerByIndex(iLayer);

                if (ogrLayer == null)
                {
                    Console.WriteLine($"Couldn't fetch advertised layer {iLayer}");
                    return;
                }

                long count = ogrLayer.GetFeatureCount(1);
                int featureCount = System.Convert.ToInt32(count);

                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Layer #{iLayer} {ogrLayer.GetName()} has {featureCount} features");

                ///Get the spatial reference of the input vector file and set to WGS84 if not known
                OSGeo.OSR.SpatialReference sourceSRS = new SpatialReference(Osr.SRS_WKT_WGS84_LAT_LONG);
                string spatialReference = GetSpatialReferenceSRS(ogrLayer, iLayer, dataSource, sourceSRS);
                sourceSRS.SetFromUserInput(spatialReference);

                ///Get OGR envelope of the data in the layer in the sourceSRS
                OSGeo.OGR.Envelope envelopeOgr = new OSGeo.OGR.Envelope();
                ogrLayer.GetExtent(envelopeOgr, 1);

                ///If not set, set the earth anchor point to the origin of the sourceSRS
                if (!Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthLocationIsSet())
                {
                    OSGeo.OGR.Geometry sourceOrigin = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
                    sourceOrigin.AddPoint(0.0, 0.0, 0.0);
                    sourceOrigin.AssignSpatialReference(sourceSRS);
                    sourceOrigin.TransformTo(new SpatialReference(Osr.SRS_WKT_WGS84_LAT_LONG));
                    EarthAnchorPoint eap = new EarthAnchorPoint();
                    eap.EarthBasepointLatitude = sourceOrigin.GetY(0);
                    eap.EarthBasepointLongitude = sourceOrigin.GetX(0);

                    ///Set new EAP
                    Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint = eap;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The earth anchor point was not previously set and has now been set to the origin of the data's spatial reference system.");

                }

                ///These transforms move and scale in order to go from heronSRS to XYZ and vice versa
                Transform heronSRSToSourceSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(sourceSRS);
                ///Need to refresh transform in this context in order to deal with scenario where no EAP is set yet--envelopePolyline returns null.  Not sure why the refresh is needed...
                userSRSToHeronSRSTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);

                ///Get bounding box based on OGR envelope.  When using a different SRS from the source, the envelope will be skewed, so 
                OSGeo.OGR.Geometry envelopeRing = new OSGeo.OGR.Geometry(wkbGeometryType.wkbLinearRing);
                envelopeRing.AddPoint(envelopeOgr.MinX, envelopeOgr.MinY, 0.0);
                envelopeRing.AddPoint(envelopeOgr.MaxX, envelopeOgr.MinY, 0.0);
                envelopeRing.AddPoint(envelopeOgr.MaxX, envelopeOgr.MaxY, 0.0);
                envelopeRing.AddPoint(envelopeOgr.MinX, envelopeOgr.MaxY, 0.0);
                envelopeRing.AddPoint(envelopeOgr.MinX, envelopeOgr.MinY, 0.0);
                envelopeRing.AssignSpatialReference(sourceSRS);
                envelopeRing.TransformTo(heronSRS);

                Curve envelopePolyline = Heron.Convert.OgrRingToCurve(envelopeRing, userSRSToHeronSRSTransform);

                var envelopeBox = envelopePolyline.GetBoundingBox(false);
                Rectangle3d recUser = new Rectangle3d(Plane.WorldXY, envelopeBox.Corner(true, true, true), envelopeBox.Corner(false, false, false));

                featureExtents.Append(new GH_Curve(recUser.ToNurbsCurve()), new GH_Path(iLayer));
                ///Add skewed envelope in sourceSRS if preferred
                //featureExtents.Append(new GH_Curve(envelopePolyline), new GH_Path(iLayer));


                if (boundary.Count == 0 && clipIt == false)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clipping boundary has not been set. File extents will be used instead.");
                    boundary.Add(recUser.ToNurbsCurve());
                }

                else if (boundary.Count > 0 && clipIt == false)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Boundary is set, but 'Clip with boundary' has been unchecked in the menu.  File extents will be used instead.");
                }

                ///Get the field names
                OSGeo.OGR.FeatureDefn def = ogrLayer.GetLayerDefn();
                List<string> fieldnames = new List<string>();
                for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
                {
                    OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iAttr);
                    fieldNames.Append(new GH_String(fdef.GetNameRef()), new GH_Path(iLayer));
                }

                ///Check if boundary is inside extents
                bool boundaryInside = true;
                if (boundary.Count > 0 && boundary != null)
                {
                    foreach (var b in boundary)
                    {
                        if (boundaryNotNull && boundaryValid && boundaryClosed)
                        {
                            if (Curve.PlanarClosedCurveRelationship(recUser.ToNurbsCurve(), b, Plane.WorldXY, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance) == RegionContainment.Disjoint)
                            {
                                boundaryInside = false;
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more clipping boundaries may be outside the bounds of the vector datasource.");
                            }
                        }
                    }
                }

                ///Check if boundary is contained in extent
                if (!recUser.IsValid || ((recUser.Height == 0) && (recUser.Width == 0) && clipIt == true))
                {
                    ///Get field data if even if no geometry is present in the layer
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more vector datasource bounds are not valid.");
                    OSGeo.OGR.Feature feat;
                    int m = 0;

                    while ((feat = ogrLayer.GetNextFeature()) != null)
                    {
                        ///Loop through field values
                        for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                        {
                            OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                            fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(iLayer, m));
                            fdef.Dispose();
                        }
                        m++;
                        feat.Dispose();
                    }
                }

                else if (boundary.Count == 0 && clipIt == true) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Define a boundary or uncheck 'Clip with boundary' in the menu.");

                else
                {

                    ///Create clipping polygon from mesh
                    //OSGeo.OGR.Geometry clippingPolygon = Heron.Convert.MeshToMultiPolygon(boundaryMesh, heronSRSToSourceSRSTransform);
                    OSGeo.OGR.Geometry clippingPolygon = Heron.Convert.MeshToMultiPolygon(boundaryMesh, heronSRSToUserSRSTransform);
                    clippingPolygon.AssignSpatialReference(heronSRS);
                    clippingPolygon.TransformTo(sourceSRS);

                    ///Clip Shapefile
                    ///http://pcjericks.github.io/py-gdalogr-cookbook/vector_layers.html
                    OSGeo.OGR.Layer clipped_layer = ogrLayer;

                    if (clipIt)
                    {
                        clipped_layer.SetSpatialFilter(clippingPolygon);
                    }

                    ///Loop through geometry
                    OSGeo.OGR.Feature feat;
                    def = clipped_layer.GetLayerDefn();

                    int m = 0;
                    while ((feat = clipped_layer.GetNextFeature()) != null)
                    {
                        OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();
                        OSGeo.OGR.Geometry sub_geomUser;

                        if (geomUser.GetSpatialReference() == null) { geomUser.AssignSpatialReference(sourceSRS); }

                        geomUser.TransformTo(heronSRS);

                        if (feat.GetGeometryRef() != null)
                        {
                            if (!pointsOnly)
                            {
                                ///Convert GDAL geometries to IGH_GeometricGoo
                                //featureGoo.AppendRange(Heron.Convert.OgrGeomToGHGoo(geomUser, userSRSToHeronSRSTransform), new GH_Path(iLayer, m));
                                geomDict[new GH_Path(iLayer, m)] = geomUser.Clone();

                                /// Get Feature Values
                                if (fieldValues.PathExists(new GH_Path(iLayer, m)))
                                {
                                    fieldValues.get_Branch(new GH_Path(iLayer, m)).Clear();
                                }
                                for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                {
                                    OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                    if (feat.IsFieldSet(iField))
                                    {
                                        fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(iLayer, m));
                                    }
                                    else
                                    {
                                        fieldValues.Append(new GH_String("null"), new GH_Path(iLayer, m));
                                    }
                                }
                                ///End get Feature Values
                            }

                            else ///Output only points
                            {
                                ///Start get points if open polylines and points
                                for (int gpc = 0; gpc < geomUser.GetPointCount(); gpc++)
                                {

                                    ///Loop through geometry points
                                    double[] ogrPtUser = new double[3];
                                    geomUser.GetPoint(gpc, ogrPtUser);
                                    Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                    pt3DUser.Transform(userSRSToHeronSRSTransform);

                                    featureGoo.Append(new GH_Point(pt3DUser), new GH_Path(iLayer, m));

                                    ///End loop through geometry points


                                    /// Get Feature Values
                                    if (fieldValues.PathExists(new GH_Path(iLayer, m)))
                                    {
                                        fieldValues.get_Branch(new GH_Path(iLayer, m)).Clear();
                                    }
                                    for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                    {
                                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                        if (feat.IsFieldSet(iField))
                                        {
                                            fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(iLayer, m));
                                        }
                                        else
                                        {
                                            fieldValues.Append(new GH_String("null"), new GH_Path(iLayer, m));
                                        }
                                    }
                                    ///End Get Feature Values
                                }
                                ///End getting points if open polylines or points


                                ///Start getting points if closed polylines and multipolygons
                                for (int gi = 0; gi < geomUser.GetGeometryCount(); gi++)
                                {

                                    sub_geomUser = geomUser.GetGeometryRef(gi);
                                    OSGeo.OGR.Geometry subsub_geomUser;

                                    if (sub_geomUser.GetGeometryCount() > 0)
                                    {
                                        for (int n = 0; n < sub_geomUser.GetGeometryCount(); n++)
                                        {
                                            subsub_geomUser = sub_geomUser.GetGeometryRef(n);

                                            ///Loop through geometry points
                                            for (int ptnum = 0; ptnum < subsub_geomUser.GetPointCount(); ptnum++)
                                            {
                                                double[] ogrPtUser = new double[3];
                                                subsub_geomUser.GetPoint(ptnum, ogrPtUser);
                                                Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                                pt3DUser.Transform(userSRSToHeronSRSTransform);
                                                featureGoo.Append(new GH_Point(pt3DUser), new GH_Path(iLayer, m, gi, n));
                                            }
                                            ///End loop through geometry points

                                            subsub_geomUser.Dispose();
                                        }
                                    }

                                    else
                                    {
                                        ///Loop through geometry points
                                        for (int ptnum = 0; ptnum < sub_geomUser.GetPointCount(); ptnum++)
                                        {
                                            double[] ogrPtUser = new double[3];
                                            sub_geomUser.GetPoint(ptnum, ogrPtUser);
                                            Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                            pt3DUser.Transform(userSRSToHeronSRSTransform);
                                            featureGoo.Append(new GH_Point(pt3DUser), new GH_Path(iLayer, m, gi));
                                        }
                                        ///End loop through geometry points
                                    }

                                    sub_geomUser.Dispose();

                                    /// Get Feature Values
                                    if (fieldValues.PathExists(new GH_Path(iLayer, m)))
                                    {
                                        fieldValues.get_Branch(new GH_Path(iLayer, m)).Clear();
                                    }
                                    for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                    {
                                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                        if (feat.IsFieldSet(iField))
                                        {
                                            fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(iLayer, m));
                                        }
                                        else
                                        {
                                            fieldValues.Append(new GH_String("null"), new GH_Path(iLayer, m));
                                        }
                                    }
                                    ///End Get Feature Values
                                }
                            }
                        }
                        m++;
                        geomUser.Dispose();
                        feat.Dispose();
                    }///end while loop through features

                }///end clipped layer else statement

                ogrLayer.Dispose();

            }///end loop through layers

            dataSource.Dispose();

            ///Multi thread conversion of ogr geometry to gh geometry
            Parallel.ForEach(geomDict, new ParallelOptions
            { MaxDegreeOfParallelism = totalMaxConcurrancy - 1 },
            geomItem =>
            {
                gooDict[geomItem.Key] = Heron.Convert.OgrGeomToGHGoo(geomItem.Value, userSRSToHeronSRSTransform);
            }
            );

            foreach (var kvp in gooDict)
            {
                featureGoo.AppendRange(kvp.Value, kvp.Key);
            }


            DA.SetDataTree(0, featureExtents);
            DA.SetDataTree(1, fieldNames);
            DA.SetDataTree(2, fieldValues);
            DA.SetDataTree(3, featureGoo);
        }

        private string GetSpatialReferenceSRS(OSGeo.OGR.Layer layer, int iLayer, DataSource dataSource, SpatialReference sourceSRS)
        {
            string spatialReference;
            if (layer.GetSpatialRef() == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) is missing.  SRS set automatically set to WGS84.");
                Driver driver = dataSource.GetDriver();
                if (driver.GetName() == "MVT") { sourceSRS.SetFromUserInput("EPSG:3857"); }
                else { sourceSRS.SetFromUserInput("WGS84"); } ///this seems to work where SetWellKnownGeogCS doesn't

                string pretty;
                sourceSRS.ExportToPrettyWkt(out pretty, 0);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, pretty);
                return spatialReference = "Spatial Reference System (SRS) is missing.  SRS set automatically set to WGS84.";
            }
            else
            {
                if (layer.GetSpatialRef().Validate() != 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) is unknown or unsupported.  SRS set automatically set to WGS84.");
                    sourceSRS.SetWellKnownGeogCS("WGS84");
                    return spatialReference = "Spatial Reference System (SRS) is unknown or unsupported.  SRS set automatically set to WGS84.";
                }
                else
                {
                    sourceSRS = layer.GetSpatialRef();
                    sourceSRS.ExportToWkt(out spatialReference, null);
                    try
                    {
                        int sourceSRSInt = Int16.Parse(sourceSRS.GetAuthorityCode(null));
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "The source Spatial Reference System (SRS) from layer " + layer.GetName() + " is EPSG:" + sourceSRSInt + ".");
                    }
                    catch
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Failed to get an EPSG Spatial Reference System (SRS) integer from layer " + layer.GetName() + ".");
                    }
                }

            }
            return spatialReference;
        }

        private List<OSGeo.OGR.Layer> GetLayersSRS(DataSource dataSource)
        {
            List<OSGeo.OGR.Layer> layerSet = new List<OSGeo.OGR.Layer>();


            for (int layerIndex = 0; layerIndex < dataSource.GetLayerCount(); layerIndex++)
            {
                OSGeo.OGR.Layer layer = dataSource.GetLayerByIndex(layerIndex);

                if (layer == null)
                {
                    Console.WriteLine("Couldn't fetch advertised layer " + layerIndex);
                    System.Environment.Exit(-1);
                }
                else
                {
                    layerSet.Add(layer);
                }
            }
            return layerSet;
        }
        private DataSource CreateDataSourceSRS(string shpFilePath)
        {
            //RESTful.GdalConfiguration.ConfigureOgr();
            //OSGeo.OGR.Ogr.RegisterAll();
            OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(shpFilePath, 0);

            if (dataSource == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
            }

            return dataSource;
        }


        /// <summary>
        /// Menu Items
        /// </summary>

        private bool clipIt = true;
        private bool pointsOnly = false;
        public bool ClipIt
        {
            get { return clipIt; }
            set
            {
                clipIt = value;
                if ((clipIt))
                {
                    //Message = "Clipped";
                }
                else
                {
                    //Message = "Not Clipped";
                }
            }
        }

        public bool PointsOnly
        {
            get { return pointsOnly; }
            set { pointsOnly = value; }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean("ClipIt", ClipIt);
            writer.SetBoolean("PointsOnly", PointsOnly);
            return base.Write(writer);
        }
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            ClipIt = reader.GetBoolean("ClipIt");
            PointsOnly = reader.GetBoolean("PointsOnly");
            return base.Read(reader);
        }

        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            ToolStripMenuItem clipItItem = Menu_AppendItem(menu, "Clip with boundary", Menu_ClippedClicked, true, ClipIt);
            clipItItem.ToolTipText = "When checked, the vector data is clipped to the boundary input.";
            ToolStripMenuItem pointsOnlyItem = Menu_AppendItem(menu, "Output points only", Menu_PointsOnlyClicked, true, PointsOnly);
            pointsOnlyItem.ToolTipText = "By default, vector geometery is converted to Rhino equivalents.  Uncheck to output only the points associated with the vector geometry.";
        }
        private void Menu_ClippedClicked(object sender, EventArgs e)
        {
            RecordUndoEvent("ClipIt");
            ClipIt = !ClipIt;
            ExpireSolution(true);
        }
        private void Menu_PointsOnlyClicked(object sender, EventArgs e)
        {
            RecordUndoEvent("PointsOnly");
            PointsOnly = !PointsOnly;
            ExpireSolution(true);
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.shp;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{CE8D1508-BCF7-4DD6-AFF8-958E22031A14}"); }
        }
    }
}
