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
    public class GdalConvexHull : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MyComponent1 class.
        /// </summary>
        public GdalConvexHull()
          : base("Gdal Convex Hull", "GCVH",
              "Create a convex hull around geometry using Gdal's convex hull function.  GDAL convex hull only works in 2D, so the results will be on the XY plane.",
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
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Hull Geometry", "H", "Hull geometry.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {

            GH_Structure<IGH_GeometricGoo> gGoo = new GH_Structure<IGH_GeometricGoo>();
            DA.GetDataTree<IGH_GeometricGoo>("Feature Geometry", out gGoo);

            GH_Structure<IGH_GeometricGoo> gGooHull = new GH_Structure<IGH_GeometricGoo>();

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

                List<string> geomTypeList = geomList.Select(o => o.TypeName).ToList();

                ///Add geomtery to feature
                ///Create containers for translating from GH Goo
                Point3d pt = new Point3d();
                List<Point3d> pts = new List<Point3d>();

                Curve crv = null;
                List<Curve> crvs = new List<Curve>();

                Mesh mesh = new Mesh();
                Mesh multiMesh = new Mesh();


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
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Not able to export " + geomTypeMixed + " geometry at branch " + gGoo.get_Path(a).ToString() +
                                ". Geometry must be a Point, Curve or Mesh.");
                            break;
                    }

                }

                var hullCol = geoCollection.ConvexHull();
                gGooHull.AppendRange(Heron.Convert.OgrGeomToGHGoo(hullCol, revTransform), new GH_Path(a));

            }

            def.Dispose();
            layer.Dispose();
            ds.Dispose();

            DA.SetDataTree(0, gGooHull);
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
            get { return new Guid("E09EE6ED-8126-4624-AE3B-B400C43DDDE0"); }
        }
    }
}