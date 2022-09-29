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
    public class HeadlessDoc : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the VisualCenter class.
        /// </summary>
        public HeadlessDoc()
          : base("Headless Doc", "HD",
              "Make a new instance of a headless doc for working in Compute.", "Utilities")
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
            pManager.AddBooleanParameter("Create", "C", "Create new headless doc", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Units", "U", "Unit system of the doc", GH_ParamAccess.item);

            pManager[0].Optional = false;
            pManager[1].Optional = false;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Output", GH_ParamAccess.item);
        }

        public static RhinoDoc headlessDoc { get; private set;}

        public static bool useHeadless { get; private set; }
        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool create = false;
            int units = 0;

            if(!DA.GetData("Create", ref create)) return;
            if (!DA.GetData("Units", ref units)) return;

            if (create)
            {
                headlessDoc = Rhino.RhinoDoc.Create(null);
                headlessDoc.AdjustModelUnitSystem((Rhino.UnitSystem)units, false);
                string message = "new headless doc created in " + headlessDoc.ModelUnitSystem.ToString() + " with model absolute tolerance of " + headlessDoc.ModelAbsoluteTolerance.ToString();
                DA.SetData(0, message);
            }
            useHeadless = true;
            
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
            get { return new Guid("7091613c-6a6d-426a-9931-ca4a1e9610f4"); }
        }
    }
}