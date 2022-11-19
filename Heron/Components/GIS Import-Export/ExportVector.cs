using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using GH_IO;
using GH_IO.Serialization;

using Rhino;
using Rhino.Geometry;

using OSGeo.OSR;
using OSGeo.OGR;
using OSGeo.GDAL;

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using System.Linq;

namespace Heron
{
    public class ExportVector : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the ExportVector class.
        /// </summary>
        public ExportVector()
          : base("Export Vector", "ExportVector",
              "Export Grasshopper geometry to Shapefile, GeoJSON, KML and GML file formats in the WGS84 (EPSG:4326) spatial reference system. " +
                "Inputs should adhere to the Simple Features Data Model where each feature, containing point(s), polyline(s), mesh(es) or a combination of these " +
                "has data values for each field. Note exporting multiple goemetry types on the same branch (a geometry collection) may cause a shapefile export to fail.",
              "GIS Import | Export")
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
            pManager.AddTextParameter("Vector Data Filename", "filename", "File name to give the vector data ouput.  Do not include file extension.", GH_ParamAccess.item);
            pManager.AddTextParameter("Vector Data Folder", "folder", "Folder location to save the vector data ouput", GH_ParamAccess.item);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the vector data features.  " +
                "For KML exports, consider adding an 'altitudeMode' field with possible values of 'relativeToGround' or 'absolute' and " +
                "an 'OGR_STYLE' field to control color and transparency via hexidicemal values (see ColorToHex component).", GH_ParamAccess.list);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry contained in the feature.  Geometry can be point(s), polyline(s), mesh(es) or a combination of these.", GH_ParamAccess.tree);
            pManager.AddBooleanParameter("Export", "export", "Go ahead and export feature geometry with associated fields and values to the specified location.  Existing files will be overwritten.", GH_ParamAccess.item, false);
            pManager[3].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Vector Data Location", "filePath", "File path location of saved vector data", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///TODO:    fix crash with "multi" types in a branch for shapefiles, can't create lines in a points layer 
            ///         fix mesh/polygon face creation issue on sphere, dropping faces or flipped faces
            ///         fix swtich case for shapfiles so that points and multipoints (eg) are written to the same file. don't use switch anymore, us ifs
            ///         fix sql statements, they don't seem to have an effect.  Need these to work for pulling apart geometry collections.

            ///Gather GHA inputs
            string filename = string.Empty;
            DA.GetData<string>("Vector Data Filename", ref filename);

            string folder = string.Empty;
            DA.GetData<string>("Vector Data Folder", ref folder);
            folder = Path.GetFullPath(folder);
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString())) { folder += Path.DirectorySeparatorChar; }

            string shpPath = folder + filename + drvExtension;
            ///for more than one geometry type, a list of files for shapefile output needs to be established
            List<string> shpPathList = new List<string>();
            List<string> shpTypeList = new List<string>();

            List<string> fields = new List<string>();
            DA.GetDataList<string>("Fields", fields);

            GH_Structure<GH_String> values = new GH_Structure<GH_String>();
            DA.GetDataTree<GH_String>("Values", out values);

            GH_Structure<IGH_GeometricGoo> gGoo = new GH_Structure<IGH_GeometricGoo>();
            DA.GetDataTree<IGH_GeometricGoo>("Feature Geometry", out gGoo);

            bool export = false;
            DA.GetData<bool>("Export", ref export);

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.GDAL.Gdal.SetConfigOption("OGR_SKIP", "KML");

            string driverType = drvType;
            //OSGeo.OGR.Driver drv = Ogr.GetDriverByName("LIBKML");// driverType);
            OSGeo.OGR.Driver drv = Ogr.GetDriverByName("GeoJSON");



            if (export == true)
            {
                ///File setup for save
                FileInfo file = new FileInfo(folder);
                file.Directory.Create();

                if (File.Exists(shpPath))
                {
                    drv.DeleteDataSource(shpPath);
                }

                ///Create virtual datasource to be converted later
                ///Using geojson as a flexiblle base file type which can be converted later with ogr2ogr
                DataSource ds = drv.CreateDataSource("/vsimem/out.geojson", null);

                ///Get HeronSRS
                OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
                heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
                int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
                Message = "EPSG:" + heronSRSInt;

                ///Use WGS84 spatial reference
                OSGeo.OSR.SpatialReference dst = new OSGeo.OSR.SpatialReference("");
                dst.SetFromUserInput(HeronSRS.Instance.SRS);
                
                ///Apply EAP to HeronSRS
                Transform heronToUserSRSTransform = new Transform(1);
                Transform userSRSToHeronTransform = new Transform(1);
                Transform transform = new Transform(1);
                ///Allow no transform for a straight export of coordinates if EAP is not set
                if (RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthLocationIsSet())
                {
                    ///Apply EAP to HeronSRS
                    heronToUserSRSTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
                    userSRSToHeronTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);
                    transform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);
                }


                ///Use OGR catch-all for geometry types
                var gtype = wkbGeometryType.wkbGeometryCollection;

                ///Create layer
                //string[] layerOptions = new string[] { "LIBKML_USE_SCHEMADATA=NO", "LIBKML_USE_SIMPLEFIELD=NO", "LIBKML_ALTITUDEMODE_FIELD=relativeToGround" };
                //string[] layerOptions = new string[] { "LIBKML_STRICT_COMPLIANCE=FALSE" };
                OSGeo.OGR.Layer layer = ds.CreateLayer(filename, dst, gtype, null);
                FeatureDefn def = layer.GetLayerDefn();

                ///Add fields to layer
                for (int f = 0; f < fields.Count; f++)
                {
                    OSGeo.OGR.FieldDefn fname = new OSGeo.OGR.FieldDefn(fields[f], OSGeo.OGR.FieldType.OFTString);
                    layer.CreateField(fname, f);
                }

                ///Specific fields for LIBKML for use in Google Earth
                ///See LIBMKL driver for more info https://gdal.org/drivers/vector/libkml.html
                if (drvType == "LIBKML")
                {
                    OSGeo.OGR.FieldDefn kmlFieldAltitudeMode = new OSGeo.OGR.FieldDefn("altitudeMode", OSGeo.OGR.FieldType.OFTString);
                    layer.CreateField(kmlFieldAltitudeMode, fields.Count());
                    //OSGeo.OGR.FieldDefn kmlFieldExtrude = new OSGeo.OGR.FieldDefn("tessellate", OSGeo.OGR.FieldType.OFTInteger);
                    //layer.CreateField(kmlFieldExtrude, fields.Count()+1);
                }


                for (int a = 0; a < gGoo.Branches.Count; a++)
                {
                    ///create feature
                    OSGeo.OGR.Feature feature = new OSGeo.OGR.Feature(def);

                    ///Set LIBKML specific fields for use in Google Earth, defaulting to 'relativeToGround'.  Consider setting to 'absolute'.
                    if (drvType == "LIBKML")
                    {
                        feature.SetField("altitudeMode", "relativeToGround");
                        //feature.SetField("altitudeMode", "absolute");
                        //feature.SetField("tessellate", 0);
                    }

                    ///TODO: Build style table
                    OSGeo.OGR.StyleTable styleTable = new StyleTable();
                    //feature.SetStyleString("BRUSH(fc:#0000FF);PEN(c:#000000)");

                    ///Get geometry type(s) in branch
                    var geomList = gGoo.Branches[a];
                    string geomType = string.Empty;
                    List<string> geomTypeList = geomList.Select(o => o.TypeName).ToList();
                    ///Test if geometry in the branch is of the same type.  
                    ///If there is more than one element of a type, tag as multi, if there is more than one type, tag as mixed
                    if (geomTypeList.Count == 1)
                    {
                        geomType = geomTypeList.First();
                    }
                    else if (geomTypeList.Count > 1 && geomTypeList.All(gt => gt == geomTypeList.First()))
                    {
                        geomType = "Multi" + geomTypeList.First();
                    }

                    else { geomType = "Mixed"; }

                    ///For testing
                    //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, geomType);

                    ///Add geomtery to feature
                    ///Create containers for translating from GH Goo
                    Point3d pt = new Point3d();
                    List<Point3d> pts = new List<Point3d>();

                    Curve crv = null;
                    List<Curve> crvs = new List<Curve>();

                    Mesh mesh = new Mesh();
                    Mesh multiMesh = new Mesh();

                    switch (geomType)
                    {
                        case "Point":
                            geomList.First().CastTo<Point3d>(out pt);
                            feature.SetGeometry(Heron.Convert.Point3dToOgrPoint(pt, transform));
                            if (!shpTypeList.Contains("POINT")) { shpTypeList.Add("POINT"); }
                            break;

                        case "MultiPoint":
                            foreach (var point in geomList)
                            {
                                point.CastTo<Point3d>(out pt);
                                pts.Add(pt);
                            }
                            feature.SetGeometry(Heron.Convert.Point3dsToOgrMultiPoint(pts, transform));
                            if (!shpTypeList.Contains("MULTIPOINT")) { shpTypeList.Add("MULTIPOINT"); }
                            break;

                        case "Curve":
                            geomList.First().CastTo<Curve>(out crv);
                            feature.SetGeometry(Heron.Convert.CurveToOgrLinestring(crv, transform));
                            if (!shpTypeList.Contains("LINESTRING")) { shpTypeList.Add("LINESTRING"); }
                            break;

                        case "MultiCurve":
                            foreach (var curve in geomList)
                            {
                                curve.CastTo<Curve>(out crv);
                                crvs.Add(crv);
                            }
                            feature.SetGeometry(Heron.Convert.CurvesToOgrMultiLinestring(crvs, transform));
                            if (!shpTypeList.Contains("MULTILINESTRING")) { shpTypeList.Add("MULTILINESTRING"); }
                            break;

                        case "Mesh":
                            geomList.First().CastTo<Mesh>(out mesh);
                            feature.SetGeometry(Heron.Convert.MeshToMultiPolygon(mesh, transform));
                            if (!shpTypeList.Contains("MULTIPOLYGON")) { shpTypeList.Add("MULTIPOLYGON"); }
                            break;

                        case "MultiMesh":
                            foreach (var m in geomList)
                            {
                                Mesh meshPart = new Mesh();
                                m.CastTo<Mesh>(out meshPart);
                                multiMesh.Append(meshPart);
                            }
                            feature.SetGeometry(Heron.Convert.MeshToMultiPolygon(multiMesh, transform));
                            if (!shpTypeList.Contains("MULTIPOLYGON")) { shpTypeList.Add("MULTIPOLYGON"); }
                            break;

                        case "Mixed":
                            OSGeo.OGR.Geometry geoCollection = new OSGeo.OGR.Geometry(wkbGeometryType.wkbGeometryCollection);
                            for (int gInt = 0; gInt < geomList.Count; gInt++)
                            {
                                string geomTypeMixed = geomTypeList[gInt];
                                switch (geomTypeMixed)
                                {
                                    case "Point":
                                        geomList[gInt].CastTo<Point3d>(out pt);
                                        geoCollection.AddGeometry(Heron.Convert.Point3dToOgrPoint(pt, transform));
                                        break;

                                    case "Curve":
                                        geomList[gInt].CastTo<Curve>(out crv);
                                        geoCollection.AddGeometry(Heron.Convert.CurveToOgrLinestring(crv, transform));
                                        break;

                                    case "Mesh":
                                        geomList[gInt].CastTo<Mesh>(out mesh);
                                        geoCollection.AddGeometry(Ogr.ForceToMultiPolygon(Heron.Convert.MeshToMultiPolygon(mesh, transform)));
                                        break;

                                    default:
                                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Not able to export " + geomType + " geometry at branch " + gGoo.get_Path(a).ToString() +
                                            ". Geometry must be a Point, Curve or Mesh.");
                                        break;
                                }

                            }
                            feature.SetGeometry(geoCollection);
                            if (!shpTypeList.Contains("GEOMETRYCOLLECTION")) { shpTypeList.Add("GEOMETRYCOLLECTION"); }
                            break;

                        default:
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Not able to export " + geomType + " geometry at branch " + gGoo.get_Path(a).ToString() +
                                ". Geometry must be a Point, Curve or Mesh.");
                            break;
                    }



                    ///Give the feature a unique ID
                    feature.SetFID(a);

                    ///Add values to fields
                    GH_Path path = gGoo.get_Path(a);

                    for (int vInt = 0; vInt < fields.Count; vInt++)
                    {
                        string val = string.Empty;
                        if (values.get_Branch(path) != null)
                        {
                            val = values.get_DataItem(path, vInt).ToString();
                        }
                        feature.SetField(fields[vInt], val);
                    }

                    ///Save feature to layer
                    layer.CreateFeature(feature);

                    ///Cleanup
                    feature.Dispose();
                }

                layer.Dispose();
                ds.Dispose();
                drv.Dispose();

                ///Convert in memory dataset to file using ogr2ogr
                ///For KML set 'altitudeMode' to 'relativeToGround' or 'absolute'

                ///Set base options for all export types
                if (drvType == "KML") { drvType = "LIBKML"; }
                List<string> ogr2ogrOptions = new List<string> {
                    "-overwrite",
                    "-f", drvType,
                    "-dim", "XYZ",
                    "-skipfailures",

                    //"-lco", "LIBKML_STRICT_COMPLIANCE=FALSE",
                    //"-lco", "AltitudeMode=absolute",

                    //"-dsco", "SHAPE_REWIND_ON_WRITE=YES"
                    };

                Dataset src = Gdal.OpenEx("/vsimem/out.geojson", 0, null, null, null);

                if (drvType != "ESRI Shapefile")
                {
                    Dataset destDataset = Gdal.wrapper_GDALVectorTranslateDestName(shpPath, src, new GDALVectorTranslateOptions(ogr2ogrOptions.ToArray()), null, null);
                    destDataset.Dispose();
                    shpPathList.Add(shpPath);
                }

                ///Export multiple layers for shapefile
                ///https://trac.osgeo.org/gdal/wiki/FAQVector#HowdoItranslateamixedgeometryfiletoshapefileformat
                else
                {

                    ///
                    if (shpTypeList.Count <= 1 && shpTypeList.First() != "GEOMETRYCOLLECTION")
                    {
                        if (shpTypeList.First() == "POLYGON" || shpTypeList.First() == "MULTIPOLYGON")
                        {
                            ogr2ogrOptions.AddRange(new List<string> { "-lco", "SHPT=MULTIPATCH" });
                        }
                        Dataset destDataset = Gdal.wrapper_GDALVectorTranslateDestName(shpPath, src, new GDALVectorTranslateOptions(ogr2ogrOptions.ToArray()), null, null);
                        destDataset.Dispose();
                        shpPathList.Add(shpPath);
                    }

                    else
                    {
                        ///Add -explodecollections for mixed geometries in a branch
                        ///"-where" statement is not necessary, but could speed up big datasets

                        string shpFileName = string.Empty;
                        List<string> ogr2ogrShpOptions = new List<string>();

                        if (shpTypeList.Contains("POINT") || shpTypeList.Contains("MULTIPOINT"))
                        {
                            shpFileName = folder + filename + "_points.shp";
                            shpPathList.Add(shpFileName);
                            List<string> ogr2ogrShpOptionsPts = new List<string> {
                                "-overwrite",
                                "-f", drvType,
                                "-dim", "XYZ",
                                "-skipfailures"
                                };
                            //ogr2ogrShpOptionsPts.AddRange(new List<string> { "-where", "ogr_geometry=POINT", "-where", "ogr_geometry=MULTIPOINT", "-lco", "SHPT=MULTIPOINTZ", "-nlt", "PROMOTE_TO_MULTI"});
                            ogr2ogrShpOptionsPts.AddRange(new List<string> { "-sql", "SELECT * FROM " + filename + " WHERE OGR_GEOMETRY='MULTIPOINT' OR OGR_GEOMETRY='POINT'", "-lco", "SHPT=MULTIPOINTZ", "-nlt", "PROMOTE_TO_MULTI" });
                            //ogr2ogrShpOptionsPts.AddRange(new List<string> { "-dialect", "sqlite", "-sql", "select * from " + filename + " where GeometryType(geometry) in ('POINT','MULTIPOINT')", "-lco", "SHPT=MULTIPOINTZ", "-nlt", "PROMOTE_TO_MULTI" });

                            Dataset destDatasetPoint = Gdal.wrapper_GDALVectorTranslateDestName(shpFileName, src, new GDALVectorTranslateOptions(ogr2ogrShpOptionsPts.ToArray()), null, null);
                            destDatasetPoint.Dispose();
                        }

                        if (shpTypeList.Contains("LINESTRING") || shpTypeList.Contains("MULTILINESTRING"))
                        {
                            shpFileName = folder + filename + "_lines.shp";
                            shpPathList.Add(shpFileName);
                            List<string> ogr2ogrShpOptionsLines = new List<string> {
                                "-overwrite",
                                "-f", drvType,
                                "-dim", "XYZ",
                                "-skipfailures"
                                };
                            //ogr2ogrShpOptionsLines.AddRange(new List<string> { "-where", "ogr_geometry=LINESTRING25D", "-where", "ogr_geometry=MULTILINESTRING25D", "-lco", "SHPT=ARCZ", "-nlt", "PROMOTE_TO_MULTI" });
                            ogr2ogrShpOptionsLines.AddRange(new List<string> { "-sql", "SELECT * FROM " + filename + " WHERE OGR_GEOMETRY='LINESTRING' OR OGR_GEOMETRY='MULTILINESTRING'", "-lco", "SHPT=ARCZ", "-nlt", "PROMOTE_TO_MULTI" });
                            //ogr2ogrShpOptionsLines.AddRange(new List<string> { "-dialect", "sqlite", "-sql", "select * from " + filename + " where GeometryType(geometry) in ('LINESTRING','MULTILINESTRING')", "-lco", "SHPT=ARCZ", "-nlt", "PROMOTE_TO_MULTI" });

                            Dataset destDatasetLinestring = Gdal.wrapper_GDALVectorTranslateDestName(shpFileName, src, new GDALVectorTranslateOptions(ogr2ogrShpOptionsLines.ToArray()), null, null);
                            destDatasetLinestring.Dispose();
                        }

                        if (shpTypeList.Contains("POLYGON") || shpTypeList.Contains("MULTIPOLYGON"))
                        {
                            shpFileName = folder + filename + "_polygons.shp";
                            shpPathList.Add(shpFileName);
                            List<string> ogr2ogrShpOptionsPolygons = new List<string> {
                                "-overwrite",
                                "-f", drvType,
                                "-dim", "XYZ",
                                "-skipfailures",
                                "-dsco", "SHAPE_REWIND_ON_WRITE=NO"
                                };
                            //ogr2ogrShpOptionsPolygons.AddRange(new List<string> { "-where", "ogr_geometry=POLYGON", "-where", "ogr_geometry=MULTIPOLYGON", "-lco", "SHPT=POLYGONZ", "-nlt", "PROMOTE_TO_MULTI" });
                            ogr2ogrShpOptionsPolygons.AddRange(new List<string> { "-sql", "SELECT * FROM " + filename + " WHERE OGR_GEOMETRY='MULTIPOLYGON25D' OR OGR_GEOMETRY='POLYGON25D'", "-lco", "SHPT=POLYGONZ", "-nlt", "PROMOTE_TO_MULTI" });
                            Dataset destDatasetPolygon = Gdal.wrapper_GDALVectorTranslateDestName(shpFileName, src, new GDALVectorTranslateOptions(ogr2ogrShpOptionsPolygons.ToArray()), null, null);
                            destDatasetPolygon.Dispose();
                        }

                        ///Not working properly when multiple geometry types are part of the same branch for SHP export.
                        if (shpTypeList.Contains("GEOMETRYCOLLECTION"))
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "One or more branches contain a mix of geometry types.");
                            ///export points
                            shpFileName = folder + filename + "_gc-points.shp";
                            shpPathList.Add(shpFileName);
                            List<string> ogr2ogrShpOptionsGCPts = new List<string> {
                                "-overwrite",
                                "-f", drvType,
                                "-dim", "XYZ",
                                "-skipfailures"
                                };
                            ogr2ogrShpOptionsGCPts.AddRange(new List<string> { "-explodecollections", "-where", "ogr_geometry=GEOMETRYCOLLECTION", "-lco", "SHPT=MULTIPOINTZ", "-nlt", "PROMOTE_TO_MULTI" });
                            //ogr2ogrShpOptionsGCPts.AddRange(new List<string> { "-explodecollections", "-sql", "SELECT * FROM " + filename + " WHERE OGR_GEOMETRY='MULTIPOINT' OR OGR_GEOMETRY='POINT'", "-lco", "SHPT=MULTIPOINTZ", "-nlt", "PROMOTE_TO_MULTI" });

                            Dataset destDatasetGCPoints = Gdal.wrapper_GDALVectorTranslateDestName(shpFileName, src, new GDALVectorTranslateOptions(ogr2ogrShpOptionsGCPts.ToArray()), null, null);
                            destDatasetGCPoints.Dispose();

                            ///export lines
                            shpFileName = folder + filename + "_gc-lines.shp";
                            shpPathList.Add(shpFileName);
                            List<string> ogr2ogrShpOptionsGCLines = new List<string> {
                                "-overwrite",
                                "-f", drvType,
                                "-dim", "XYZ",
                                "-skipfailures"
                                };
                            ogr2ogrShpOptionsGCLines.AddRange(new List<string> { "-explodecollections", "-where", "ogr_geometry=GEOMETRYCOLLECTION", "-lco", "SHPT=ARCZ", "-nlt", "PROMOTE_TO_MULTI" });
                            //ogr2ogrShpOptionsGCLines.AddRange(new List<string> { "-explodecollections", "-sql", "SELECT * FROM " + filename + " WHERE OGR_GEOMETRY='MULTILINESTRING25D' OR OGR_GEOMETRY='LINESTRING25D'", "-lco", "SHPT=ARCZ", "-nlt", "PROMOTE_TO_MULTI" });
                            Dataset destDatasetGCLines = Gdal.wrapper_GDALVectorTranslateDestName(shpFileName, src, new GDALVectorTranslateOptions(ogr2ogrShpOptionsGCLines.ToArray()), null, null);
                            destDatasetGCLines.Dispose();

                            ///export meshes
                            shpFileName = folder + filename + "_gc-polygons.shp";
                            shpPathList.Add(shpFileName);
                            List<string> ogr2ogrShpOptionsGCPolygons = new List<string> {
                                "-overwrite",
                                "-f", drvType,
                                "-dim", "XYZ",
                                "-skipfailures",
                                "-dsco", "SHAPE_REWIND_ON_WRITE=NO"
                                };
                            ogr2ogrShpOptionsGCPolygons.AddRange(new List<string> { "-explodecollections", "-where", "ogr_geometry=GEOMETRYCOLLECTION", "-lco", "SHPT=POLYGONZ", "-nlt", "PROMOTE_TO_MULTI" });
                            //ogr2ogrShpOptionsGCPolygons.AddRange(new List<string> { "-explodecollections", "-sql", "SELECT * FROM " + filename + " WHERE OGR_GEOMETRY='MULTIPOLYGON' OR OGR_GEOMETRY='POLYGON'", "-lco", "SHPT=POLYGONZ", "-nlt", "PROMOTE_TO_MULTI" });
                            Dataset destDatasetGCPolygons = Gdal.wrapper_GDALVectorTranslateDestName(shpFileName, src, new GDALVectorTranslateOptions(ogr2ogrShpOptionsGCPolygons.ToArray()), null, null);
                            destDatasetGCPolygons.Dispose();

                        }

                    }

                }

                ///Clean up
                Gdal.Unlink("/vsimem/out.geojson");

            }

            DA.SetDataList(0, shpPathList);
        }




        ////////////////////////////
        //Menu Items

        private bool IsServiceSelected(string serviceString)
        {
            return serviceString.Equals(drvType);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            if (drvDict.Count == 0)
            {
                drvDict = new Dictionary<string, string>()
                {
                    {"ESRI Shapefile", ".shp"},
                    {"GeoJSON", ".geojson" },
                    {"KML", ".kml" },
                    {"GML", ".gml" }
                };

            }

            ToolStripMenuItem root = new ToolStripMenuItem("Pick file type for export");

            foreach (var fileType in drvDict.Keys)
            {
                ToolStripMenuItem serviceName = new ToolStripMenuItem(fileType);
                serviceName.Tag = fileType;
                serviceName.Checked = IsServiceSelected(fileType);
                //serviceName.ToolTipText = service["description"].ToString();
                serviceName.Click += ServiceItemOnClick;

                root.DropDownItems.Add(serviceName);
            }

            menu.Items.Add(root);

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

            RecordUndoEvent("DriverType");
            RecordUndoEvent("DriverExtension");

            drvType = code;
            drvExtension = drvDict[drvType];
            Message = drvType;

            ExpireSolution(true);
        }


        ///////////////////////////

        ///////////////////////////
        //Sticky Parameters

        private Dictionary<string, string> drvDict = new Dictionary<string, string>()
            {
                {"ESRI Shapefile", ".shp"},
                {"GeoJSON", ".geojson" },
                {"KML", ".kml" },
                {"GML", ".gml" }
            };

        private string drvType = "ESRI Shapefile";
        private string drvExtension = ".shp";

        public Dictionary<string, string> DriverDictionary
        {
            get { return drvDict; }
            set
            {
                drvDict = value;
            }
        }

        public string DriverType
        {
            get { return drvType; }
            set
            {
                drvType = value;
                Message = drvType;
            }
        }

        public string DriverExtension
        {
            get { return drvExtension; }
            set { drvExtension = value; }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("DriverType", DriverType);
            writer.SetString("DriverExtension", DriverExtension);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            DriverType = reader.GetString("DriverType");
            DriverExtension = reader.GetString("DriverExtension");
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
            get { return new Guid("7bddff1a-8a4b-4fb8-bd97-0800bcdb6aed"); }
        }
    }
}