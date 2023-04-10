using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using OSGeo.OGR;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Heron.Components.GIS_Tools
{
    public class GdalBuffer : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MyComponent1 class.
        /// </summary>
        public GdalBuffer()
          : base("Gdal Buffer", "GdalBuffer",
              "Create a buffer around a geometry using Gdal's buffer function.  GDAL buffering only works in 2D, so the results will be on the XY plane.",
              "GIS Tools")
        {
        }

        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.tertiary; }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter("Feature Geometry", "FG", "Geometry contained in the feature.  Geometry can be point(s), polyline(s), mesh(es) or a combination of these.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Buffer Distance", "B", "Buffer distance.  A negative value can be used with meshes and closed polylines (ie polygons).", GH_ParamAccess.tree);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Buffer Geometry", "BG", "Buffered geometry.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {

            GH_Structure<IGH_GeometricGoo> gGoo = new GH_Structure<IGH_GeometricGoo>();
            DA.GetDataTree<IGH_GeometricGoo>("Feature Geometry", out gGoo);

            GH_Structure<GH_Number> bufferInt = new GH_Structure<GH_Number>();
            DA.GetDataTree<GH_Number>("Buffer Distance", out bufferInt);

            GH_Structure<IGH_GeometricGoo> gGooBuffered = new GH_Structure<IGH_GeometricGoo>();

            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();

            ///Use WGS84 spatial reference
            OSGeo.OSR.SpatialReference dst = new OSGeo.OSR.SpatialReference("");
            dst.SetWellKnownGeogCS("WGS84");
            Transform transform = new Transform(1);
            Transform revTransform = new Transform(1);

            ///Create virtual datasource to be converted later
            ///Using geojson as a flexiblle base file type which can be converted later with ogr2ogr
            OSGeo.OGR.Driver drv = Ogr.GetDriverByName("GeoJSON");
            DataSource ds = drv.CreateDataSource("/vsimem/out.geojson", null);

            ///Use OGR catch-all for geometry types
            var gtype = wkbGeometryType.wkbGeometryCollection;

            ///Create layer
            OSGeo.OGR.Layer layer = ds.CreateLayer("temp", dst, gtype, null);
            FeatureDefn def = layer.GetLayerDefn();

            var branchPaths = gGoo.Paths;

            for (int a = 0; a < gGoo.Branches.Count; a++)
            {
                ///create feature
                OSGeo.OGR.Feature feature = new OSGeo.OGR.Feature(def);

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

                //var buffList = bufferInt.Branches[a];
                var buffList = new List<GH_Number>();
                //var path = new GH_Path(a);
                var path = branchPaths[a];
                if (path.Valid) 
                { 
                    buffList = (List<GH_Number>) bufferInt.get_Branch(path); 
                }
                else { buffList = bufferInt.Branches[0]; }
                int buffIndex = 0;


                double buffDist = 0;
                GH_Convert.ToDouble(buffList[buffIndex], out buffDist, GH_Conversion.Primary);

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

                int quadsecs = 10;

                switch (geomType)
                {
                    case "Point":
                        geomList.First().CastTo<Point3d>(out pt);
                        var bufferPt = Heron.Convert.Point3dToOgrPoint(pt, transform).Buffer(buffDist, quadsecs);
                        gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferPt, revTransform), new GH_Path(a));
                        break;

                    case "MultiPoint":
                        foreach (var point in geomList)
                        {
                            point.CastTo<Point3d>(out pt);
                            pts.Add(pt);
                        }

                        if (pts.Count == buffList.Count)
                        {
                            OSGeo.OGR.Geometry bufferedSubPts = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiPolygon25D);
                            for (int ptIndex = 0; ptIndex < crvs.Count; ptIndex++)
                            {
                                bufferedSubPts.AddGeometry(Heron.Convert.Point3dToOgrPoint(pts[ptIndex], transform).Buffer(buffList[ptIndex].Value, quadsecs));
                            }
                            gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferedSubPts.Buffer(0, quadsecs), revTransform), new GH_Path(a));
                        }
                        else
                        {
                            var bufferPts = Heron.Convert.Point3dsToOgrMultiPoint(pts, transform).Buffer(buffDist, quadsecs);
                            gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferPts, revTransform), new GH_Path(a));
                        }

                        break;

                    case "Curve":
                        geomList.First().CastTo<Curve>(out crv);
                        var bufferCrv = Heron.Convert.CurveToOgrLinestring(crv, transform).Buffer(buffDist, quadsecs);
                        gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferCrv, revTransform), new GH_Path(a));
                        break;

                    case "MultiCurve":
                        bool allClosed = true;
                        foreach (var curve in geomList)
                        {
                            curve.CastTo<Curve>(out crv);
                            if (!crv.IsClosed) { allClosed = false; }
                            crvs.Add(crv);
                        }
                        if (allClosed)
                        {
                            if (crvs.Count == buffList.Count)
                            {
                                OSGeo.OGR.Geometry bufferedSubCrvs = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiPolygon25D);
                                for (int crvIndex = 0; crvIndex < crvs.Count; crvIndex++)
                                {
                                    bufferedSubCrvs.AddGeometry(Heron.Convert.CurveToOgrPolygon(crvs[crvIndex], transform).Buffer(buffList[crvIndex].Value, quadsecs));
                                }
                                gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferedSubCrvs.Buffer(0, quadsecs), revTransform), new GH_Path(a));
                            }
                            else
                            {
                                var bufferCrvs = Heron.Convert.CurvesToOgrPolygon(crvs, transform).Buffer(buffDist, quadsecs);
                                gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferCrvs, revTransform), new GH_Path(a));
                            }
                        }
                        else
                        {
                            if (crvs.Count == buffList.Count)
                            {
                                OSGeo.OGR.Geometry bufferedSubCrvs = new OSGeo.OGR.Geometry(wkbGeometryType.wkbMultiPolygon25D);
                                for (int crvIndex = 0; crvIndex<crvs.Count; crvIndex ++)
                                {
                                    bufferedSubCrvs.AddGeometry(Heron.Convert.CurveToOgrLinestring(crvs[crvIndex], transform).Buffer(buffList[crvIndex].Value, quadsecs));
                                }
                                gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferedSubCrvs.Buffer(0,quadsecs), revTransform), new GH_Path(a));
                            }
                            else
                            {
                                var bufferCrvs = Heron.Convert.CurvesToOgrMultiLinestring(crvs, transform).Buffer(buffDist, quadsecs);
                                gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferCrvs, revTransform), new GH_Path(a));
                            }
                        }
                        
                        break;

                    case "Mesh":
                        geomList.First().CastTo<Mesh>(out mesh);
                        mesh.Ngons.AddPlanarNgons(DocumentTolerance());
                        var bufferPoly = Ogr.ForceToMultiPolygon(Heron.Convert.MeshToMultiPolygon(mesh, transform)).Buffer(buffDist, quadsecs);
                        gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferPoly, revTransform), new GH_Path(a));
                        break;

                    case "MultiMesh":
                        foreach (var m in geomList)
                        {
                            Mesh meshPart = new Mesh();
                            m.CastTo<Mesh>(out meshPart);
                            meshPart.Ngons.AddPlanarNgons(DocumentTolerance());
                            multiMesh.Append(meshPart);
                        }
                        var bufferPolys = Ogr.ForceToMultiPolygon(Heron.Convert.MeshToMultiPolygon(multiMesh, transform)).Buffer(buffDist, quadsecs);
                        gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferPolys, revTransform), new GH_Path(a));
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
                        var bufferCol = geoCollection.Buffer(buffDist, quadsecs);
                        gGooBuffered.AppendRange(Heron.Convert.OgrGeomToGHGoo(bufferCol, revTransform), new GH_Path(a));
                        break;


                    default:
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Not able to export " + geomType + " geometry at branch " + gGoo.get_Path(a).ToString() +
                            ". Geometry must be a Point, Curve or Mesh.");
                        break;
                }


            }

            def.Dispose();
            layer.Dispose();
            ds.Dispose();

            DA.SetDataTree(0, gGooBuffered);
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
            get { return new Guid("e71dd99c-d9d1-41fc-a201-f569a382493a"); }
        }
    }
}