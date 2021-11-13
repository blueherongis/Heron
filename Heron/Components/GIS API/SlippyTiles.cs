using System;
using System.Collections.Generic;
using System.Drawing;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Heron
{
    public class SlippyTiles : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the SlippyTiles class.
        /// </summary>
        public SlippyTiles()
          : base("Slippy Tiles", "SlippyTiles",
              "Visualize boundaries of slippy map tiles within a given boundary at a given zoom level.  See https://en.wikipedia.org/wiki/Tiled_web_map for more information about map tiles.",
              "GIS API")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve for map tiles", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Zoom Level", "zoom", "Slippy map zoom level. Higher zoom level is higher resolution.", GH_ParamAccess.item);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Tile Extents", "tiles", "Map tile boundaries for each tile", GH_ParamAccess.list);
            pManager.AddTextParameter("Tile ID", "id", "Map tile ID. The tile ID is formatted 'Z-X-Y' where Z is zoom level, X is the column and Y the row.", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Curve boundary = null;
            DA.GetData<Curve>(0, ref boundary);

            int zoom = -1;
            DA.GetData<int>(1, ref zoom);

            ///Get image frame for given boundary
            if (!boundary.GetBoundingBox(true).IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Boundary is not valid.");
                return;
            }
            BoundingBox boundaryBox = boundary.GetBoundingBox(true);

            ///Tile bounding box array
            List<Point3d> boxPtList = new List<Point3d>();

            ///Get the tile coordinates for all tiles within boundary
            var ranges = Convert.GetTileRange(boundaryBox, zoom);
            var x_range = ranges.XRange;
            var y_range = ranges.YRange;

            if (x_range.Length > 100 || y_range.Length > 100)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "This tile range is too big (more than 100 tiles in the x or y direction). Check your units.");
                return;
            }

            ///Cycle through tiles to get bounding box
            List<Polyline> tileExtents = new List<Polyline>();
            List<string> tileID = new List<string>();

            for (int y = (int)y_range.Min; y <= y_range.Max; y++)
            {
                for (int x = (int)x_range.Min; x <= x_range.Max; x++)
                {
                    string tileString = zoom + "-" + x + "-" + y;
                    tileID.Add(tileString);
                    Polyline tileExtent = Heron.Convert.GetTileAsPolygon(zoom, y, x);
                    tileExtents.Add(tileExtent);
                    double tileHeight = tileExtent[1].DistanceTo(tileExtent[2]);

                    if (!string.IsNullOrWhiteSpace(tileString))
                    {
                        _text.Add(tileString);
                        _point.Add(tileExtent.CenterPoint());
                        _size.Add(tileHeight / 20);
                        _tile.Add(tileExtent);
                    }
                }
            }

            DA.SetDataList(0, tileExtents);
            DA.SetDataList(1, tileID);
        }

        ///Preview text and tile polylines
        ///https://www.grasshopper3d.com/forum/topics/drawing-a-text-tag-from-a-c-component?commentId=2985220%3AComment%3A1024697
        
        private readonly List<string> _text = new List<string>();
        private readonly List<Point3d> _point = new List<Point3d>();
        private readonly List<double> _size = new List<double>();
        private readonly List<Polyline> _tile = new List<Polyline>();

        protected override void BeforeSolveInstance()
        {
            _text.Clear();
            _point.Clear();
            _size.Clear();
            _tile.Clear();
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (_text.Count == 0)
                return;

            //Plane plane;
            //args.Viewport.GetFrustumFarPlane(out plane);

            for (int i = 0; i < _text.Count; i++)
            {
                string text = _text[i];
                Point3d point = _point[i];
                double size = _size[i];
                Polyline tile = _tile[i];

                Plane plane;
                args.Viewport.GetFrustumFarPlane(out plane);
                plane.Origin = point;

                Rhino.Display.Text3d drawText = new Rhino.Display.Text3d(text, plane, size);
                args.Display.Draw3dText(text, Color.Black, plane, size, null, false, false, Rhino.DocObjects.TextHorizontalAlignment.Center, Rhino.DocObjects.TextVerticalAlignment.Middle);
                args.Display.DrawPolyline(tile, Color.Black, 2);
                drawText.Dispose();
            }
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
            get { return new Guid("ae47ba29-49ae-4bbe-b76c-7335e725e91e"); }
        }
    }
}