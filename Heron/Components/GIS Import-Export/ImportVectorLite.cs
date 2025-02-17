using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using OSGeo.OGR;
using OSGeo.OSR;
using Rhino.Geometry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Heron
{
    public class ImportVectorLite : HeronComponent
    {
        //Class Constructor
        public ImportVectorLite() : base("Import Vector Lite", "IVL", "This is a basic version of the Import Vector component " +
            "allowing the import of GIS data, including SHP, GeoJSON, OSM, KML, MVT and GDB folders.  The data will be imported in the source's units " +
            "and spatial reference system, Heron's SetSRS and Rhino's EarthAnchorPoint will have no effect.", "GIS Import | Export")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for clipping the vector data.  " +
                "Curves on the same branch will be considered as one polygon for clipping.", GH_ParamAccess.tree);
            pManager.AddTextParameter("File Path", "filepath", "File path(s) for the vector data source(s).", GH_ParamAccess.tree);
            pManager[0].Optional = true;
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
            GH_Structure<GH_Curve> boundaryTree = new GH_Structure<GH_Curve>();
            DA.GetDataTree<GH_Curve>("Boundary", out boundaryTree);

            bool clipIt = false;
            if (boundaryTree.Count() > 0) { clipIt = true; }

            GH_Structure<GH_String> shpFilePathTree = new GH_Structure<GH_String>();
            DA.GetDataTree<GH_String>("File Path", out shpFilePathTree);

            ///GDAL setup
            Heron.GdalConfiguration.ConfigureOgr();

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

            Transform transform = new Transform(1);

            ///Loop through boundary inputs
            int boundaryCount = 1; ///Allow entry into boundary loop without a boundary
            if (boundaryTree.Branches.Count > 0) { boundaryCount = boundaryTree.Branches.Count; }

            for (int boundaryBranch = 0; boundaryBranch < boundaryCount; boundaryBranch++)
            {
                ///Cast a list GH_Curves to Curves
                List<Curve> boundaryList = new List<Curve>();

                ///Setup for clipping polygon
                Mesh boundaryMesh = new Mesh();
                bool boundaryNotNull = true, boundaryValid = true, boundaryClosed = true;

                if (boundaryTree.Count() > 0)
                {
                    foreach (var boundary in boundaryTree.get_Branch(boundaryBranch))
                    {
                        Curve crv = null;
                        GH_Convert.ToCurve(boundary, ref crv, GH_Conversion.Primary);
                        boundaryList.Add(crv);
                    }

                    ///Create polygon for clipping geometry
                    if (boundaryList.Count > 0 && clipIt)
                    {
                        ///Check if boundaries are closed and valid 
                        foreach (var b in boundaryList)
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
                            Brep[] boundarySurface = Brep.CreatePlanarBreps(boundaryList, 0.001);
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
                }

                ///Loop through each datasource filepath.  Flatten source tree to a list
                for (int index = 0; index < shpFilePathTree.DataCount; index++)//int index = 0; index < shpFilePathTree.get_Branch(filepathBranchIndex).Count; index++)
                {
                    ///Setup paths to allow no boundary input and clip without boundary
                    var path = new GH_Path(0).AppendElement(index);
                    if (boundaryTree.Count() > 0) { path = boundaryTree.get_Path(boundaryBranch).AppendElement(index); }

                    string dataSourceString = shpFilePathTree.get_DataItem(index).ToString();

                    DataSource dataSource = CreateDataSourceSRS(dataSourceString);
                    List<OSGeo.OGR.Layer> layerSet = GetLayersSRS(dataSource);

                    ///Loop through each layer. Layers usually occur in Geodatabase GDB format. SHP usually has only one layer.
                    for (int iLayer = 0; iLayer < dataSource.GetLayerCount(); iLayer++)
                    {
                        OSGeo.OGR.Layer ogrLayer = dataSource.GetLayerByIndex(iLayer);

                        var layerPath = path.AppendElement(iLayer);

                        if (ogrLayer == null)
                        {
                            Console.WriteLine($"Couldn't fetch advertised layer {iLayer}");
                            return;
                        }

                        long? count = ogrLayer.GetFeatureCount(1);
                        int featureCount = System.Convert.ToInt32(count);

                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Layer #{iLayer} {ogrLayer.GetName()} has {featureCount} features");

                        ///Get the spatial reference of the input vector file and set to WGS84 if not known
                        OSGeo.OSR.SpatialReference sourceSRS = new SpatialReference(Osr.SRS_WKT_WGS84_LAT_LONG);
                        string spatialReference = GetSpatialReferenceSRS(ogrLayer, iLayer, dataSource, sourceSRS);
                        sourceSRS.SetFromUserInput(spatialReference);

                        ///Get OGR envelope of the data in the layer in the sourceSRS
                        OSGeo.OGR.Envelope envelopeOgr = new OSGeo.OGR.Envelope();
                        ogrLayer.GetExtent(envelopeOgr, 1);

                        ///Get bounding box based on OGR envelope.  When using a different SRS from the source, the envelope will be skewed, so 
                        OSGeo.OGR.Geometry envelopeRing = new OSGeo.OGR.Geometry(wkbGeometryType.wkbLinearRing);
                        envelopeRing.AddPoint(envelopeOgr.MinX, envelopeOgr.MinY, 0.0);
                        envelopeRing.AddPoint(envelopeOgr.MaxX, envelopeOgr.MinY, 0.0);
                        envelopeRing.AddPoint(envelopeOgr.MaxX, envelopeOgr.MaxY, 0.0);
                        envelopeRing.AddPoint(envelopeOgr.MinX, envelopeOgr.MaxY, 0.0);
                        envelopeRing.AddPoint(envelopeOgr.MinX, envelopeOgr.MinY, 0.0);
                        envelopeRing.AssignSpatialReference(sourceSRS);

                        Curve envelopePolyline = Heron.Convert.OgrRingToCurve(envelopeRing, transform);

                        var envelopeBox = envelopePolyline.GetBoundingBox(false);
                        Rectangle3d recUser = new Rectangle3d(Plane.WorldXY, envelopeBox.Corner(true, true, true), envelopeBox.Corner(false, false, false));

                        featureExtents.Append(new GH_Curve(recUser.ToNurbsCurve()), layerPath);
                        ///Add skewed envelope in sourceSRS if preferred
                        //featureExtents.Append(new GH_Curve(envelopePolyline), layerPath);

                        if (boundaryList.Count == 0 && clipIt == false)
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clipping boundary has not been set. File extents will be used instead.");
                            boundaryList.Add(recUser.ToNurbsCurve());
                        }

                        ///Get the field names
                        OSGeo.OGR.FeatureDefn def = ogrLayer.GetLayerDefn();
                        List<string> fieldnames = new List<string>();
                        for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
                        {
                            OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iAttr);
                            fieldNames.Append(new GH_String(fdef.GetNameRef()), layerPath);
                        }

                        ///Check if boundary is inside extents
                        if (boundaryList.Count > 0 && boundaryList != null)
                        {
                            foreach (var b in boundaryList)
                            {
                                if (boundaryNotNull && boundaryValid && boundaryClosed)
                                {
                                    if (Curve.PlanarClosedCurveRelationship(recUser.ToNurbsCurve(), b, Plane.WorldXY, 0.001) == RegionContainment.Disjoint)
                                    {
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
                                    fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), layerPath.AppendElement(m));
                                    fdef.Dispose();
                                }
                                m++;
                                feat.Dispose();
                            }
                        }

                        else
                        {
                            ///Create clipping polygon from mesh
                            OSGeo.OGR.Geometry clippingPolygon = Heron.Convert.MeshToMultiPolygon(boundaryMesh, transform);
                            clippingPolygon.AssignSpatialReference(sourceSRS);

                            ///Clip Shapefile
                            ///http://pcjericks.github.io/py-gdalogr-cookbook/vector_layers.html
                            OSGeo.OGR.Layer clipped_layer = ogrLayer;

                            if (clipIt)
                            {
                                clipped_layer.SetSpatialFilter(clippingPolygon);
                            }

                            ///Loop through geometry
                            OSGeo.OGR.Feature feat;
                            def = ogrLayer.GetLayerDefn();

                            int m = 0;
                            while ((feat = ogrLayer.GetNextFeature()) != null)
                            {
                                if (feat.GetGeometryRef() != null)
                                {
                                    OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();
                                    OSGeo.OGR.Geometry sub_geomUser;
                                    var featurePath = layerPath.AppendElement(m);

                                    if (geomUser.GetSpatialReference() == null) { geomUser.AssignSpatialReference(sourceSRS); }

                                    if (!pointsOnly)
                                    {
                                        ///Convert GDAL geometries to IGH_GeometricGoo
                                        //featureGoo.AppendRange(Heron.Convert.OgrGeomToGHGoo(geomUser, userSRSToHeronSRSTransform), new GH_Path(iLayer, m));
                                        //geomDict[new GH_Path(iLayer, m)] = geomUser.Clone();
                                        geomDict[featurePath] = geomUser.Clone();

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
                                                fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), featurePath); // new GH_Path(iLayer, m));
                                            }
                                            else
                                            {
                                                fieldValues.Append(new GH_String("null"), featurePath); // new GH_Path(iLayer, m));
                                            }
                                        }
                                        ///End get Feature Values
                                    }
                                    ///End if not pointsOnly

                                    else ///Output only points
                                    {
                                        ///Start get points if open polylines and points
                                        for (int gpc = 0; gpc < geomUser.GetPointCount(); gpc++)
                                        {

                                            ///Loop through geometry points
                                            double[] ogrPtUser = new double[3];
                                            geomUser.GetPoint(gpc, ogrPtUser);
                                            Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                            featureGoo.Append(new GH_Point(pt3DUser), featurePath);// new GH_Path(iLayer, m));
                                            ///End loop through geometry points


                                            /// Get Feature Values
                                            if (fieldValues.PathExists(featurePath)) //new GH_Path(iLayer, m)))
                                            {
                                                fieldValues.get_Branch(featurePath).Clear(); // new GH_Path(iLayer, m)).Clear();
                                            }
                                            for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                            {
                                                OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                                if (feat.IsFieldSet(iField))
                                                {
                                                    fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), featurePath);// new GH_Path(iLayer, m));
                                                }
                                                else
                                                {
                                                    fieldValues.Append(new GH_String("null"), featurePath);// new GH_Path(iLayer, m));
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
                                                        featureGoo.Append(new GH_Point(pt3DUser), layerPath.AppendElement(m).AppendElement(gi).AppendElement(n));
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
                                                    featureGoo.Append(new GH_Point(pt3DUser), layerPath.AppendElement(m).AppendElement(gi));
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
                                                    fieldValues.Append(new GH_String(feat.GetFieldAsString(iField)), layerPath.AppendElement(m));
                                                }
                                                else
                                                {
                                                    fieldValues.Append(new GH_String("null"), layerPath.AppendElement(m));
                                                }
                                            }
                                            ///End Get Feature Values

                                        }
                                        ///End getting points if closed polylines and multipolygons
                                    }
                                    /// End else pointOnly

                                    m++;
                                    geomUser.Dispose();
                                    feat.Dispose();
                                }
                                /// End if feat.GetGeometryRef() != null
                            }
                            /// End while loop through features
                        }
                        /// End clipped layer else statement

                        ogrLayer.Dispose();

                    } 
                    /// End loop through layers

                    dataSource.Dispose();

                } 
                ///End loop through datasource

            } 
            ///End loop through boundary tree


            ///Multi thread conversion of ogr geometry to gh geometry
            Parallel.ForEach(geomDict, new ParallelOptions
            { MaxDegreeOfParallelism = totalMaxConcurrancy },
            geomItem =>
            {
                gooDict[geomItem.Key] = Heron.Convert.OgrGeomToGHGoo(geomItem.Value, transform);
                //featureGoo.AppendRange(Heron.Convert.OgrGeomToGHGoo(geomItem.Value, transform), geomItem.Key);
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
                layer.Dispose();
            }

            return layerSet;
        }
        private DataSource CreateDataSourceSRS(string shpFilePath)
        {
            //Heron.GdalConfiguration.ConfigureOgr();
            //OSGeo.OGR.Ogr.RegisterAll();
            //OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(shpFilePath, 0);
            if (shpFilePath.ToLower().EndsWith("gml"))
            {
                OSGeo.GDAL.Dataset dataSet = OSGeo.GDAL.Gdal.OpenEx(shpFilePath, 0, null, new string[] {
                    "REMOVE_UNUSED_LAYERS=YES",
                    "REMOVE_UNUSED_FIELDS=YES" 
                    //"VALIDATE=YES",
                    //"XSD=https://schemas.opengis.net/citygml/building/1.0/building.xsd"
                    //"EXPOSE_METADATA_LAYERS=YES"
                    //"REFRESH_CACHE=YES"
                }, null);
                string tempDS = "/vsimem/temp.gpkg";
                OSGeo.GDAL.Gdal.wrapper_GDALVectorTranslateDestName(tempDS, dataSet, new OSGeo.GDAL.GDALVectorTranslateOptions(new string[] { }), null, null);
                OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(tempDS, 0);

                if (dataSource == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                }
                dataSet.Dispose();
                ///Release vsimem file if used for GML import
                ///Get the following error: '...failed: attempt to write a readonly database'
                //OSGeo.GDAL.Gdal.Unlink(tempDS);
                //OSGeo.OGR.Ogr.GetDriverByName("GPKG").DeleteDataSource(tempDS);
                return dataSource;
            }

            else
            {
                OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(shpFilePath, 0);
                if (dataSource == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                }
                return dataSource;
            }

        }


        /// <summary>
        /// Menu Items
        /// </summary>

        private bool pointsOnly = false;

        public bool PointsOnly
        {
            get { return pointsOnly; }
            set { pointsOnly = value; }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean("PointsOnly", PointsOnly);
            return base.Write(writer);
        }
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            PointsOnly = reader.GetBoolean("PointsOnly");
            return base.Read(reader);
        }

        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            ToolStripMenuItem pointsOnlyItem = Menu_AppendItem(menu, "Output points only", Menu_PointsOnlyClicked, true, PointsOnly);
            pointsOnlyItem.ToolTipText = "By default, vector geometery is converted to Rhino equivalents.  Uncheck to output only the points associated with the vector geometry.";
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
            get { return new Guid("5B88A42A-4911-4350-B684-287951C32EDA"); }
        }
    }
}
