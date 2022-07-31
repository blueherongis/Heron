using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Heron
{
    public class TopiaryFlatten : HeronComponent
    {

        public TopiaryFlatten()
          : base("Topiary Flatten", "TF", "Flatten branches by a set depth from the deepest path in a data tree. " +
                "The resulting tree will look more like a topiary. This can be useful for data trees with uneven path depths.", "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Data Tree", "DT", "Data tree to flatten to a topiary.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Number of Branches", "N", "The number of branches to merge from the deepest path branch count.  " +
                "For instance, if N=2 and the path with the most branches is 4, any path in the tree with a depth greater than 2 will be flattened up into 2.", GH_ParamAccess.item);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Pruned Tree", "PT", "Pruned tree.", GH_ParamAccess.tree);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<IGH_Goo> treeIn = new GH_Structure<IGH_Goo>();
            DA.GetDataTree<IGH_Goo>(0, out treeIn);

            int pruneDepth = 0;
            DA.GetData<int>(1, ref pruneDepth);

            GH_Structure<IGH_Goo> treeOut = new GH_Structure<IGH_Goo>();

            ///Create list of path strings
            var pathStrings = treeIn.Paths.Select(x => x.ToString());
            ///Find the deepest path in the tree
            var maxDepthPath = pathStrings.Aggregate((max, cur) => max.Split(';').Length > cur.Split(';').Length ? max : cur);
            ///Get number of branches for deepest path
            var maxDepthInt = maxDepthPath.Split(';').Length;

            foreach (var path in treeIn.Paths)
            {
                ///Determine number of branches to prune if any
                GH_Path.SplitPathLikeString(path.ToString(), out string[] path_segments, out string index_segment);
                var numBranchesToRemove = path_segments.Length - (maxDepthInt - pruneDepth);
                
                var newPath = path;

                if (numBranchesToRemove > 0 && maxDepthInt - pruneDepth > 0)
                {
                    ///Remove pruned branches from path string
                    path_segments = path_segments.Take(path_segments.Count() - numBranchesToRemove).ToArray();
                    int[] path_args = path_segments.Select(int.Parse).ToArray();

                    newPath = new GH_Path(path_args);
                }

                treeOut.AppendRange(treeIn[path],newPath);
            }

            DA.SetDataTree(0, treeOut);
        }


        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.shp;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("a3499421-1bcb-4877-9c68-4afca606c3f7"); }
        }
    }
}