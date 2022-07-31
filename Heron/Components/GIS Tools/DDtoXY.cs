using Grasshopper.Kernel;
using Rhino.Geometry;
using System;

namespace Heron
{
    public class DDtoXY : HeronComponent
    {
        //Class Constructor
        public DDtoXY() : base("Decimal Degrees to XY", "DDtoXY", "Convert WGS84 Decimal Degrees Longitude/Latitude to X/Y", "GIS Tools")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "LAT", "Decimal Degree Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Decimal Degree Longitude", GH_ParamAccess.item);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("xyPoint", "xyPoint", "Longitude/Latitude translated to X/Y", GH_ParamAccess.item);
            pManager.AddTransformParameter("Transform", "xForm", "The transform from WGS to XYZ", GH_ParamAccess.item);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();

            ///Set transform from input spatial reference to Heron spatial reference
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            OSGeo.OSR.SpatialReference wgsSRS = new OSGeo.OSR.SpatialReference("");
            wgsSRS.SetFromUserInput("WGS84");
            //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + heronSRSInt;

            ///Apply EAP to HeronSRS
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
            Transform wgsToHeronSRSTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(wgsSRS);

            ///Set transforms between source and HeronSRS
            OSGeo.OSR.CoordinateTransformation coordTransform = new OSGeo.OSR.CoordinateTransformation(wgsSRS, heronSRS);

            ///Dump out the transform first
            DA.SetData("Transform", wgsToHeronSRSTransform);


            /// Then, we need to retrieve all data from the input parameters.
            /// We'll start by declaring variables and assigning them starting values.
            double lat = -1;
            double lon = -1;

            /// Then we need to access the input parameters individually. 
            /// When data cannot be extracted from a parameter, we should abort this method.
            if (!DA.GetData("Latitude", ref lat)) return;
            if (!DA.GetData("Longitude", ref lon)) return;

            /// We should now validate the data and warn the user if invalid data is supplied.
            if (lat < -90.0 || lat > 90.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Latitude should be between -90.0 deg and 90.0 deg");
                return;
            }
            if (lon < -180.0 || lon > 180.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Longitude should be between -180.0 deg and 180.0 deg");
                return;
            }

            Point3d dd = Heron.Convert.OSRTransformPoint3dToPoint3d(new Point3d(lon, lat, 0), coordTransform);
            dd.Transform(userSRSToModelTransform);
            DA.SetData("xyPoint", dd);

        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.ddtoxy;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{78543216-14B5-422C-85F8-BB575FBED3D2}"); }
        }
    }
}
