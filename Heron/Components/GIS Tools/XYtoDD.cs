using Grasshopper.Kernel;
using Rhino.Geometry;
using System;

namespace Heron
{
    public class XYtoDD : HeronComponent
    {
        //Class Constructor
        public XYtoDD() : base("XY to Decimal Degrees", "XYtoDD", "Convert X/Y to Decimal Degrees Longitude/Latitude in the WGS84 spatial reference system", "GIS Tools")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("xyPoint", "xyPoint", "Point to translate to Longitude/Latitude", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "LAT", "Decimal Degree Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Decimal Degree Longitude", GH_ParamAccess.item);
            pManager.AddTransformParameter("Transform", "xForm", "The transform from XYZ to WGS", GH_ParamAccess.item);
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
            Transform heronToUserSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);
            Transform heronToWgsSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(wgsSRS);

            ///Set transforms between source and HeronSRS
            OSGeo.OSR.CoordinateTransformation revTransform = new OSGeo.OSR.CoordinateTransformation(heronSRS, wgsSRS);

            ///Dump out the transform first
            DA.SetData("Transform", heronToWgsSRSTransform);


            Point3d xyPt = new Point3d();
            if (!DA.GetData<Point3d>("xyPoint", ref xyPt)) return;

            xyPt.Transform(heronToUserSRSTransform);
            Point3d dd = Heron.Convert.OSRTransformPoint3dToPoint3d(xyPt, revTransform);

            DA.SetData("Latitude", dd.Y);
            DA.SetData("Longitude", dd.X);
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.xytodd;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{0B461B47-632B-4145-AA06-157AAFAC1DDA}"); }
        }
    }
}
