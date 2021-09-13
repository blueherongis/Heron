using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using System.Linq;

namespace Heron.Components.GIS_Tools
{
    public class MultiMoveToMesh : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MultiMoveToMesh class.
        /// </summary>
        public MultiMoveToMesh()
          : base("MultiMoveToTopo", "MMoveToTopo",
              "Move breps, meshes, polylines and points to a topo mesh.  ",
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
            pManager.AddMeshParameter("Topographic Mesh", "topoMesh", "Mesh representing the topography to which to move objects.", GH_ParamAccess.item);
            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry to move to topography.  Geometry can be points, polylines, meshes or breps.  " +
                "Open meshes will be flattened to topography", GH_ParamAccess.tree);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Moved Geometry", "movedGeomery", "Resulting moved feature geometry.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh topoMesh = new Mesh();
            DA.GetData<Mesh>("Topographic Mesh", ref topoMesh);

            GH_Structure<IGH_GeometricGoo> featureGoo = new GH_Structure<IGH_GeometricGoo>();
            DA.GetDataTree<IGH_GeometricGoo>("Feature Geometry", out featureGoo);

            //reserve one processor for GUI
            totalMaxConcurrancy = System.Environment.ProcessorCount - 1;

            //tells us how many threads were using
            Message = totalMaxConcurrancy + " threads";

            Mesh topoFlat = topoMesh.DuplicateMesh();
            for (int i = 0; i < topoFlat.Vertices.Count; i++)
            {
                Point3f v = topoFlat.Vertices[i];
                v.Z = 0;
                topoFlat.Vertices.SetVertex(i, v);
            }

            Point3d[] topoFlatPoints = topoFlat.Vertices.ToPoint3dArray();

            //create a dictionary that works in parallel
            var gooTree = new System.Collections.Concurrent.ConcurrentDictionary<GH_Path, List<IGH_GeometricGoo>>();

            //Multi-threading the loop
            System.Threading.Tasks.Parallel.ForEach(featureGoo.Paths,
              new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = totalMaxConcurrancy },
              pth =>
              {

                  ///Create containers for translating from GH Goo
                  Point3d pt = new Point3d();
                  Polyline pLine = null;
                  PolylineCurve pLineCurve = null;
                  Curve curve = null;
                  Mesh mesh = new Mesh();
                  Brep brep = new Brep();

                  ///Output container list
                  List<IGH_GeometricGoo> gGooList = new List<IGH_GeometricGoo>();


                  if (featureGoo.get_Branch(pth).Count > 0)
                  {
                      foreach (var bGoo in featureGoo.get_Branch(pth))
                      {

                          ///Get geometry type(s) in branch
                          string geomType = string.Empty;
                          IGH_GeometricGoo fGoo = GH_Convert.ToGeometricGoo(bGoo);
                          if (fGoo != null)
                          {
                              geomType = fGoo.TypeName;

                              switch (geomType)
                              {
                                  case "Point":
                                      fGoo.CastTo<Point3d>(out pt);
                                      gGooList.Add(ProjectPointToTopo(topoMesh, pt));
                                      break;

                                  case "Polyline":
                                      fGoo.CastTo<Polyline>(out pLine);
                                      gGooList.Add(ProjectPolylineToTopo(topoMesh, topoFlat, pLine));
                                      break;

                                  case "PolylineCurve":
                                      fGoo.CastTo<PolylineCurve>(out pLineCurve);
                                      gGooList.Add(ProjectPolylineToTopo(topoMesh, topoFlat, pLineCurve.ToPolyline()));
                                      break;

                                  case "Curve":
                                      fGoo.CastTo<Curve>(out curve);
                                      curve.TryGetPolyline(out pLine);
                                      if (pLine.IsValid)
                                      {
                                          gGooList.Add(ProjectPolylineToTopo(topoMesh, topoFlat, pLine));
                                      }
                                      break;

                                  case "Mesh":
                                      fGoo.CastTo<Mesh>(out mesh);
                                      if (mesh.IsClosed)
                                      {
                                          gGooList.Add(ProjectSolidMeshToTopo(topoMesh, mesh));
                                      }
                                      else
                                      {
                                          //gGooList.Add(ProjectMeshToTopo(topoMesh, topoFlat, topoFlatPoints, mesh));
                                          gGooList.Add(ProjectSolidMeshToTopo(topoMesh, mesh));
                                      }
                                      break;

                                  case "Brep":
                                      fGoo.CastTo<Brep>(out brep);
                                      gGooList.Add(ProjectBrepToTopo(topoMesh, brep));
                                      break;

                                  default:
                                      AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Not able to move " + geomType + " geometry to mesh" +
                                          ". Geometry must be a Point, Curve, Mesh or Brep.");
                                      break;
                              }
                          }
                          else { }
                      }
                  }

                  gooTree[pth] = gGooList;

              });
            ///End of multi-threaded loop

            ///Convert dictionary to regular old data tree
            GH_Structure<IGH_GeometricGoo> gTree = new GH_Structure<IGH_GeometricGoo>();
            foreach (KeyValuePair<GH_Path, List<IGH_GeometricGoo>> g in gooTree)
            {
                gTree.AppendRange(g.Value, g.Key);
            }

            DA.SetDataTree(0, gTree);

        }

        ///Projection engines
        public static GH_Point ProjectPointToTopo(Mesh topoMesh, Point3d pt)
        {
            GH_Point ghPoint = new GH_Point();
            Ray3d ray = new Ray3d(pt, moveDir);
            double t = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, ray);
            if (t >= 0.0)
            {
                GH_Convert.ToGHPoint(ray.PointAt(t), GH_Conversion.Primary, ref ghPoint);
            }
            else
            {
                Ray3d rayOpp = new Ray3d(pt, -moveDir);
                double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, rayOpp);
                if (tOpp >= 0.0)
                {
                    GH_Convert.ToGHPoint(rayOpp.PointAt(tOpp), GH_Conversion.Primary, ref ghPoint);
                }
                else
                {
                    return null;
                }
            }
            return ghPoint;
        }

        public static GH_Curve ProjectPolylineToTopo(Mesh topoMesh, Mesh topoFlatMesh, Polyline pLine)
        {
            GH_Curve ghCurve = new GH_Curve();
            PolylineCurve polylineCurve = pLine.ToPolylineCurve();
            Polyline flatPulledCurve = polylineCurve.PullToMesh(topoFlatMesh, tol).ToPolyline();

            List<Point3d> projectedPts = new List<Point3d>();
            for (int j = 0; j < flatPulledCurve.Count; j++)
            {
                Ray3d ray = new Ray3d(flatPulledCurve[j], moveDir);
                double t = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, ray);
                if (t >= 0.0)
                {
                    projectedPts.Add(ray.PointAt(t));
                }
                else
                {
                    Ray3d rayOpp = new Ray3d(flatPulledCurve[j], -moveDir);
                    double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, rayOpp);
                    if (tOpp >= 0.0)
                    {
                        projectedPts.Add(rayOpp.PointAt(tOpp));
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            Polyline pLineOut = new Polyline(projectedPts);
            if (pLineOut.IsValid)
            {
                GH_Convert.ToGHCurve(pLineOut.ToNurbsCurve(), GH_Conversion.Primary, ref ghCurve);
                return ghCurve;
            }
            else { return null; }
        }

        public static GH_Mesh ProjectMeshToTopo(Mesh topoMesh, Mesh topoFlatMesh, Point3d[] topoFlatPoints, Mesh featureMesh)
        {
            GH_Mesh ghMesh = new GH_Mesh();


            featureMesh.Vertices.AddVertices(topoFlatPoints);
            Point3d[] newVerts = featureMesh.Vertices.ToPoint3dArray();

            Polyline[] nakedEdges = featureMesh.GetNakedEdges();

            ///Pull naked edges to flat topo to add control points at mesh edge intersections
            List<PolylineCurve> trimCurves = new List<PolylineCurve>();
            foreach (Polyline pLine in nakedEdges)
            {
                trimCurves.Add(pLine.ToPolylineCurve().PullToMesh(topoFlatMesh, tol));
            }

            Polyline pL = trimCurves[0].ToPolyline();
            if (!pL.IsClosed) { return null; }

            List<Curve> innerBoundaries = new List<Curve>();
            for (int i = 1; i < nakedEdges.Length; i++)
            {
                innerBoundaries.Add(nakedEdges[i].ToNurbsCurve());
            }

            ///create patch
            Mesh mPatch = Mesh.CreatePatch(pL, tol, null, innerBoundaries, null, newVerts, true, 1);

            ///move patch verts to topo
            for (int i = 0; i < mPatch.Vertices.Count; i++)
            {
                Ray3d ray = new Ray3d((Point3d)mPatch.Vertices[i], moveDir);
                double t = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, ray);
                if (t >= 0.0)
                {
                    mPatch.Vertices.SetVertex(i, (Point3f)ray.PointAt(t));
                }
                else
                {
                    Ray3d rayOpp = new Ray3d((Point3d)mPatch.Vertices[i], -moveDir);
                    double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, rayOpp);
                    if (tOpp >= 0.0)
                    {
                        mPatch.Vertices.SetVertex(i, (Point3f)rayOpp.PointAt(tOpp));
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            if (mPatch.IsValid)
            {
                GH_Convert.ToGHMesh(mPatch, GH_Conversion.Primary, ref ghMesh);
                return ghMesh;
            }
            else { return null; }
        }

        public static GH_Mesh ProjectSolidMeshToTopo(Mesh topoMesh, Mesh solidMesh)
        {
            GH_Mesh ghMesh = new GH_Mesh();
            List<Mesh> topoMeshList = new List<Mesh>();
            topoMeshList.Add(topoMesh);
            BoundingBox bb = solidMesh.GetBoundingBox(false);

            ///get list of verteces from mesh to project
            Point3d[] originalVerts = solidMesh.Vertices.ToPoint3dArray();

            ///create a list of verteces who share the lowest Z value of the bounding box
            List<Point3d> lowestVerts = originalVerts.Where(lowPoint => lowPoint.Z == bb.Min.Z).ToList();

            ///project list of lowest verts to mesh topo
            ///Intersection.ProjectPointsToMeshes is not as robust as MeshRay and misses some vertices
            //Point3d[] projectedVerts = Rhino.Geometry.Intersect.Intersection.ProjectPointsToMeshes(topoMeshList, lowestVerts, moveDir, tol);

            ///transalte mesh to project up with move vector and save to output array
            Vector3d moveV = GetProjectedPointsToMesh(lowestVerts, topoMesh);
            Vector3d maxV = new Vector3d(0, 0, Double.MaxValue);
            if (moveV == maxV)
            {
                return null;
            }

            ///transalte mesh to project up with move vector and save to output array
            solidMesh.Translate(moveV);

            if (solidMesh.IsValid)
            {
                GH_Convert.ToGHMesh(solidMesh, GH_Conversion.Primary, ref ghMesh);
                return ghMesh;
            }
            else { return null; }

        }

        public static GH_Brep ProjectBrepToTopo(Mesh topoMesh, Brep brep)
        {
            GH_Brep ghBrep = new GH_Brep();

            List<Mesh> topoMeshList = new List<Mesh>();
            topoMeshList.Add(topoMesh);
            BoundingBox bb = brep.GetBoundingBox(false);

            ///get list of verteces from mesh to project
            List<Point3d> originalVerts = new List<Point3d>();
            foreach (var v in brep.Vertices)
            {
                originalVerts.Add(v.Location);
            }

            ///create a list of verteces who share the lowest Z value of the bounding box
            List<Point3d> lowestVerts = originalVerts.Where(lowPoint => lowPoint.Z == bb.Min.Z).ToList();


            ///project list of lowest verts to mesh topo
            ///Intersection.ProjectPointsToMeshes is not as robust as MeshRay and misses some vertices
            //Point3d[] projectedVerts = Rhino.Geometry.Intersect.Intersection.ProjectPointsToMeshes(topoMeshList, lowestVerts, Vector3d.ZAxis, tol);


            ///transalte mesh to project up with move vector and save to output array
            Vector3d moveV = GetProjectedPointsToMesh(lowestVerts, topoMesh);
            Vector3d maxV = new Vector3d(0, 0, Double.MaxValue);
            if (moveV == maxV)
            { 
                return null;
            }
            brep.Translate(moveV);

            if (brep.IsValid)
            {
                GH_Convert.ToGHBrep(brep, GH_Conversion.Primary, ref ghBrep);
                return ghBrep;
            }
            else { return null; }

        }

        public static Vector3d GetProjectedPointsToMesh(List<Point3d> points, Mesh topoMesh)
        {
            List<Point3d> projectedPts = new List<Point3d>();
            foreach (Point3d pt in points)
            {
                Ray3d ray = new Ray3d(pt, moveDir);
                double t = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, ray);
                if (t >= 0.0)
                {
                    projectedPts.Add(ray.PointAt(t));
                }
                else
                {
                    Ray3d rayOpp = new Ray3d(pt, -moveDir);
                    double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, rayOpp);
                    if (tOpp >= 0.0)
                    {
                        projectedPts.Add(rayOpp.PointAt(tOpp));
                    }
                    else
                    {
                        return new Vector3d(0, 0, Double.MaxValue);
                    }
                }
            }

            Vector3d min_v = new Vector3d(0, 0, Double.MaxValue);

            for (int i = 0; i < points.Count; i++)
            {
                Vector3d v = projectedPts[i] - points[i];
                if (v.Length < min_v.Length) { min_v = v; }
            }

            return min_v;
        }

        private static int totalMaxConcurrancy = 1;

        private static Vector3d moveDir = Vector3d.ZAxis;

        private static double tol = DocumentTolerance() / 10;

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
            get { return new Guid("c12afc88-293d-46dd-9265-dea64f5840dd"); }
        }
    }
}