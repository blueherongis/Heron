using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using OSGeo.OSR;
using OSGeo.OGR;
using Rhino.Geometry;
using Rhino.DocObjects;
using System;
using System.Collections.Generic;



namespace Heron
{
    public class ImportVector_DEPRECATED20220730 : HeronComponent
    {
        //Class Constructor
        public ImportVector_DEPRECATED20220730() : base("Import Vector", "ImportVector_old", "Import vector GIS data clipped to a boundary, including SHP, GeoJSON, OSM, KML, MVT and GDB folders.", "GIS Import | Export")
        {

        }

        ///Retiring this component to add HeronSRS functionality 
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.hidden; }
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("Vector Data Location", "filePath", "File path for the vector data input", GH_ParamAccess.item);
            pManager.AddTextParameter("User Spatial Reference System", "userSRS", "Custom SRS", GH_ParamAccess.item, "WGS84");
            pManager.AddBooleanParameter("Crop file", "cropIt", "Crop the file to boundary?", GH_ParamAccess.item, true);
            pManager[0].Optional = true;
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Extents", "extents", "Bounding box of all the vector data features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Source Spatial Reference System", "sourceSRS", "Spatial Reference of the input vector data", GH_ParamAccess.tree);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the vector data features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddPointParameter("FeaturePoints", "featurePoints", "Point geometry describing each feature", GH_ParamAccess.tree);

            pManager.AddPointParameter("FeaturePointsUser", "featurePointsUser", "Point geometry describing each feature", GH_ParamAccess.tree);
            pManager.AddCurveParameter("ExtentUser", "extentsUser", "Bounding box of all Shapefile features given the userSRS", GH_ParamAccess.tree);

            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry contained in the feature", GH_ParamAccess.tree);
            pManager.AddTextParameter("Geometry Type", "geoType", "Type of geometry contained in the feature", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {

            ///Gather GHA inputs
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            string shpFilePath = string.Empty;
            DA.GetData<string>("Vector Data Location", ref shpFilePath);

            bool cropIt = true;
            DA.GetData<Boolean>("Crop file", ref cropIt);

            string userSRStext = "WGS84";
            DA.GetData<string>(2, ref userSRStext);


            ///GDAL setup
            ///Some preliminary testing has been done to read SHP, GeoJSON, OSM, KML, MVT, GML and GDB
            ///It can be spotty with KML, MVT and GML and doesn't throw informative errors.  Likely has to do with getting a valid CRS and 
            ///TODO: resolve errors with reading KML, MVT, GML.

            DataSource dataSource = CreateDataSource(shpFilePath);
            List<OSGeo.OGR.Layer> layerSet = GetLayers(dataSource); 

            ///Declare trees
            GH_Structure<GH_Rectangle> recs = new GH_Structure<GH_Rectangle>();
            GH_Structure<GH_String> spatialReferences = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fnames = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fset = new GH_Structure<GH_String>();
            GH_Structure<GH_Point> gset = new GH_Structure<GH_Point>();

            GH_Structure<GH_Point> gsetUser = new GH_Structure<GH_Point>();
            GH_Structure<GH_Rectangle> recsUser = new GH_Structure<GH_Rectangle>();

            GH_Structure<GH_String> gtype = new GH_Structure<GH_String>();
            GH_Structure<IGH_GeometricGoo> gGoo = new GH_Structure<IGH_GeometricGoo>();


            ///Loop through each layer. Layers usually occur in Geodatabase GDB format. SHP usually has only one layer.
            for (int iLayer = 0; iLayer < dataSource.GetLayerCount(); iLayer++)
            {
                OSGeo.OGR.Layer ogrLayer = dataSource.GetLayerByIndex(iLayer);

                if (ogrLayer == null)
                {
                    Console.WriteLine($"Couldn't fetch advertised layer {iLayer}");
                    System.Environment.Exit(-1);
                }

                long count = ogrLayer.GetFeatureCount(1);
                int featureCount = System.Convert.ToInt32(count);

         

                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Layer #{iLayer} {ogrLayer.GetName()} has {featureCount} features");

                ///Get the spatial reference of the input vector file and set to WGS84 if not known
                ///
                OSGeo.OSR.SpatialReference sourceSRS = new SpatialReference(Osr.SRS_WKT_WGS84_LAT_LONG);
                string spatialReference = GetSpatialReference(ogrLayer, iLayer, dataSource, sourceSRS);
                sourceSRS.SetFromUserInput(spatialReference);
                spatialReferences.Append(new GH_String(spatialReference), new GH_Path(iLayer));
                

                ///Set transform from input spatial reference to Rhino spatial reference
                ///TODO: look into adding a step for transforming to CRS set in SetCRS 
                OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
                rhinoSRS.SetWellKnownGeogCS("WGS84");

                ///TODO: verify the userSRS is valid
                ///TODO: use this as override of global SetSRS
                OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
                userSRS.SetFromUserInput(userSRStext);

                ///Get OGR envelope of the data in the layer in the sourceSRS
                OSGeo.OGR.Envelope envelopeOgr = new OSGeo.OGR.Envelope();
                ogrLayer.GetExtent(envelopeOgr, 1);

                ///If not set, set the earth anchor point to the center of the data
                if (!Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthLocationIsSet())
                {
                    OSGeo.OGR.Geometry sourceCenter = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
                    sourceCenter.AddPoint(envelopeOgr.MinX + (envelopeOgr.MaxX - envelopeOgr.MinX)/2, envelopeOgr.MinY + (envelopeOgr.MaxY - envelopeOgr.MinY)/2, 0.0);
                    sourceCenter.AssignSpatialReference(sourceSRS);
                    sourceCenter.TransformTo(userSRS);
                    EarthAnchorPoint eap = new EarthAnchorPoint();
                    eap.EarthBasepointLatitude = sourceCenter.GetY(0);
                    eap.EarthBasepointLongitude = sourceCenter.GetX(0);

                    ///Set new EAP
                    Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint = eap;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "The earth anchor point was not previously set and has now been set to the center of the data.");

                }

                ///These transforms move and scale in order to go from userSRS to XYZ and vice versa
                Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);
                Transform modelToUserSRSTransform = Heron.Convert.GetModelToUserSRSTransform(userSRS);
                Transform sourceToModelSRSTransform = Heron.Convert.GetUserSRSToModelTransform(sourceSRS);
                Transform modelToSourceSRSTransform = Heron.Convert.GetModelToUserSRSTransform(sourceSRS);


                OSGeo.OGR.Geometry extMinSourceOgr = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
                extMinSourceOgr.AddPoint(envelopeOgr.MinX, envelopeOgr.MinY, 0.0);
                extMinSourceOgr.AssignSpatialReference(sourceSRS);

                OSGeo.OGR.Geometry extMaxSourceOgr = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
                extMaxSourceOgr.AddPoint(envelopeOgr.MaxX, envelopeOgr.MaxY, 0.0);
                extMaxSourceOgr.AssignSpatialReference(sourceSRS);


                ///Get extents in Rhino SRS
                Point3d extPTmin = Heron.Convert.OgrPointToPoint3d(extMinSourceOgr, sourceToModelSRSTransform);
                Point3d extPTmax = Heron.Convert.OgrPointToPoint3d(extMaxSourceOgr, sourceToModelSRSTransform);

                Rectangle3d rec = new Rectangle3d(Plane.WorldXY, extPTmin, extPTmax);
                recs.Append(new GH_Rectangle(rec), new GH_Path(iLayer));

                ///Get extents in userSRS
                ///Can give odd results if crosses 180 longitude
                extMinSourceOgr.TransformTo(userSRS);
                extMaxSourceOgr.TransformTo(userSRS);

                Point3d extPTminUser = Heron.Convert.OgrPointToPoint3d(extMinSourceOgr, userSRSToModelTransform);
                Point3d extPTmaxUser = Heron.Convert.OgrPointToPoint3d(extMaxSourceOgr, userSRSToModelTransform);

                Rectangle3d recUser = new Rectangle3d(Plane.WorldXY, extPTminUser, extPTmaxUser);
                recsUser.Append(new GH_Rectangle(recUser), new GH_Path(iLayer));



                if (boundary.Count == 0 && cropIt == true)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Define a boundary or set cropIt to False");
                }

                else if (boundary.Count == 0 && cropIt == false)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clipping boundary has not been defined. File extents will be used instead");
                    boundary.Add(rec.ToNurbsCurve());
                }



                ///Loop through input boundaries
                for (int i = 0; i < boundary.Count; i++)
                {
                    OSGeo.OGR.FeatureDefn def = ogrLayer.GetLayerDefn();

                    ///Get the field names
                    List<string> fieldnames = new List<string>();
                    for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
                    {
                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iAttr);
                        fnames.Append(new GH_String(fdef.GetNameRef()), new GH_Path(i, iLayer));
                    }

                    ///Check if boundary is contained in extent
                    if (!rec.IsValid || ((rec.Height == 0) && (rec.Width == 0)))
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
                                fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                fdef.Dispose();
                            }
                            m++;
                            feat.Dispose();
                        }
                    }

                    else if (boundary[i] == null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping boundary " + i + " not set.");

                    else if (!boundary[i].IsValid) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping boundary " + i + "  is not valid.");

                    else if (rec.IsValid && Curve.PlanarClosedCurveRelationship(rec.ToNurbsCurve(), boundary[i], Plane.WorldXY, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance) == RegionContainment.Disjoint)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more clipping boundaries may be outside the bounds of the vector datasource.");
                    }

                    else
                    {
                        ///Create bounding box for clipping geometry
                        Point3d min = boundary[i].GetBoundingBox(true).Min;
                        Point3d max = boundary[i].GetBoundingBox(true).Max;
                        min.Transform(modelToSourceSRSTransform);
                        max.Transform(modelToSourceSRSTransform);
                        double[] minpT = new double[3];
                        double[] maxpT = new double[3];

                        minpT[0] = min.X;
                        minpT[1] = min.Y;
                        minpT[2] = min.Z;
                        maxpT[0] = max.X;
                        maxpT[1] = max.Y;
                        maxpT[2] = max.Z;


                        OSGeo.OGR.Geometry ebbox = OSGeo.OGR.Geometry.CreateFromWkt("POLYGON((" + minpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + minpT[1] + "))");

                        ///Create bounding box for clipping geometry
                        ///Not working on MVT type files
                        //boundary[i].Transform(modelToSourceSRSTransform);
                        //OSGeo.OGR.Geometry ebbox = Heron.Convert.CurveToOgrPolygon(boundary[i]);


                        ///Clip Shapefile
                        ///http://pcjericks.github.io/py-gdalogr-cookbook/vector_layers.html
                        OSGeo.OGR.Layer clipped_layer = ogrLayer;

                        if (cropIt)
                        {
                            clipped_layer.SetSpatialFilter(ebbox);
                        }

                        ///Loop through geometry
                        OSGeo.OGR.Feature feat;
                        def = clipped_layer.GetLayerDefn();

                        int m = 0;
                        while ((feat = clipped_layer.GetNextFeature()) != null)
                        {

                            OSGeo.OGR.Geometry geom = feat.GetGeometryRef();
                            OSGeo.OGR.Geometry sub_geom;

                            OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();
                            OSGeo.OGR.Geometry sub_geomUser;

                            ///reproject geometry to WGS84 and userSRS
                            ///TODO: look into using the SetCRS global variable here
                            if (geom.GetSpatialReference() == null) { geom.AssignSpatialReference(sourceSRS); }
                            if (geomUser.GetSpatialReference() == null) { geomUser.AssignSpatialReference(sourceSRS); }

                            geom.TransformTo(rhinoSRS);
                            geomUser.TransformTo(userSRS);
                            gtype.Append(new GH_String(geom.GetGeometryName()), new GH_Path(i, iLayer, m));

                            if (feat.GetGeometryRef() != null)
                            {

                                ///Convert GDAL geometries to IGH_GeometricGoo
                                gGoo.AppendRange(Heron.Convert.OgrGeomToGHGoo(geomUser, userSRSToModelTransform), new GH_Path(i, iLayer, m));

                                /// Get Feature Values
                                if (fset.PathExists(new GH_Path(i, iLayer, m)))
                                {
                                    fset.get_Branch(new GH_Path(i, iLayer, m)).Clear();
                                }
                                for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                {
                                    OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                    if (feat.IsFieldSet(iField))
                                    {
                                        fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                    }
                                    else
                                    {
                                        fset.Append(new GH_String("null"), new GH_Path(i, iLayer, m));
                                    }
                                }
                                ///End get Feature Values


                                ///Start get points if open polylines and points
                                for (int gpc = 0; gpc < geom.GetPointCount(); gpc++)
                                {
                                    ///Loop through geometry points for Rhino SRS
                                    double[] ogrPt = new double[3];
                                    geom.GetPoint(gpc, ogrPt);
                                    Point3d pt3D = new Point3d(ogrPt[0], ogrPt[1], ogrPt[2]);
                                    pt3D.Transform(Heron.Convert.WGSToXYZTransform());

                                    gset.Append(new GH_Point(pt3D), new GH_Path(i, iLayer, m));

                                    ///Loop through geometry points for User SRS
                                    double[] ogrPtUser = new double[3];
                                    geomUser.GetPoint(gpc, ogrPtUser);
                                    Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                    pt3DUser.Transform(userSRSToModelTransform);

                                    gsetUser.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m));

                                    ///End loop through geometry points


                                    /// Get Feature Values
                                    if (fset.PathExists(new GH_Path(i, iLayer, m)))
                                    {
                                        fset.get_Branch(new GH_Path(i, iLayer, m)).Clear();
                                    }
                                    for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                    {
                                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                        if (feat.IsFieldSet(iField))
                                        {
                                            fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                        }
                                        else
                                        {
                                            fset.Append(new GH_String("null"), new GH_Path(i, iLayer, m));
                                        }
                                    }
                                    ///End Get Feature Values
                                }
                                ///End getting points if open polylines or points


                                ///Start getting points if closed polylines and multipolygons
                                for (int gi = 0; gi < geom.GetGeometryCount(); gi++)
                                {
                                    sub_geom = geom.GetGeometryRef(gi);
                                    OSGeo.OGR.Geometry subsub_geom;

                                    sub_geomUser = geomUser.GetGeometryRef(gi);
                                    OSGeo.OGR.Geometry subsub_geomUser;

                                    if (sub_geom.GetGeometryCount() > 0)
                                    {
                                        for (int n = 0; n < sub_geom.GetGeometryCount(); n++)
                                        {

                                            subsub_geom = sub_geom.GetGeometryRef(n);
                                            subsub_geomUser = sub_geomUser.GetGeometryRef(n);

                                            for (int ptnum = 0; ptnum < subsub_geom.GetPointCount(); ptnum++)
                                            {
                                                ///Loop through geometry points
                                                double[] ogrPt = new double[3];
                                                subsub_geom.GetPoint(ptnum, ogrPt);
                                                Point3d pt3D = new Point3d(ogrPt[0], ogrPt[1], ogrPt[2]);
                                                pt3D.Transform(Heron.Convert.WGSToXYZTransform());

                                                gset.Append(new GH_Point(pt3D), new GH_Path(i, iLayer, m, gi, n));

                                                ///Loop through geometry points for User SRS
                                                double[] ogrPtUser = new double[3];
                                                subsub_geomUser.GetPoint(ptnum, ogrPtUser);
                                                Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                                pt3DUser.Transform(userSRSToModelTransform);

                                                gsetUser.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m, gi, n));

                                                ///End loop through geometry points
                                            }
                                            subsub_geom.Dispose();
                                            subsub_geomUser.Dispose();
                                        }
                                    }

                                    else
                                    {
                                        for (int ptnum = 0; ptnum < sub_geom.GetPointCount(); ptnum++)
                                        {
                                            ///Loop through geometry points
                                            double[] ogrPt = new double[3];
                                            sub_geom.GetPoint(ptnum, ogrPt);
                                            Point3d pt3D = new Point3d(ogrPt[0], ogrPt[1], ogrPt[2]);
                                            pt3D.Transform(Heron.Convert.WGSToXYZTransform());

                                            gset.Append(new GH_Point(pt3D), new GH_Path(i, iLayer, m, gi));

                                            ///Loop through geometry points for User SRS
                                            double[] ogrPtUser = new double[3];
                                            sub_geomUser.GetPoint(ptnum, ogrPtUser);
                                            Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                            pt3DUser.Transform(userSRSToModelTransform);

                                            gsetUser.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m, gi));

                                            ///End loop through geometry points
                                        }
                                    }

                                    sub_geom.Dispose();
                                    sub_geomUser.Dispose();


                                    /// Get Feature Values
                                    if (fset.PathExists(new GH_Path(i, iLayer, m)))
                                    {
                                        fset.get_Branch(new GH_Path(i, iLayer, m)).Clear();
                                    }
                                    for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                    {
                                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                        if (feat.IsFieldSet(iField))
                                        {
                                            fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                        }
                                        else
                                        {
                                            fset.Append(new GH_String("null"), new GH_Path(i, iLayer, m));
                                        }
                                    }
                                    ///End Get Feature Values


                                }


                                //m++;

                            }
                            m++;
                            geom.Dispose();
                            geomUser.Dispose();
                            feat.Dispose();
                        }///end while loop through features

                    }///end clipped layer else statement

                }///end loop through boundaries

                ogrLayer.Dispose();

            }///end loop through layers

            dataSource.Dispose();

            DA.SetDataTree(0, recs);
            DA.SetDataTree(1, spatialReferences);
            DA.SetDataTree(2, fnames);
            DA.SetDataTree(3, fset);
            DA.SetDataTree(4, gset);
            DA.SetDataTree(5, gsetUser);
            DA.SetDataTree(6, recsUser);
            DA.SetDataTree(7, gGoo);
            DA.SetDataTree(8, gtype);
        }

        private string GetSpatialReference(OSGeo.OGR.Layer layer, int iLayer, DataSource dataSource, SpatialReference sourceSRS)
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

        private List<OSGeo.OGR.Layer> GetLayers(DataSource dataSource)
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
        private DataSource CreateDataSource(string shpFilePath)
        {
            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();
            OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(shpFilePath, 0);

            if (dataSource == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
            }

            return dataSource;
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
            get { return new Guid("{CCDA0ABF-ED36-4502-95EA-FD3024376F46}"); }
        }
    }
}
