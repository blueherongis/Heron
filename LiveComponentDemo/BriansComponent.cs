using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using GH_IO;

namespace RESTful
{
    public class BriansComponent : GH_Component
    {
        //Class Constructor
        public BriansComponent() : base("Brian's Amazing Component","BAC","This is a demo","nbbj","GIS REST")
        { 
        
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("My String", "Sb", "Here's an input string", GH_ParamAccess.item,"Brian's Default String");
            pManager[0].Optional = true;
            pManager.AddNumberParameter("Number List", "N", "A list of numbers", GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("My Output String", "Ob", "Here's what comes out", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string inputString = "";
            List<double> numberList = new List<double>();


            string outputString = "";

            DA.GetData<string>("My String", ref inputString);
            if (!DA.GetDataList<double>("Number List", numberList))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "You need to provide a list for this component to do anything");
                return;
            }
            outputString = inputString + " Hello world";

            DA.SetData("My Output String", outputString);
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.Demo;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{5A8E405A-3E26-4E30-B80F-5F30C6D0EA97}"); }
        }
    }
}
