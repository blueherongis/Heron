using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.GUI;
using GH_IO;
using GH_IO.Serialization;

using Rhino;
using Rhino.Geometry;

namespace Heron
{
    public class VisualCenter : GH_TaskCapableComponent<VisualCenter.SolveResults>
    {
        /// <summary>
        /// Initializes a new instance of the VisualCenter class.
        /// </summary>
        public VisualCenter()
          : base("Visual Center", "VC",
              "Find the visual center of closed planar curves. The resulting point will lie within the boundary of the curve and multiple curves on a branch will be treated as a surface with holes.",
              "Heron","Utilities")
        {
        }

        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.quarternary; }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Closed Curves", "CC", "Closed curves of which to find the visual center", GH_ParamAccess.list);
            pManager.AddNumberParameter("Tolerance", "T", "Tolerance in document units.  The smaller the tolerance, the longer it take to resolve the solution.", GH_ParamAccess.item, 1.0);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("Visual Center", "VC", "Visual center of closed curve", GH_ParamAccess.list);
            //pManager.AddRectangleParameter("Boxes", "B", "test", GH_ParamAccess.list);
        }

        public class SolveResults
        {
            public List<Point3d> centers { get; set; }
        }

        SolveResults Compute (List<Curve> closedCrvs, double tol)
        {
            var rc = new SolveResults();

            List<Point3d> centers = new List<Point3d>();
            List<Rectangle3d> boxes = new List<Rectangle3d>();

            Brep[] srfs = Brep.CreatePlanarBreps(closedCrvs, tol);

            if (srfs != null)
            {
                ///Pick the srf with the biggest area in order to discard smaller islands
                ///TODO: Add option to include islands with a foreach in srfs
                Brep srf = srfs[0];
                double area = AreaMassProperties.Compute(srf).Area;
                foreach (var b in srfs)
                {
                    if (AreaMassProperties.Compute(b).Area > area)
                    {
                        area = AreaMassProperties.Compute(b).Area;
                        srf = b;
                    }
                }

                /// Based on javascript code from: https://github.com/mapbox/polylabel/blob/master/polylabel.js

                Point3d vc = new Point3d();

                /// Find bounding box of the outer ring
                BoundingBox bb = srf.GetBoundingBox(true);
                var minX = bb.Min.X;
                var minY = bb.Min.Y;
                var maxX = bb.Max.X;
                var maxY = bb.Max.Y;
                var width = maxX - minX;
                var height = maxY - minY;
                var cellSize = Math.Min(width, height);
                var h = cellSize / 2;

                /// A priority queue of cells in order of their "potential" (max distance to polygon)
                List<Cell> cellList = new List<Cell>();
                //List<Cell> cellArchive = new List<Cell>();

                if (cellSize == 0) vc = bb.Min;

                /// Cover polygon with initial cells
                for (var x = minX; x < maxX; x += cellSize)
                {
                    for (var y = minY; y < maxY; y += cellSize)
                    {
                        cellList.Add(new Cell(x + h, y + h, h, srf));
                        //cellList.Sort((a, b) => a.max.CompareTo(b.max));
                        //cellArchive.AddRange(cellList); //if (showBoxes) cellArchive.AddRange(cellList);
                    }
                }
                cellList.Sort((a, b) => a.max.CompareTo(b.max));

                /// Take centroid as the first best guess
                Cell bestCell = GetCentroidCell(srf);

                /// Special case for rectangular polygons
                Cell bboxCell = new Cell(minX + width / 2, minY + height / 2, 0, srf);
                if (bboxCell.d > bestCell.d) { bestCell = bboxCell; }

                /// Do the work
                while (cellList.Count > 0)
                {

                    /// Pick the most promising cell from the queue
                    Cell cell = cellList[cellList.Count - 1];
                    cellList.RemoveAt(cellList.Count - 1);

                    /// Update the best cell if we found a better one
                    if (cell.d > bestCell.d)
                    {
                        bestCell = cell;
                    }

                    /// Do not drill down further if there's no chance of a better solution
                    if (cell.max - bestCell.d <= tol) continue;

                    /// Split the cell into four cells
                    h = cell.h / 2;

                    cellList.Add(new Cell(cell.x - h, cell.y - h, h, srf));
                    cellList.Add(new Cell(cell.x + h, cell.y - h, h, srf));
                    cellList.Add(new Cell(cell.x - h, cell.y + h, h, srf));
                    cellList.Add(new Cell(cell.x + h, cell.y + h, h, srf));
                    cellList.Sort((a, b) => a.max.CompareTo(b.max));
                    //if (cellArchive.Count < 1000) cellArchive.AddRange(cellList);
                }

                centers.Add(new Point3d(bestCell.x, bestCell.y, 0));
                //boxes.AddRange(cellArchive.Select(x => x.box));

            }
            rc.centers = centers;
            return rc;
        }


        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            if (InPreSolve)
            {
                ///First pass; collect data and construct tasks
                List<Curve> closedCrvs = new List<Curve>();
                double tol = DocumentTolerance();
                Task<SolveResults> tsk = null;

                if (DA.GetDataList<Curve>(0, closedCrvs) && DA.GetData<double>(1, ref tol))
                {
                    tsk = Task.Run(() => Compute(closedCrvs, tol), CancelToken);
                }

                ///Add a null task even if data collection fails.  This keeps the list size in sync with the iterations
                TaskList.Add(tsk);
                return;
            }

            if (!GetSolveResults(DA, out var results))
            {
                ///Compute right here, right now.
                ///1. Collect
                List<Curve> closedCrvs = new List<Curve>();
                double tol = DocumentTolerance();

                if (!DA.GetDataList<Curve>(0, closedCrvs)) { return; }
                if (!DA.GetData<double>(1, ref tol)) { return; }

                ///2. Compute
                results = Compute(closedCrvs, tol);
            }

            ///3. Set
            if (results != null)
            {
                DA.SetDataList(0, results.centers);
            }
        }


        public class Cell
        {
            public double x, y, h, d, max;
            public Rectangle3d box;

            public Point3d c;
            public bool ins;

            public Cell(double x, double y, double h, Brep polygon)
            {
                this.x = x; // cell center x
                this.y = y; // cell center y
                this.h = h; // half the cell size
                this.c = new Point3d(x, y, 0);  // cell center point
                this.d = DistanceToBrepEdge(polygon, this.c);
                this.max = this.d + this.h * Math.Sqrt(2); // max distance to polygon within a cell
                Plane pl = new Plane(new Point3d(x - h, y - h, 0), new Vector3d(0, 0, 1));
                this.box = new Rectangle3d(pl, h * 2, h * 2);
            }
        }

        public static Cell GetCentroidCell(Brep polygon)
        {
            Point3d center = polygon.GetBoundingBox(true).PointAt(0.5, 0.5, 0.5);
            return new Cell(center.X, center.Y, 0, polygon);
        }

        public static double DistanceToBrepEdge(Brep polygon, Point3d point)
        {
            var closestPoint = polygon.Edges[0].PointAtStart;
            var distance = Double.PositiveInfinity;// point.DistanceTo(closestPoint);
            
            foreach (var edge in polygon.Edges)
            {
                double t_prm;
                if (edge.ClosestPoint(point, out t_prm, distance))
                {
                    var tmpClosestpoint = edge.PointAt(t_prm);
                    var tmpDistance = point.DistanceTo(tmpClosestpoint);
                    if (tmpDistance < distance)
                    {
                        distance = tmpDistance;
                    }
                }
            }

            bool inside = IsInside(point, polygon);
            int sign;
            if (inside) sign = 1;
            else sign = -1;
            return sign * distance;
        }

        public static bool IsInside (Point3d point, Brep polygon)
        {
            bool inside = true;
            double dist = point.DistanceTo(polygon.ClosestPoint(point));
            if (dist > DocumentTolerance()) inside = false;
            return inside;
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
            get { return new Guid("f63fb5c5-a307-4a7a-94dc-16b400bd464f"); }
        }
    }
}