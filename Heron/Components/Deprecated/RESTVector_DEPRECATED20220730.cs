using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Heron
{
    public class RESTVector_DEPRECATED20220730 : HeronComponent
    {
        //Class Constructor
        public RESTVector_DEPRECATED20220730() : base("Get REST Vector", "RESTVector", "Get vector data from ArcGIS REST Services", "GIS REST")
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
            pManager.AddTextParameter("REST URL", "URL", "ArcGIS REST Service website to query", GH_ParamAccess.item);
            pManager.AddTextParameter("User Spatial Reference System", "userSRS", "Custom SRS", GH_ParamAccess.item, "WGS84");
            pManager.AddBooleanParameter("run", "get", "Go ahead to download vector data from the Service", GH_ParamAccess.item, false);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Field Names", "fieldNames", "List of data fields associated with vectors", GH_ParamAccess.list);
            pManager.AddTextParameter("Field Values", "fieldValues", "Data values associated with vectors", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Feature Geometry", "featureGeometry", "Feature geometry from REST vector data.  To output points only, select Points Only from component menu.", GH_ParamAccess.tree);
            pManager.AddTextParameter("RESTQuery", "RESTQuery", "Full text of REST query", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>(0, boundary);

            string URL = string.Empty;
            DA.GetData<string>("REST URL", ref URL);
            if (!URL.EndsWith("/")) { URL = URL + "/"; }

            string userSRStext = string.Empty;
            DA.GetData<string>("User Spatial Reference System", ref userSRStext);

            bool run = false;
            DA.GetData<bool>("run", ref run);

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();

            ///TODO: implement SetCRS here.
            ///Option to set CRS here to user-defined.  Needs a SetCRS global variable.

            OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
            userSRS.SetFromUserInput(userSRStext);
            int userSRSInt = Int16.Parse(userSRS.GetAuthorityCode(null));

            ///Set transform from input spatial reference to Rhino spatial reference
            OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
            rhinoSRS.SetWellKnownGeogCS("WGS84");

            ///This transform moves and scales the points required in going from userSRS to XYZ and vice versa
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);
            Transform modelToUserSRSTransform = Heron.Convert.GetModelToUserSRSTransform(userSRS);

            GH_Structure<GH_String> mapquery = new GH_Structure<GH_String>();

            GH_Structure<GH_Point> gsetUser = new GH_Structure<GH_Point>();
            GH_Structure<IGH_GeometricGoo> gGoo = new GH_Structure<IGH_GeometricGoo>();
            GH_Structure<GH_String> fset = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fieldnames = new GH_Structure<GH_String>();


            for (int i = 0; i < boundary.Count; i++)
            {

                GH_Path cpath = new GH_Path(i);
                BoundingBox bbox = boundary[i].GetBoundingBox(false);
                bbox.Transform(modelToUserSRSTransform);

                string restquery = URL +
                  "query?where=&text=&objectIds=&time=&geometry=" + bbox.Min.X + "%2C" + bbox.Min.Y + "%2C" + bbox.Max.X + "%2C" + bbox.Max.Y +
                  "&geometryType=esriGeometryEnvelope&inSR=" + userSRSInt +
                  "&spatialRel=esriSpatialRelIntersects" +
                  "&relationParam=&outFields=*" +
                  "&returnGeometry=true" +
                  "&maxAllowableOffset=" +
                  "&geometryPrecision=" +
                  "&outSR=" + userSRSInt +
                  "&returnIdsOnly=false" +
                  "&returnCountOnly=false" +
                  "&orderByFields=" +
                  "&groupByFieldsForStatistics=&outStatistics=" +
                  "&returnZ=true" +
                  "&returnM=false" +
                  "&gdbVersion=" +
                  "&returnDistinctValues=false" +
                  "&f=json";

                mapquery.Append(new GH_String(restquery), cpath);

                if (run)
                {
                    //string result = Heron.Convert.HttpToJson(restquery);

                    OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open("ESRIJSON:" + restquery, 0);

                    if (dataSource == null)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                    }


                    ///Loop through each layer. Likely not any layers in a REST service
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

                        OSGeo.OGR.FeatureDefn def = ogrLayer.GetLayerDefn();

                        ///Get the field names
                        for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
                        {
                            ///TODO: Look into GetAlternativeNameRef() for field aliases (more readable) available in GDAL 3.2
                            OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iAttr);
                            fieldnames.Append(new GH_String(fdef.GetNameRef()), new GH_Path(i, iLayer));
                        }

                        ///Loop through geometry
                        OSGeo.OGR.Feature feat;
                        def = ogrLayer.GetLayerDefn();

                        int m = 0;
                        while ((feat = ogrLayer.GetNextFeature()) != null)
                        {

                            OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();
                            OSGeo.OGR.Geometry sub_geomUser;

                            ///reproject geometry to WGS84 and userSRS
                            ///TODO: look into using the SetCRS global variable here
                            if (geomUser.GetSpatialReference() == null) { geomUser.AssignSpatialReference(userSRS); }

                            geomUser.TransformTo(userSRS);

                            if (feat.GetGeometryRef() != null)
                            {
                                if (!pointsOnly)
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
                                }

                                else
                                {
                                    ///Start get points if open polylines and points
                                    for (int gpc = 0; gpc < geomUser.GetPointCount(); gpc++)
                                    {
                                        ///Loop through geometry points for User SRS
                                        double[] ogrPtUser = new double[3];
                                        geomUser.GetPoint(gpc, ogrPtUser);
                                        Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                        pt3DUser.Transform(userSRSToModelTransform);

                                        gGoo.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m));
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
                                    for (int gi = 0; gi < geomUser.GetGeometryCount(); gi++)
                                    {
                                        sub_geomUser = geomUser.GetGeometryRef(gi);
                                        OSGeo.OGR.Geometry subsub_geomUser;

                                        if (sub_geomUser.GetGeometryCount() > 0)
                                        {
                                            for (int n = 0; n < sub_geomUser.GetGeometryCount(); n++)
                                            {
                                                subsub_geomUser = sub_geomUser.GetGeometryRef(n);

                                                for (int ptnum = 0; ptnum < subsub_geomUser.GetPointCount(); ptnum++)
                                                {
                                                    ///Loop through geometry points for User SRS
                                                    double[] ogrPtUser = new double[3];
                                                    subsub_geomUser.GetPoint(ptnum, ogrPtUser);
                                                    Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                                    pt3DUser.Transform(userSRSToModelTransform);

                                                    gGoo.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m, gi, n));
                                                    ///End loop through geometry points
                                                }
                                                subsub_geomUser.Dispose();
                                            }
                                        }

                                        else
                                        {
                                            for (int ptnum = 0; ptnum < sub_geomUser.GetPointCount(); ptnum++)
                                            {
                                                ///Loop through geometry points for User SRS
                                                double[] ogrPtUser = new double[3];
                                                sub_geomUser.GetPoint(ptnum, ogrPtUser);
                                                Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                                pt3DUser.Transform(userSRSToModelTransform);

                                                gGoo.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m, gi));

                                                ///End loop through geometry points
                                            }
                                        }

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
                                    }///End closed polygons and multipolygons
                                }///End points only
                            }
                            m++;
                            geomUser.Dispose();
                            feat.Dispose();
                        }///end while loop through features

                    }
                    dataSource.Dispose();
                }
            }

            ///Not the most elegant way of setting outputs only on run
            if (run)
            {
                DA.SetDataList(0, fieldnames.get_Branch(0));
                DA.SetDataTree(1, fset);
                DA.SetDataTree(2, gGoo);
            }
            DA.SetDataTree(3, mapquery);

        }



        private bool pointsOnly = false;
        public bool PointsOnly
        {
            get { return pointsOnly; }
            set
            {
                pointsOnly = value;
                if ((!pointsOnly))
                {
                    Message = string.Empty;
                }
                else
                {
                    Message = "Points Only";
                }
            }
        }
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem item = Menu_AppendItem(menu, "Points Only", Menu_PointsOnlyChecked, true, PointsOnly);
            item.ToolTipText = "Output only the points which make up the feature geometry.  If unchecked, the feature geometry will be output.";
        }

        private void Menu_PointsOnlyChecked(object sender, EventArgs e)
        {
            RecordUndoEvent("PointsOnly");
            PointsOnly = !PointsOnly;
            ExpireSolution(true);
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

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.vector;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{9324AECD-F345-4507-9C04-34D017378976}"); }
        }
    }
}
