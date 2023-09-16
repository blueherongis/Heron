using Grasshopper.Kernel;
using Grasshopper.Kernel.Geometry;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;

namespace Heron.Components.Utilities
{
    public class DecimateTopoFromPoint : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the DecimateTopoFromPoint class.
        /// </summary>
        public DecimateTopoFromPoint()
          : base("Decimate Topography From Point", "DTP",
              "Reduce the number of vertexes of a topo mesh the farther they are from a given point.",
              "Utilities")
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
            pManager.AddPointParameter("View Point", "V", "The point from which to decimate the mesh.  " +
                "The farther from this point a mesh vertex is, the more likely it will be eliminated from the mesh.", GH_ParamAccess.item);
            pManager.AddMeshParameter("Topography Mesh", "M", "The topographic (2.5D) mesh to be decimated.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Distance Increments", "D", "List of distance increments for which to apply the corresponding percent reduction.  " +
                "The number of distance and percent increments must match.", GH_ParamAccess.list);
            pManager[2].Optional = true;
            pManager.AddNumberParameter("Percent Increments", "P", "List of increments (from 0-1) by which to randomly reduce the mesh within each corresponding distance increment.  " +
                "Numbers less than 0 and more than 1 will be clamped back to the 0-1 range.  " +
                "The number of percent and distance increments must match.", GH_ParamAccess.list);
            pManager[3].Optional = true;
            pManager.AddIntegerParameter("Random Seed", "S", "Random seed used for randomly reducing the vertexes.", GH_ParamAccess.item, 1);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Decimated Mesh", "M", "The decimated mesh, a 2.5D Delaunay triagulation of the decimated vertexes.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Point3d p = new Point3d();
            DA.GetData<Point3d>("View Point", ref p);

            Mesh topoMesh = new Mesh();
            DA.GetData<Mesh>("Topography Mesh", ref topoMesh);

            List<double> distanceIncrements = new List<double>();
            DA.GetDataList<double>("Distance Increments", distanceIncrements);

            List<double> percentIncrements = new List<double>();
            DA.GetDataList<double>("Percent Increments", percentIncrements);

            int seed = 1;
            DA.GetData<int>("Random Seed", ref seed);

            BoundingBox bbox = topoMesh.GetBoundingBox(false);
            double diagonal = bbox.Diagonal.Length;

            ///Default ranges
            var distRanges = new List<double>() { diagonal * 0.1 / 2, diagonal * 0.2 / 2, diagonal * 0.3 / 2, diagonal * 0.4 / 2 };
            var pctRanges = new List<double>() { 0.5, 0.8, 0.9, 0.99 };
            
            ///Clamp values of user input
            if(distanceIncrements.Count != 0) { distRanges = distanceIncrements.Select(x => Math.Max(x,0.0)).ToList(); }
            if(percentIncrements.Count != 0) { pctRanges = percentIncrements.Select(x => Math.Min(Math.Max(x, 0.0), 1.0)).ToList(); }


            if (distRanges.Count != pctRanges.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The number of Distance Increments does not match the number of Percent Increments. The default is 4 increments.");
                return;
            }

            pctRanges.Insert(0, 0.0);


            List<Point3d> vertexPoints = topoMesh.Vertices.ToPoint3dArray().ToList();
            List<Point3d> nakedVerts = new List<Point3d>();
            List<Point3d> clothedVerts = new List<Point3d>();

            ///Allow maintaining of naked vertexes
            bool keepNakedVerts = true;
            if (keepNakedVerts)
            {
                bool[] nakedArray = topoMesh.GetNakedEdgePointStatus();
                for (int v = 0; v < nakedArray.Length; v++)
                {
                    if (!nakedArray[v]) { clothedVerts.Add(vertexPoints[v]); }
                    else { nakedVerts.Add(vertexPoints[v]); }
                }

                vertexPoints = clothedVerts;
            }


            ///Points grouped by distance
            var pointGroups = vertexPoints.GroupBy(x => distRanges.FirstOrDefault(r => r > Math.Sqrt(x.DistanceToSquared(p))))
                .OrderBy(grp => grp.First().DistanceToSquared(p))
                .Select(x => new List<Point3d>(x))
                .ToList();

            ///Randomly reduce points in a group based on user provided percentages
            var delPoints = new List<Point3d>();
            for (int i=0; i<pointGroups.Count; i++)
            {
                Random rnd = new Random(seed);
                int count = (int) (pointGroups[i].Count * (1-pctRanges[i]));
                delPoints.AddRange(pointGroups[i].OrderBy(x => rnd.Next()).Take(count));
            }

            ///Create a Delaunay triangulated mesh
            delPoints.AddRange(nakedVerts);
            Mesh delMesh = DelaunayPoints(delPoints);
            delMesh.Faces.ConvertTrianglesToQuads(0, 0);

            DA.SetData(0, delMesh);
        }


        /// From https://discourse.mcneel.com/t/3d-delaunay/126194
        public Mesh DelaunayPoints(List<Point3d> pts)
        {
            //code from http://james-ramsden.com/create-2d-delaunay-triangulation-mesh-with-c-in-grasshopper/

            //convert point3d to node2
            //grasshopper requres that nodes are saved within a Node2List for Delaunay
            var nodes = new Node2List();
            for (int i = 0; i < pts.Count; i++)
            {
                //notice how we only read in the X and Y coordinates
                //  this is why points should be mapped onto the XY plane
                nodes.Append(new Node2(pts[i].X, pts[i].Y));
            }

            //solve Delaunay
            var delMesh = new Mesh();
            var faces = new List<Grasshopper.Kernel.Geometry.Delaunay.Face>();

            faces = Grasshopper.Kernel.Geometry.Delaunay.Solver.Solve_Faces(nodes, DocumentTolerance());

            //output
            delMesh = Grasshopper.Kernel.Geometry.Delaunay.Solver.Solve_Mesh(nodes, DocumentTolerance(), ref faces);
            for (int i = 0; i < pts.Count; i++)
            {
                delMesh.Vertices.SetVertex(i, pts[i]);
            }

            return delMesh;
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
                return null;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("0c6c5f78-9b7a-4e53-8d3c-4e8f6c1c2632"); }
        }
    }
}