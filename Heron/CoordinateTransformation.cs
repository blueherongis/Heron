using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Data;
using Rhino.Geometry;
using System;
using System.Collections.Generic;

using OSGeo.OGR;

namespace Heron
{
    public class CoordinateTransformation : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the TranslateTo class.
        /// </summary>
        public CoordinateTransformation()
          : base("Coordinate Transformation", "CT",
              "Transform points from a source SRS to a destination SRS. The source points should be in the coordinate system of the source SRS.",
              "GIS Tools")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("Source Points", "sourcePoints", "Points to transform.", GH_ParamAccess.tree);
            pManager.AddTextParameter("Source SRS", "sourceSRS", "Source spatial reference system from which to translate the points.  " +
                "This can be a simple EPSG code (ie 'EPSG:4326') or a full projection string (ie text from a prj file).", GH_ParamAccess.item, "WGS84");
            pManager.AddTextParameter("Destination SRS", "destSRS", "Destination spatial reference system to which to translate the points.  " +
                "This can be a simple EPSG code (ie 'EPSG:4326') or a full projection string (ie text from a prj file).", GH_ParamAccess.item, "WGS84");
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("Translated Points", "destPoints", "Translated points", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();

            ///Working with data trees allows us to only call the osr coordinate transformation once, which seems to be expensive
            GH_Structure<GH_Point> sourcePoints = new GH_Structure<GH_Point>();
            DA.GetDataTree(0, out sourcePoints);

            GH_Structure<GH_Point> destPoints = new GH_Structure<GH_Point>();

            string sourceString = string.Empty;
            DA.GetData(1, ref sourceString);
            OSGeo.OSR.SpatialReference sourceSRS = new OSGeo.OSR.SpatialReference("");
            sourceSRS.SetFromUserInput(sourceString);
            if (sourceSRS.Validate()==1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid Source SRS.");
                return;
            }

            string destString = string.Empty;
            DA.GetData(2, ref destString);
            OSGeo.OSR.SpatialReference destSRS = new OSGeo.OSR.SpatialReference("");
            destSRS.SetFromUserInput(destString);
            if (destSRS.Validate() == 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid Destination SRS.");
                return;
            }

            OSGeo.OSR.CoordinateTransformation trans = new OSGeo.OSR.CoordinateTransformation(sourceSRS, destSRS);

            foreach(var path in sourcePoints.Paths)
            {
                List<GH_Point> branchPts = (List<GH_Point>)sourcePoints.get_Branch(path);
                foreach (var sp in branchPts)
                {
                    OSGeo.OGR.Geometry destOgrPoint = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
                    destOgrPoint.AddPoint(sp.Value.X, sp.Value.Y, sp.Value.Z);
                    destOgrPoint.AssignSpatialReference(sourceSRS);

                    destOgrPoint.Transform(trans);
                    Point3d destPoint = new Point3d(destOgrPoint.GetX(0), destOgrPoint.GetY(0), destOgrPoint.GetZ(0));

                    destPoints.Append(new GH_Point(destPoint),path);
                    destOgrPoint.Dispose();
                }
            }

            DA.SetDataTree(0, destPoints);


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
                return Properties.Resources.xytodd;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("0c32eee6-1721-4a0b-bd68-a10b0a7b6ccb"); }
        }
    }
}