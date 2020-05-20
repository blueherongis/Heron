using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using OSGeo.OSR;
using Rhino.Geometry;
using System;
using System.Collections.Generic;



namespace Heron
{
    public class ImportVector : HeronComponent
    {
        //Class Constructor
        public ImportVector() : base("Import Vector", "ImportVector", "Import vector GIS data clipped to a boundary, including SHP, GeoJSON, OSM, KML, MVT and GDB folders.", "GIS Tools")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("Vector Data Location", "fileLoc", "File path for the vector data input", GH_ParamAccess.item);
            pManager.AddTextParameter("User Spatial Reference System", "userSRS", "Custom SRS", GH_ParamAccess.item,"WGS84");
            pManager.AddBooleanParameter("Crop file", "cropIt", "Crop the file to boundary?", GH_ParamAccess.item, true);
            pManager[0].Optional = true;
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("LayerName", "layerName", "Shapefile Layer Name", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("shpFeatureCount", "shpFeatureCount", "Total number of features in the Shapefile", GH_ParamAccess.tree);
            pManager.AddCurveParameter("shpExtent", "shpExtent", "Bounding box of all Shapefile features", GH_ParamAccess.tree);
            pManager.AddTextParameter("shpSpatRef", "shpSpatRef", "Spatial Reference of the input Shapefile", GH_ParamAccess.tree);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the Shapefile features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddPointParameter("FeaturePoints", "featurePoints", "Point geometry describing each feature", GH_ParamAccess.tree);

            pManager.AddPointParameter("FeaturePointsUser", "featurePointsUser", "Point geometry describing each feature", GH_ParamAccess.tree);
            pManager.AddCurveParameter("shpExtentUser", "shpExtentUser", "Bounding box of all Shapefile features", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {

            ///Gather GHA inputs
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            string shpFileLoc = "";
            DA.GetData<string>("Vector Data Location", ref shpFileLoc);


            bool cropIt = true;
            DA.GetData<Boolean>("Crop file", ref cropIt);

            string userSRStext = "WGS84";
            DA.GetData<string>(2, ref userSRStext);


            ///GDAL setup
            ///Some preliminary testing has been done to read SHP, GeoJSON, OSM, KML, MVT, GML and GDB
            ///It can be spotty with KML, MVT and GML and doesn't throw informative errors.  Likely has to do with getting a valid CRS and 
            ///TODO: resolve errors with reading KML, MVT, GML.

            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();
            OSGeo.OGR.Driver drv = OSGeo.OGR.Ogr.GetDriverByName("ESRI Shapefile");
            OSGeo.OGR.DataSource ds = OSGeo.OGR.Ogr.Open(shpFileLoc, 0);

            if (ds == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                return;
            }

            List<OSGeo.OGR.Layer> layerset = new List<OSGeo.OGR.Layer>();
            List<int> fc = new List<int>();

            for (int iLayer = 0; iLayer < ds.GetLayerCount(); iLayer++)
            {
                OSGeo.OGR.Layer layer = ds.GetLayerByIndex(iLayer);

                if (layer == null)
                {
                    Console.WriteLine("Couldn't fetch advertised layer " + iLayer);
                    System.Environment.Exit(-1);
                }
                else
                {
                    layerset.Add(layer);
                }
            }

            ///Declare trees
            GH_Structure<GH_String> layname = new GH_Structure<GH_String>();
            GH_Structure<GH_Integer> fcs = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Rectangle> recs = new GH_Structure<GH_Rectangle>();
            GH_Structure<GH_String> sRefs = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fnames = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fset = new GH_Structure<GH_String>();
            GH_Structure<GH_Point> gset = new GH_Structure<GH_Point>();

            GH_Structure<GH_Point> gsetUser = new GH_Structure<GH_Point>();
            GH_Structure<GH_Rectangle> recsUser = new GH_Structure<GH_Rectangle>();


            ///Loop through each layer. Layers usually occur in Geodatabase GDB format. SHP usually has only one layer.
            for (int iLayer = 0; iLayer < ds.GetLayerCount(); iLayer++)
            {
                OSGeo.OGR.Layer layer = ds.GetLayerByIndex(iLayer);

                if (layer == null)
                {
                    Console.WriteLine("Couldn't fetch advertised layer " + iLayer);
                    System.Environment.Exit(-1);
                }

                long count = layer.GetFeatureCount(1);
                int featureCount = System.Convert.ToInt32(count);
                fcs.Append(new GH_Integer(featureCount), new GH_Path(iLayer));

                layname.Append(new GH_String(layer.GetName()), new GH_Path(iLayer));


                ///Get the spatial reference of the input vector file and set to WGS84 if not known
                OSGeo.OSR.SpatialReference sourceSRS = new SpatialReference(Osr.SRS_WKT_WGS84);
                string sRef = "";

                if (layer.GetSpatialRef() == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Coordinate Reference System (CRS) is missing.  CRS set automatically set to WGS84. Mapbox to 3857");
                    //sr.ImportFromXML(shpFileLoc);
                    //sr.ImportFromEPSG(3857);
                    //sr.SetWellKnownGeogCS("EPSG:3857");
                    sourceSRS.SetFromUserInput("WGS84"); ///this seems to work where SetWellKnownGeogCS doesn't

                    string pretty = "";
                    sourceSRS.ExportToPrettyWkt(out pretty, 0);
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, pretty);
                    //sr.SetWellKnownGeogCS("WGS84");
                    //sr.SetWellKnownGeogCS("EPSG:3857");
                    sRef = "Coordinate Reference System (CRS) is missing.  CRS set automatically set to WGS84.";
                    

                }
                else
                {
                    if (layer.GetSpatialRef().Validate() != 0)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Coordinate Reference System (CRS) is unknown or unsupported.  CRS set automatically set to WGS84.");
                        sourceSRS.SetWellKnownGeogCS("WGS84");
                        sRef = "Coordinate Reference System (CRS) is unknown or unsupported.  SRS set automatically set to WGS84.";
                    }
                    else
                    {
                        sourceSRS = layer.GetSpatialRef();
                        sourceSRS.ExportToWkt(out sRef);
                        try
                        {
                            int sourceSRSInt = Int16.Parse(sourceSRS.GetAuthorityCode(null));
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Source Coordinate Reference System (CRS) from layer " + layer.GetName() + " is EPSG:" + sourceSRSInt + ".");
                        }
                        catch
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Failed to get Source Coordinate Reference System (CRS) from layer " + layer.GetName() + ".");
                        }
                        }
                        
                }

                sRefs.Append(new GH_String(sRef), new GH_Path(iLayer));


                ///Set transform from input spatial reference to Rhino spatial reference
                ///TODO: look into adding a step for transforming to CRS set in SetCRS 
                OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
                rhinoSRS.SetWellKnownGeogCS("WGS84");

                ///TODO: verify the userSRS is valid
                ///TODO: use this as override of global SetSRS
                OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
                userSRS.SetFromUserInput(userSRStext);

                ///This transform moves and scales the points required in going from userSRS to XYZ and vice versa
                Transform userSRSToModel = Heron.Convert.GetUserSRSToModelTransform(userSRS);
                Transform modelToUserSRS = Heron.Convert.GetModelToUserSRSTransform(userSRS);

                OSGeo.OSR.CoordinateTransformation coordTransformSourceToRhino = new OSGeo.OSR.CoordinateTransformation(sourceSRS, rhinoSRS);
                OSGeo.OSR.CoordinateTransformation coordTransformRhinoToUser = new OSGeo.OSR.CoordinateTransformation(rhinoSRS, userSRS);
                OSGeo.OSR.CoordinateTransformation coordTransformSourceToUser = new OSGeo.OSR.CoordinateTransformation(sourceSRS, userSRS);

                OSGeo.OSR.CoordinateTransformation revTransformUserToRhino = new OSGeo.OSR.CoordinateTransformation(userSRS, rhinoSRS);
                OSGeo.OSR.CoordinateTransformation revTransformRhinoToSource = new OSGeo.OSR.CoordinateTransformation(rhinoSRS, sourceSRS);
                OSGeo.OSR.CoordinateTransformation revTransformUserToSource = new OSGeo.OSR.CoordinateTransformation(userSRS, sourceSRS);

                ///Get OGR envelope of the data in the layer in the sourceSRS
                OSGeo.OGR.Envelope ext = new OSGeo.OGR.Envelope();
                layer.GetExtent(ext, 1);
                Point3d extMinSource = new Point3d();
                Point3d extMaxSource = new Point3d();
                extMinSource.X = ext.MinX;
                extMinSource.Y = ext.MinY;
                extMaxSource.X = ext.MaxX;
                extMaxSource.Y = ext.MaxY;

                ///Get bounding box of data in layer for coordinate transformation in Rhino SRS
                double[] extMinPT = new double[3] { extMinSource.X, extMinSource.Y, extMinSource.Z };
                double[] extMaxPT = new double[3] { extMaxSource.X, extMaxSource.Y, extMaxSource.Z };

                ///Transform corners of extents from Source to Rhino SRS
                coordTransformSourceToRhino.TransformPoint(extMinPT);
                coordTransformSourceToRhino.TransformPoint(extMaxPT);

                ///Get extents in Rhino SRS
                Point3d extPTmin = new Point3d(extMinPT[0], extMinPT[1], extMinPT[2]);
                Point3d extPTmax = new Point3d(extMaxPT[0], extMaxPT[1], extMaxPT[2]);
                Rectangle3d rec = new Rectangle3d(Plane.WorldXY, Heron.Convert.WGSToXYZ(extPTmin), Heron.Convert.WGSToXYZ(extPTmax));
                recs.Append(new GH_Rectangle(rec), new GH_Path(iLayer));

                if (boundary.Count == 0 && cropIt == true)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Define a boundary or set cropIt to False");
                }

                else if (boundary.Count == 0 && cropIt == false)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clipping boundary has not been defined. File extents will be used instead");
                    boundary.Add(rec.ToNurbsCurve());
                }

                ///Get bounding box of data in layer for coordinate transformation in User SRS
                ///TODO: currently the extents are showing odd results that don't seem to be shifting properly
                double[] extMinPTUser = new double[3] { extMinSource.X, extMinSource.Y, extMinSource.Z };
                double[] extMaxPTUser = new double[3] { extMaxSource.X, extMaxSource.Y, extMaxSource.Z };

                ///Transform corners of extents from Source to userSRS
                coordTransformSourceToUser.TransformPoint(extMinPTUser);
                coordTransformSourceToUser.TransformPoint(extMaxPTUser);

                ///Get extents in userSRS
                Point3d extPTminUser = new Point3d(extMinPTUser[0], extMinPTUser[1], extMinPTUser[2]);
                Point3d extPTmaxUser = new Point3d(extMaxPTUser[0], extMaxPTUser[1], extMaxPTUser[2]);
                Rectangle3d recUser = new Rectangle3d(Plane.WorldXY, Heron.Convert.UserSRSToXYZ(extPTminUser, userSRSToModel), Heron.Convert.UserSRSToXYZ(extPTmaxUser, userSRSToModel));
                recsUser.Append(new GH_Rectangle(recUser), new GH_Path(iLayer));


                ///Loop through input boundaries
                for (int i = 0; i < boundary.Count; i++)
                {
                    OSGeo.OGR.FeatureDefn def = layer.GetLayerDefn();

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
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more vector datasource bounds are not valid.");
                        OSGeo.OGR.Feature feat;
                        int m = 0;

                        while ((feat = layer.GetNextFeature()) != null)
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
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more boundaries may be outside the bounds of the vector datasource.");
                    }

                    else 
                    {
                        ///Create bounding box for clipping geometry
                        Point3d min = Heron.Convert.XYZToWGS(boundary[i].GetBoundingBox(true).Min);
                        Point3d max = Heron.Convert.XYZToWGS(boundary[i].GetBoundingBox(true).Max);
                        double[] minpT = new double[3];
                        double[] maxpT = new double[3];

                        minpT[0] = min.X;
                        minpT[1] = min.Y;
                        minpT[2] = min.Z;
                        maxpT[0] = max.X;
                        maxpT[1] = max.Y;
                        maxpT[2] = max.Z;

                        revTransformRhinoToSource.TransformPoint(minpT);
                        revTransformRhinoToSource.TransformPoint(maxpT);

                        ///TODO: allow boundary to be converted to userSRS 
                        //revTransformUserToRhino.TransformPoint(minpT);
                        //revTransformUserToRhino.TransformPoint(maxpT);

                        ///Convert to OGR geometry
                        ///TODO: add conversion from GH geometry to OGR to Convert class
                        OSGeo.OGR.Geometry ebbox = OSGeo.OGR.Geometry.CreateFromWkt("POLYGON((" + minpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + minpT[1] + "))");

                        ///Clip Shapefile
                        ///http://pcjericks.github.io/py-gdalogr-cookbook/vector_layers.html
                        ///TODO: allow for polyline/curve as clipper, not just bounding box
                        OSGeo.OGR.Layer clipped_layer = layer;

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

                            OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();// geom.Clone();
                            OSGeo.OGR.Geometry sub_geomUser;

                            ///reproject geometry to WGS84
                            ///TODO: look into using the SetCRS global variable here
                            geom.Transform(coordTransformSourceToRhino);

                            geomUser.Transform(coordTransformSourceToUser);

                            if (feat.GetGeometryRef() != null)
                            {
                                ///Start get points if open polylines and points
                                for (int gpc = 0; gpc < geom.GetPointCount(); gpc++)
                                { 
                                    ///Loop through geometry points for Rhino SRS
                                    double[] pT = new double[3];
                                    pT[0] = geom.GetX(gpc);
                                    pT[1] = geom.GetY(gpc);
                                    pT[2] = geom.GetZ(gpc);
                                    if (Double.IsNaN(geom.GetZ(gpc)))
                                    {
                                        pT[2] = 0;
                                    }

                                    Point3d pt3D = new Point3d();
                                    pt3D.X = pT[0];
                                    pt3D.Y = pT[1];
                                    pt3D.Z = pT[2];

                                    gset.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3D)), new GH_Path(i, iLayer, m));

                                    ///Loop through geometry points for User SRS
                                    double[] pTUser = new double[3];
                                    pTUser[0] = geomUser.GetX(gpc);
                                    pTUser[1] = geomUser.GetY(gpc);
                                    pTUser[2] = geomUser.GetZ(gpc);
                                    if (Double.IsNaN(geomUser.GetZ(gpc)))
                                    {
                                        pTUser[2] = 0;
                                    }

                                    Point3d pt3DUser = new Point3d();
                                    pt3DUser.X = pTUser[0];
                                    pt3DUser.Y = pTUser[1];
                                    pt3DUser.Z = pTUser[2];

                                    if ((userSRS.IsProjected() == 0) && (userSRS.IsLocal() == 0))
                                    {
                                        gsetUser.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3DUser)), new GH_Path(i, iLayer, m));
                                    }
                                    else
                                    {
                                        gsetUser.Append(new GH_Point(userSRSToModel * pt3DUser), new GH_Path(i, iLayer, m));
                                    }
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
                                    //List<Point3d> geom_list = new List<Point3d>();

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
                                                double[] pT = new double[3];
                                                pT[0] = subsub_geom.GetX(ptnum);
                                                pT[1] = subsub_geom.GetY(ptnum);
                                                pT[2] = subsub_geom.GetZ(ptnum);
 
                                                Point3d pt3D = new Point3d();
                                                pt3D.X = pT[0];
                                                pt3D.Y = pT[1];
                                                pt3D.Z = pT[2];

                                                gset.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3D)), new GH_Path(i, iLayer, m, gi, n));


                                                double[] pTUser = new double[3];
                                                pTUser[0] = subsub_geomUser.GetX(ptnum);
                                                pTUser[1] = subsub_geomUser.GetY(ptnum);
                                                pTUser[2] = subsub_geomUser.GetZ(ptnum);

                                                Point3d pt3DUser = new Point3d();
                                                pt3DUser.X = pTUser[0];
                                                pt3DUser.Y = pTUser[1];
                                                pt3DUser.Z = pTUser[2];

                                                if ((userSRS.IsProjected() == 0) && (userSRS.IsLocal() == 0))
                                                {
                                                    gsetUser.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3DUser)), new GH_Path(i, iLayer, m, gi, n));
                                                }
                                                else
                                                {
                                                    gsetUser.Append(new GH_Point(userSRSToModel * pt3DUser), new GH_Path(i, iLayer, m, gi, n));
                                                }

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
                                            double[] pT = new double[3];
                                            pT[0] = sub_geom.GetX(ptnum);
                                            pT[1] = sub_geom.GetY(ptnum);
                                            pT[2] = sub_geom.GetZ(ptnum);

                                            Point3d pt3D = new Point3d();
                                            pt3D.X = pT[0];
                                            pt3D.Y = pT[1];
                                            pt3D.Z = pT[2];

                                            gset.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3D)), new GH_Path(i, iLayer, m, gi));


                                            double[] pTUser = new double[3];
                                            pTUser[0] = sub_geomUser.GetX(ptnum);
                                            pTUser[1] = sub_geomUser.GetY(ptnum);
                                            pTUser[2] = sub_geomUser.GetZ(ptnum);

                                            Point3d pt3DUser = new Point3d();
                                            pt3DUser.X = pTUser[0];
                                            pt3DUser.Y = pTUser[1];
                                            pt3DUser.Z = pTUser[2];

                                            if ((userSRS.IsProjected() == 0) && (userSRS.IsLocal() == 0))
                                            {
                                                gsetUser.Append(new GH_Point(Heron.Convert.WGSToXYZ(pt3DUser)), new GH_Path(i, iLayer, m, gi));
                                            }
                                            else
                                            {
                                                gsetUser.Append(new GH_Point(userSRSToModel * pt3DUser), new GH_Path(i, iLayer, m, gi));
                                            }
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

                     }

                }//end loop through boundaries

                layer.Dispose();

            }///end loop through layers

            ds.Dispose();

            DA.SetDataTree(0, layname);
            DA.SetDataTree(1, fcs);
            DA.SetDataTree(2, recs);
            DA.SetDataTree(3, sRefs);
            DA.SetDataTree(4, fnames);
            DA.SetDataTree(5, fset);
            DA.SetDataTree(6, gset);

            DA.SetDataTree(7, gsetUser);
            DA.SetDataTree(8, recsUser);

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
