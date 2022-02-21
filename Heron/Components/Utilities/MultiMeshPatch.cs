using System;
using System.Collections.Generic;
using System.Windows.Forms;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace Heron
{
    public class MultiMeshPatch : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MultiMeshPatch class.
        /// </summary>
        public MultiMeshPatch()
          : base("Multi Mesh Patch", "MMPatch",
              "Multithreaded creation of mesh patches from planar polylines. The first polyine in a branch will be considered the outer boundary, " +
                "any others will be considered holes and should be completely within the outer boundary.",
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
            pManager.AddCurveParameter("Polylines", "polylines", "Polylines from which to generate mesh patches.  The first polyine in a branch will be considered the outer boundary, " +
                "any others will be considered holes and should be completely within the outer boundary.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Extrude Height", "extrude", "Height to extrude mesh patches.", GH_ParamAccess.tree);
            pManager[1].Optional = true;
            Message = extrudeDir + "\n(" + totalMaxConcurrancy + " threads)";

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh Patches", "meshes", "Meshes resulting from mesh patches.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Curve> crvs = new GH_Structure<GH_Curve>();
            DA.GetDataTree<GH_Curve>(0, out crvs);

            GH_Structure<GH_Number> height = new GH_Structure<GH_Number>();
            DA.GetDataTree<GH_Number>(1, out height);

            double tol = DocumentTolerance();

            //reserve one processor for GUI

            //create a dictionary that works in parallel
            var mPatchTree = new System.Collections.Concurrent.ConcurrentDictionary<GH_Path, GH_Mesh>();

            //Multi-threading the loop
            System.Threading.Tasks.Parallel.ForEach(crvs.Paths,
              new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = totalMaxConcurrancy },
              pth =>
              {
                  List<Curve> branchCrvs = new List<Curve>();
                  double offset = 0;
                  if (crvs.get_Branch(pth).Count > 0)
                  {
                      foreach (var ghCrv in crvs.get_Branch(pth))
                      {
                          Curve c = null;
                          GH_Convert.ToCurve(ghCrv, ref c, 0);

                          if (extrudeDir == "Extrude Z")
                          {
                              ///Ensure boundary winds clockwise
                              if (c.ClosedCurveOrientation(Vector3d.ZAxis) < 0)
                              {
                                  c.Reverse();
                              }
                          }
                          
                          branchCrvs.Add(c);
                      }

                      ///Convert first curve in branch to polyline
                      ///Don't know why the boundary parameter can't be a Curve if the holes are allowed to be Curves
                      Polyline pL = null;
                      branchCrvs[0].TryGetPolyline(out pL);
                      branchCrvs.RemoveAt(0);
                      if (!pL.IsClosed) { pL.Add(pL[0]); }

                      ///Check validity of pL
                      if (!pL.IsValid)
                      {
                          AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Outer boundary curve could not be converted to polyline or is invalid");
                      }

                      ///The magic found here:
                      ///https://discourse.mcneel.com/t/mesh-with-holes-from-polylines-in-rhinowip-to-c/45589
                      Mesh mPatch = Mesh.CreatePatch(pL, tol, null, branchCrvs, null, null, true, 1);
                      mPatch.Ngons.AddPlanarNgons(tol);
                      //mPatch.UnifyNormals();
                      mPatch.FaceNormals.ComputeFaceNormals();
                      mPatch.Normals.ComputeNormals();
                      mPatch.Compact();

                      if (height.PathExists(pth))
                      {
                          if (height.get_Branch(pth).Count > 0)
                          {
                              GH_Convert.ToDouble(height.get_Branch(pth)[0], out offset, 0);
                              if (extrudeDir == "Extrude Z") { mPatch = mPatch.Offset(offset, true, Vector3d.ZAxis); }
                              else 
                              {
                                  mPatch.Flip(true, true, true);
                                  mPatch = mPatch.Offset(offset, true); 
                              }
                          }
                      }
                      else if (height.get_FirstItem(true) != null)
                      {
                          GH_Convert.ToDouble(height.get_FirstItem(true), out offset, 0);
                          if (extrudeDir == "Extrude Z") { mPatch = mPatch.Offset(offset, true, Vector3d.ZAxis); }
                          else 
                          {
                              mPatch.Flip(true, true, true);
                              mPatch = mPatch.Offset(offset, true); 
                          }
                      }
                      else
                      {

                      }

                      if (mPatch != null)
                      {
                          if (mPatch.SolidOrientation() < 0) { mPatch.Flip(true, true, true); }
                      }

                      mPatchTree[pth] = new GH_Mesh(mPatch);
                  }

              });
            ///End of multi-threaded loop


            ///Convert dictionary to regular old data tree
            GH_Structure<GH_Mesh> mTree = new GH_Structure<GH_Mesh>();
            foreach (KeyValuePair<GH_Path, GH_Mesh> m in mPatchTree)
            {
                mTree.Append(m.Value, m.Key);
            }

            DA.SetDataTree(0, mTree);
        }

        ///Reserve one processor for GUI
        public static int totalMaxConcurrancy = System.Environment.ProcessorCount - 1;

        /// Add menu items for extrusion selection
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem exZ = new ToolStripMenuItem("Extrude Z");
            exZ.Tag = "Extrude Z";
            exZ.ToolTipText = "Extrude in the positive World Z axis.";
            exZ.Checked = IsItemSelected("Extrude Z");
            exZ.Click += Menu_DoClick;

            ToolStripMenuItem exN = new ToolStripMenuItem("Extrude Normal");
            exN.Tag = "Extrude Normal";
            exN.ToolTipText = "Extrude normal to the mesh faces in the patch.";
            exN.Checked = IsItemSelected("Extrude Normal");
            exN.Click += Menu_DoClick;

            //base.AppendAdditionalComponentMenuItems(menu);
            menu.Items.Add(exZ);
            menu.Items.Add(exN);
        }

        private void Menu_DoClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string code = (string)item.Tag;
            if (IsItemSelected(code))
                return;

            RecordUndoEvent("ExtrudeDir");

            extrudeDir = code;
            Message = extrudeDir + "\n(" + totalMaxConcurrancy + " threads)";


            ExpireSolution(true);
        }

        private string extrudeDir = "Extrude Z";

        private bool IsItemSelected(string eZ)
        {
            return eZ.Equals(extrudeDir);
        }

        public string ExtrudeDir
        {
            get { return extrudeDir; }
            set
            {
                extrudeDir = value;
            }
        }


        /// Sticky items
        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("ExtrudeDir", ExtrudeDir);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            ExtrudeDir = reader.GetString("ExtrudeDir");
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
            get { return new Guid("7bc480af-4f21-4b55-8ba7-36e3f2082d7f"); }
        }
    }
}