using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Heron
{
    public class MultiSDiff : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the MultiSDiff class.
        /// </summary>
        public MultiSDiff()
          : base("Multi SDiff", "MSDiff",
              "This multithreaded boolean solid difference (SDiff) component spreads the branches of input over threads for the boolean operation. " +
                "Any failed difference breps will be discarded to the Bad Breps output.  " +
                "An example use would be to differnce shapes from panels where each panel and the shapes to be cut are on the same relative branches in a tree.  " +
                "Of the available threads, one thread is always reserved for the GUI.",
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
            pManager.AddBrepParameter("Solids", "S", "Solid breps from which to difference.", GH_ParamAccess.tree);
            pManager.AddBrepParameter("Diffs", "D", "Solid breps of which to remove.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Results", "R", "Results of boolean difference operation.", GH_ParamAccess.tree);
            pManager.AddBrepParameter("Bad Breps", "BB", "Differnce breps that failed in the boolean operation", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Brep> sBreps = new GH_Structure<GH_Brep>();
            DA.GetDataTree<GH_Brep>(0, out sBreps);

            GH_Structure<GH_Brep> dBreps = new GH_Structure<GH_Brep>();
            DA.GetDataTree<GH_Brep>(1, out dBreps);

            double tol = DocumentTolerance();

            ///Reserve one processor for GUI
            int totalMaxConcurrancy = System.Environment.ProcessorCount - 1;

            ///Tells us how many threads were using
            Message = totalMaxConcurrancy + " threads";

            ///Declare dictionaries that work in parallel to hold the successful boolean results and
            ///the unsuccessful boolean cutters
            var mainBrepsMT = new System.Collections.Concurrent.ConcurrentDictionary<GH_Path, GH_Brep>();
            var badBrepsMT = new System.Collections.Concurrent.ConcurrentDictionary<GH_Path, List<GH_Brep>>();

            ///Start of the parallel engine
            ///Cast to GH_Brep to Brep and back in parallel engine to avoid speed hit when casting all at once later
            System.Threading.Tasks.Parallel.ForEach(sBreps.Paths, new System.Threading.Tasks.ParallelOptions
            { MaxDegreeOfParallelism = totalMaxConcurrancy },
              pth =>
              {

                  List<GH_Brep> badBrep = new List<GH_Brep>();

                  Brep mainBrep = new Brep();
                  GH_Convert.ToBrep(sBreps.get_Branch(pth)[0], ref mainBrep, 0);
                  List<Brep> diffBreps = new List<Brep>();
                  foreach (var d_GH in dBreps.get_Branch(pth))
                  {
                      Brep d_Rhino = new Brep();
                      GH_Convert.ToBrep(d_GH, ref d_Rhino, 0);
                      diffBreps.Add(d_Rhino);
                  }
                  

                  ///Difference one cutter brep at a time from the main brep in the branch.
                  ///This allows the boolean operation to continue without failing
                  ///and bad cutter breps can be discarded to a list that can be used for troubleshooting
                  ///haven't noticed a hit big hit on performance
                  foreach (Brep b in diffBreps)
                  {
                      Brep[] breps = new Brep[] { };
                      breps = Brep.CreateBooleanDifference(mainBrep, b, tol);
                      if ((breps == null) || (breps.Length < 1))
                      {
                          badBrep.Add(new GH_Brep(b));
                      }
                      else
                      {
                          mainBrep = breps[0];
                      }
                  }
                  mainBrepsMT[pth] = new GH_Brep(mainBrep);
                  badBrepsMT[pth] = badBrep;
              });
            ///End of the parallel engine
            ///

            //convert dictionaries to regular old data trees
            GH_Structure<GH_Brep> mainBreps = new GH_Structure<GH_Brep>();
            GH_Structure<GH_Brep> badBreps = new GH_Structure<GH_Brep>();

            foreach (KeyValuePair<GH_Path, GH_Brep> p in mainBrepsMT)
            {
                mainBreps.Append(p.Value, p.Key);
            }

            foreach (KeyValuePair<GH_Path, List<GH_Brep>> b in badBrepsMT)
            {
                badBreps.AppendRange(b.Value, b.Key);
            }

            DA.SetDataTree(0, mainBreps);
            DA.SetDataTree(1, badBreps);

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
            get { return new Guid("94a1165e-7fed-45a7-8c08-449bea06d503"); }
        }
    }
}