using System;
using System.Collections.Generic;
using System.Linq;

using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Heron
{
    public class SlippyViewport : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the SlippyViewport class.
        /// </summary>
        public SlippyViewport()
          : base("Slippy Viewport", "SlippyVP", "Projects the boundary of a given Viewport to the World XY plane and calculates a good Zoom level for use with tile-based map components.", "GIS API")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Named Viewport", "view", "Provide the name of the viewport to be used.", GH_ParamAccess.item, "Top");
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Viewport Boundary", "boundary", "The boundary of the given viewport projected onto the World XY plane", GH_ParamAccess.item);
            pManager.AddNumberParameter("Zoom Level", "zoom", "A good zoom level to be used with a Raster API componenet given the extents of viewport boundary.  Max zoom level is set to 21", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Point3d> pProjected = new List<Point3d>();

            string view = string.Empty;
            DA.GetData<string>(0, ref view);
            viewportName = view;
            ///Get viewport boundary
            Rhino.Display.RhinoView[] rvList = Rhino.RhinoDoc.ActiveDoc.Views.GetViewList(true, false);
            Rhino.Display.RhinoView rv = Rhino.RhinoDoc.ActiveDoc.Views.Find(view, true);

            if (!rvList.Contains(rv))
            {
                rv = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Viewport name is not valid. Using active viewport " + rv.ActiveViewport.Name);
            }


            Rhino.Display.RhinoViewport vp = rv.MainViewport;
            Point3d[] pNear = rv.MainViewport.GetNearRect();
            Point3d[] pFar = rv.MainViewport.GetFarRect();

            ///Project viewport boundary to a plane
            for (int i = 0; i < pNear.Length; i++)
            {
                Vector3d tVec = pFar[i] - pNear[i];
                Transform trans = Transform.ProjectAlong(Plane.WorldXY, tVec);
                pNear[i].Transform(trans);
                pProjected.Add(pNear[i]);
            }

            ///Create polyline from project viewport boundary
            Polyline pL = new Polyline();
            pL.Add(pProjected[2]);
            pL.Add(pProjected[3]);
            pL.Add(pProjected[1]);
            pL.Add(pProjected[0]);
            pL.Add(pProjected[2]);


            ///Calculate recommended zoom level from viewport size
            BoundingBox bb = pL.BoundingBox;
            Vector3d dia = bb.Diagonal;
            Double maxDim = Math.Max(dia.X, dia.Y) * Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);
            Double maxPix = Math.Max(vp.Size.Height, vp.Size.Width);


            ///https://gis.stackexchange.com/questions/19632/how-to-calculate-the-optimal-zoom-level-to-display-two-or-more-points-on-a-map
            ///diameter of earth at equator is approx 40,000km
            ///resolution = (512px*distance)/40,075,000 meters * 2^zoom
            ///2^zoom = (resolution * 40,000,000) / (512px * distance)
            Double a = (maxPix * 40075000) / (512 * maxDim * 1.2);

            ///Solve for zoom
            ///https://stackoverflow.com/questions/4016213/whats-the-opposite-of-javascripts-math-pow
            Double z = Math.Log(a) / Math.Log(2);


            ///make sure zoom doesn't get too ridiculous levels
            DA.SetData(0, pL);
            DA.SetData(1, Math.Min(z, 21));
        }

        public override bool IsPreviewCapable => true;

        private int updateFrequencyMS = 300;
        private Point3d lastCameraLocation = default(Point3d);
        private string viewportName = null;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (args.Display.Viewport.Name == viewportName)
            {
                var delta = args.Display.Viewport.CameraLocation.DistanceToSquared(lastCameraLocation);
                if (delta > 1) // if it's moved
                {
                    lastCameraLocation = args.Display.Viewport.CameraLocation;
                    OnPingDocument().ScheduleSolution(updateFrequencyMS, (doc) =>
                    {
                        ExpireSolution(false);
                    });
                }
            }
            base.DrawViewportWires(args);
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
                return Properties.Resources.vector;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("76082489-C7F2-403A-9490-BC52374C4F2B"); }
        }
    }
}