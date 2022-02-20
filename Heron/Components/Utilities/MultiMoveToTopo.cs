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
    public class MultiMoveToTopo : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MultiMoveToMesh class.
        /// </summary>
        public MultiMoveToTopo()
          : base("Multi Move to Topo", "MMoveToTopo",
              "Move breps, surfaces, meshes, polylines and points to a topography mesh.  Breps and closed meshes will be moved to the lowest point on the topography mesh within their footprint. " +
                "Vertexes of curves and open meshes and control points of surfaces will be moved to the topography mesh." +
                "Geometry on a branch will be moved together as a group, but can be moved independently by deselecting 'Group' from the component menu." +
                "For a slower, but more detailed projection where curves and open meshes take on the vertexes of the topography mesh, " +
                "select 'Detailed' from the component menu.",
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
            pManager.AddMeshParameter("Topography Mesh", "topoMesh", "Mesh representing the topography to which to move objects.", GH_ParamAccess.item);
            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry to move to topography.  Geometry can be points, polylines, meshes, surfaces or breps.  " +
                "Open meshes will be flattened to topography.", GH_ParamAccess.tree);
            if (fast) { Message = "Fast"; }
            else { Message = "Detailed"; }
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
            DA.GetData<Mesh>("Topography Mesh", ref topoMesh);

            GH_Structure<IGH_GeometricGoo> featureGoo = new GH_Structure<IGH_GeometricGoo>();
            DA.GetDataTree<IGH_GeometricGoo>("Feature Geometry", out featureGoo);

            ///Reserve one processor for GUI
            totalMaxConcurrancy = System.Environment.ProcessorCount - 1;

            ///Tells us how many threads were using
            //Message = totalMaxConcurrancy + " threads";


            ///Get Rtree of points and flat mesh for processing in detailed feature mesh projection
            Mesh topoFlat = new Mesh();
            Point3d[] topoFlatPoints = null;
            RTree rTree = new RTree();
            if (!fast)
            {
                topoFlat = topoMesh.DuplicateMesh();
                for (int i = 0; i < topoFlat.Vertices.Count; i++)
                {
                    Point3f v = topoFlat.Vertices[i];
                    v.Z = 0;
                    topoFlat.Vertices.SetVertex(i, v);
                }

                topoFlatPoints = topoFlat.Vertices.ToPoint3dArray();
                rTree = RTree.CreateFromPointArray(topoFlatPoints);
            }

            ///Create a dictionary that works in parallel
            var gooTree = new System.Collections.Concurrent.ConcurrentDictionary<GH_Path, List<IGH_GeometricGoo>>();

            ///Multi-threading the loop
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
                  Surface surface = null;
                  Brep brep = new Brep();

                  ///Output container list
                  List<IGH_GeometricGoo> gGooList = new List<IGH_GeometricGoo>();
                  List<IGH_GeometricGoo> fGooList = new List<IGH_GeometricGoo>();
                  var branchFeatures = featureGoo.get_Branch(pth);
                  BoundingBox branchBox = new BoundingBox();

                  if (branchFeatures.Count > 0)
                  {
                      foreach (var bGoo in branchFeatures)
                      {

                          ///Get geometry type(s) in branch
                          string geomType = string.Empty;
                          IGH_GeometricGoo fGoo = GH_Convert.ToGeometricGoo(bGoo);

                          if (fGoo != null && fGoo.IsValid)
                          {
                              if (grouped)
                              {
                                  ///Need to duplicate geometry or else move vector piles on similar to the following
                                  ///https://www.grasshopper3d.com/forum/topics/c-component-refresh-problem
                                  fGooList.Add(fGoo.DuplicateGeometry());
                                  branchBox.Union(fGoo.Boundingbox);
                              }

                              geomType = fGoo.TypeName;

                              switch (geomType)
                              {
                                  case "Point":
                                      fGoo.CastTo<Point3d>(out pt);
                                      gGooList.Add(ProjectPointToTopo(topoMesh, pt));
                                      break;

                                  case "Line":
                                  case "Polyline":
                                      fGoo.CastTo<Polyline>(out pLine);

                                      if (fast) { gGooList.Add(ProjectPolylineToTopo(topoMesh, pLine)); }
                                      else
                                      {
                                          ///Lock topoMesh so it's not accessed by mutliple threads at once in a "deadlock"
                                          ///https://docs.microsoft.com/en-us/dotnet/standard/threading/managed-threading-best-practices
                                          lock (topoMesh)
                                          {
                                              gGooList.AddRange(ProjectCurveToTopo(topoMesh, pLine.ToNurbsCurve()));
                                          }
                                      }

                                      break;

                                  case "PolylineCurve":
                                      fGoo.CastTo<PolylineCurve>(out pLineCurve);
                                      if (fast) { gGooList.Add(ProjectPolylineToTopo(topoMesh, pLineCurve.ToPolyline())); }
                                      else
                                      {
                                          lock (topoMesh)
                                          {
                                              gGooList.AddRange(ProjectCurveToTopo(topoMesh, pLineCurve.ToNurbsCurve()));
                                          }
                                      }
                                      break;

                                  case "Curve":
                                      fGoo.CastTo<Curve>(out curve);
                                      if (fast)
                                      {
                                          if (curve.TryGetPolyline(out pLine)) { gGooList.Add(ProjectPolylineToTopo(topoMesh, pLine)); }
                                          else { gGooList.AddRange(ProjectCurveToTopo(topoMesh, curve)); }
                                      }
                                      else
                                      {
                                          lock (topoMesh)
                                          {
                                              gGooList.AddRange(ProjectCurveToTopo(topoMesh, curve));
                                          }
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
                                          if (fast) { gGooList.Add(ProjectMeshToTopoFast(topoMesh, mesh)); }
                                          else
                                          {
                                              lock (topoMesh)
                                              {
                                                  gGooList.Add(ProjectMeshToTopoSlow(topoMesh, topoFlat, topoFlatPoints, rTree, mesh));
                                              }
                                          }
                                      }
                                      break;

                                  case "Surface":
                                      fGoo.CastTo<Surface>(out surface);
                                      gGooList.Add(ProjectSurfaceToTopoFast(topoMesh, surface));
                                      break;

                                  case "Brep":
                                      fGoo.CastTo<Brep>(out brep);
                                      gGooList.Add(ProjectBrepToTopo(topoMesh, brep));
                                      break;

                                  default:
                                      AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Not able to move " + geomType + " geometry to mesh" +
                                          ". Geometry must be a Point, Curve, Mesh, Surface or Brep.");
                                      break;
                              }
                          }
                          else { }
                      }

                      ///Move objects in branch a minimum distance if grouped selected
                      ///If only one feature in a branch, not need to group
                      if (grouped && gGooList.Count > 1)
                      {
                          ///Get the minimum move vector
                          Point3d lowestPoint = new Point3d();
                          double minDistance = double.MaxValue;
                          Vector3d minimumVec = new Vector3d();
                          foreach (var gi in gGooList)
                          {
                              if (gi != null)
                              {
                                  Point3d gGooMin = gi.Boundingbox.Min;
                                  Vector3d distVector = gGooMin - branchBox.Min;
                                  if (distVector.Length < minDistance && distVector.Length > 0 && distVector.IsValid)
                                  {
                                      lowestPoint = gGooMin;
                                      minDistance = distVector.Length;
                                      minimumVec = new Vector3d(0, 0, distVector.Z);
                                  }
                              }

                          }

                          ///Move orignal feature geometry the minimum move vector
                          if (minDistance != double.MaxValue)
                          {
                              Transform transform = Transform.Translation(minimumVec);
                              for (int f = 0; f < fGooList.Count; f++)
                              {
                                  fGooList[f].Transform(transform);
                              }
                              gooTree[pth] = fGooList;
                          }
                      }
                      else
                      {
                          gooTree[pth] = gGooList;
                      }
                  }
              });
            ///End of multi-threaded loop


            ///Convert dictionary to regular old data tree
            GH_Structure<IGH_GeometricGoo> gTree = new GH_Structure<IGH_GeometricGoo>();
            foreach (KeyValuePair<GH_Path, List<IGH_GeometricGoo>> g in gooTree)
            {
                gTree.AppendRange(g.Value, g.Key);
            }

            topoFlat.Dispose();
            topoMesh.Dispose();

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

        public static GH_Curve ProjectPolylineToTopo(Mesh topoMesh, Polyline pLine)
        {
            GH_Curve ghCurve = new GH_Curve();


            List<Point3d> projectedPts = new List<Point3d>();
            for (int j = 0; j < pLine.Count; j++)
            {
                Ray3d ray = new Ray3d(pLine[j], moveDir);
                double t = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, ray);
                if (t >= 0.0)
                {
                    projectedPts.Add(ray.PointAt(t));
                }
                else
                {
                    Ray3d rayOpp = new Ray3d(pLine[j], -moveDir);
                    double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, rayOpp);
                    if (tOpp >= 0.0)
                    {
                        projectedPts.Add(rayOpp.PointAt(tOpp));
                    }
                    else
                    {
                        //return null;
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

        public static List<GH_Curve> ProjectCurveToTopo(Mesh topoMesh, Curve crv)
        {
            List<GH_Curve> curves = new List<GH_Curve>();
            foreach (Curve curve in Curve.ProjectToMesh(crv, topoMesh, moveDir, tol))
            {
                if (curve.IsValid)
                {
                    curves.Add(new GH_Curve(curve.ToNurbsCurve()));
                }
            }
            return curves;
        }

        public static GH_Mesh ProjectMeshToTopoFast(Mesh topoMesh, Mesh featureMesh)
        {
            GH_Mesh ghMesh = new GH_Mesh();
            Mesh mesh = featureMesh.DuplicateMesh();
            ///Move patch verts to topo
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                Ray3d ray = new Ray3d((Point3d)mesh.Vertices[i], moveDir);
                double t = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, ray);

                if (t >= 0.0)
                {
                    mesh.Vertices.SetVertex(i, (Point3f)ray.PointAt(t));
                }
                else
                {
                    Ray3d rayOpp = new Ray3d((Point3d)mesh.Vertices[i], -moveDir);
                    double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, rayOpp);
                    if (tOpp >= 0.0)
                    {
                        mesh.Vertices.SetVertex(i, (Point3f)rayOpp.PointAt(tOpp));
                    }
                    else
                    {
                        //mesh.Vertices.SetVertex(i, new Point3f(0, 0, 0));
                        //return null;
                    }
                }
            }
            GH_Convert.ToGHMesh(mesh, GH_Conversion.Primary, ref ghMesh);
            return ghMesh;
        }

        public static GH_Mesh ProjectMeshToTopoSlow(Mesh topoMesh, Mesh topoFlatMesh, Point3d[] topoFlatPoints, RTree rTree, Mesh featureMesh)
        {
            ///TODO: Look into using Mesh.SplitWithProjectedPolylines available in RhinoCommon 7.0

            GH_Mesh ghMesh = new GH_Mesh();
            Mesh[] disjointMeshes = featureMesh.SplitDisjointPieces();
            Mesh projectedDisjointMeshes = new Mesh();
            foreach (Mesh disjointMesh in disjointMeshes)
            {
                ///Flatten disjointMesh first
                for (int i = 0; i < disjointMesh.Vertices.Count; i++)
                {
                    Point3f v = disjointMesh.Vertices[i];
                    v.Z = 0;
                    disjointMesh.Vertices.SetVertex(i, v);
                }

                ///Get naked edges and sort list so that outer boundary is first
                Polyline[] nakedEdges = disjointMesh.GetNakedEdges();
                var nakedEdgesCurves = new Dictionary<Curve, double>();
                foreach (Polyline p in nakedEdges)
                {
                    Curve pNurbs = p.ToNurbsCurve();
                    nakedEdgesCurves.Add(pNurbs, AreaMassProperties.Compute(pNurbs).Area);
                }
                var nakedEdgesSorted = from pair in nakedEdgesCurves
                                       orderby pair.Value descending
                                       select pair.Key;

                BoundingBox bbox = disjointMesh.GetBoundingBox(false);

                ///Project naked edges to flat topo to add control points at mesh edge intersections
                List<Curve> trimCurves = new List<Curve>();
                foreach (Curve nakedEdge in nakedEdgesSorted)
                {
                    Curve[] projectedCurves = Curve.ProjectToMesh(nakedEdge, topoFlatMesh, moveDir, tol);
                    ///If projection of naked edge results in more than one curve, join curves back into one closed curve.  
                    ///Approximation at edge of topo is unavoidable
                    if (projectedCurves.Length > 0)
                    {
                        if (projectedCurves.Length > 1)
                        {
                            ///Collector polyline to combine projectedCurves
                            Polyline projBoundary = new Polyline();
                            ///Add individual curves from Curve.ProjectToMesh together by converting to Polyline and appending the vetexes together
                            foreach (Curve c in projectedCurves)
                            {
                                Polyline tempPolyline = new Polyline();
                                c.TryGetPolyline(out tempPolyline);
                                projBoundary.AddRange(tempPolyline);
                            }
                            ///Make sure polyine is closed
                            projBoundary.Add(projBoundary[0]);
                            trimCurves.Add(projBoundary.ToNurbsCurve());
                        }
                        else if (!projectedCurves[0].IsClosed)
                        {
                            Line closer = new Line(projectedCurves[0].PointAtEnd, projectedCurves[0].PointAtStart);
                            Curve closedProjectedCurve = Curve.JoinCurves(new Curve[] { projectedCurves[0], closer.ToNurbsCurve() })[0];
                            trimCurves.Add(closedProjectedCurve);
                        }
                        else
                        {
                            trimCurves.Add(projectedCurves[0]);
                        }
                    }
                    else
                    {
                        ///Projection missed the topoFlatMesh
                        return null;
                    }
                } ///End projected naked edges

                Polyline pL = new Polyline();
                trimCurves[0].TryGetPolyline(out pL);
                trimCurves.RemoveAt(0);

                ///Add points from topoMeshFlat to array used for Mesh.CreatePatch
                List<Point3f> bboxPoints = new List<Point3f>();

                ///Try RTree method. RTree of topoFlatPoints
                ///https://discourse.mcneel.com/t/rtree-bounding-box-search/96282/6

                var boxSearchData = new BoxSearchData();
                rTree.Search(bbox, BoundingBoxCallback, boxSearchData);

                foreach (int id in boxSearchData.Ids)
                {
                    Ray3d ray = new Ray3d((Point3d)topoFlatPoints[id], -moveDir);
                    double t = Rhino.Geometry.Intersect.Intersection.MeshRay(disjointMesh, ray);
                    if (t >= 0.0)
                    {
                        bboxPoints.Add((Point3f)ray.PointAt(t));
                    }
                    else
                    {
                        Ray3d rayOpp = new Ray3d((Point3d)topoFlatPoints[id], moveDir);
                        double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(disjointMesh, rayOpp);
                        if (tOpp >= 0.0)
                        {
                            bboxPoints.Add((Point3f)rayOpp.PointAt(tOpp));
                        }
                        else
                        {
                            //return null;
                        }
                    }

                }

                ///A hack way of adding points to newVerts which is needed in the form of an array for Mesh.CreatePatch
                disjointMesh.Vertices.AddVertices(bboxPoints);
                Point3d[] newVerts = disjointMesh.Vertices.ToPoint3dArray();

                ///Create patch
                Mesh mPatch = Mesh.CreatePatch(pL, tol, null, trimCurves, null, newVerts, true, 1);

                ///Move patch verts to topo
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
                ///Combine disjoint meshes
                if (mPatch.IsValid)
                {
                    projectedDisjointMeshes.Append(mPatch);
                    mPatch.Dispose();
                }
                else { return null; }

            }
            projectedDisjointMeshes.Ngons.AddPlanarNgons(tol);
            projectedDisjointMeshes.Compact();
            GH_Convert.ToGHMesh(projectedDisjointMeshes, GH_Conversion.Primary, ref ghMesh);
            return ghMesh;

        }

        static void BoundingBoxCallback(object sender, RTreeEventArgs e)
        {
            var boxSearchData = e.Tag as BoxSearchData;
            boxSearchData.Ids.Add(e.Id);
        }

        public class BoxSearchData
        {
            public BoxSearchData()
            {
                Ids = new List<int>();
            }
            public List<int> Ids { get; set; }
        }

        public static GH_Surface ProjectSurfaceToTopoFast(Mesh topoMesh, Surface featureSurface)
        {
            GH_Surface ghSurface = new GH_Surface();
            NurbsSurface surface = featureSurface.ToNurbsSurface();

            ///Move patch verts to topo
            for (int u = 0; u < surface.Points.CountU; u++)
            {
                for (int v = 0; v < surface.Points.CountV; v++)
                {
                    Point3d controlPt;
                    surface.Points.GetPoint(u, v, out controlPt);
                    Ray3d ray = new Ray3d(controlPt, moveDir);
                    double t = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, ray);

                    if (t >= 0.0)
                    {
                        surface.Points.SetPoint(u, v, ray.PointAt(t));
                    }
                    else
                    {
                        Ray3d rayOpp = new Ray3d(controlPt, -moveDir);
                        double tOpp = Rhino.Geometry.Intersect.Intersection.MeshRay(topoMesh, rayOpp);
                        if (tOpp >= 0.0)
                        {
                            surface.Points.SetPoint(u, v, rayOpp.PointAt(t));
                        }
                        else
                        {
                            //return null;
                        }
                    }
                }

            }
            if (surface.IsValid)
            {
                GH_Convert.ToGHSurface(surface, GH_Conversion.Primary, ref ghSurface);
                return ghSurface;
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

            ///transalte mesh to project up with move vector and save to output array
            Vector3d moveV = GetMinProjectedPointToMesh(lowestVerts, topoMesh);
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

            ///Get list of verteces from mesh to project
            List<Point3d> originalVerts = new List<Point3d>();
            foreach (var v in brep.Vertices)
            {
                originalVerts.Add(v.Location);
            }

            ///create a list of verteces who share the lowest Z value of the bounding box
            List<Point3d> lowestVerts = originalVerts.Where(lowPoint => lowPoint.Z == bb.Min.Z).ToList();

            ///transalte mesh to project up with move vector and save to output array
            Vector3d moveV = GetMinProjectedPointToMesh(lowestVerts, topoMesh);
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

        public static Vector3d GetMinProjectedPointToMesh(List<Point3d> points, Mesh topoMesh)
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

        private int totalMaxConcurrancy = 1;
        private static Vector3d moveDir = Vector3d.ZAxis;
        private static double tol = DocumentTolerance();
        private bool fast = true;
        private bool grouped = true;

        public bool Fast
        {
            get { return fast; }
            set
            {
                fast = value;
                if ((!fast))
                {
                    Message = "Detailed";
                }
                else
                {
                    Message = "Fast";
                }
            }
        }

        public bool Grouped
        {
            get { return grouped; }
            set
            {
                grouped = value;
            }
        }
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem item = Menu_AppendItem(menu, "Detailed", Menu_FastChecked, true, !Fast);
            item.ToolTipText = "If 'fast' is selected, vetexes of polylines and open meshes are moved to the topo mesh, " +
                "otherwise they take on the vertexes of the topo mesh which can take significantly longer for larger topo meshes.";
            ToolStripMenuItem groupedItem = Menu_AppendItem(menu, "Grouped", Menu_GroupedChecked, true, Grouped);
            groupedItem.ToolTipText = "If 'grouped' is selected, all objects in a branch will be moved the same minimum distance to the topo mesh.";
        }

        private void Menu_FastChecked(object sender, EventArgs e)
        {
            RecordUndoEvent("Fast");
            Fast = !Fast;
            ExpireSolution(true);
        }
        private void Menu_GroupedChecked(object sender, EventArgs e)
        {
            RecordUndoEvent("Grouped");
            Grouped = !Grouped;
            ExpireSolution(true);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean("Fast", Fast);
            writer.SetBoolean("Grouped", Grouped);
            return base.Write(writer);
        }
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            Fast = reader.GetBoolean("Fast");
            Grouped = reader.GetBoolean("Grouped");
            return base.Read(reader);
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
            get { return new Guid("c12afc88-293d-46dd-9265-dea64f5840dd"); }
        }
    }
}