using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;
using GH_IO;
using GH_IO.Serialization;

namespace RESTful
{
    public class MeshFromPolyline : GH_Component
    {
        //Class Constructor
        public MeshFromPolyline() : base("Mesh From Closed Polyline","MFCP","Create a mesh from a closed polyline","nbbj","GIS Tools")
        { 
        
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Polyline", "Polyline", "Closed polylines", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh", "Mesh", "Meshes from closed polylines", GH_ParamAccess.item);
            
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            PolylineCurve polyc = null;
            DA.GetData<PolylineCurve>("Polyline", ref polyc);
            
            DA.SetData("Mesh", Mesh.CreateFromClosedPolyline(polyc.ToPolyline(-1,-1,0,0,0,0,0,0,false)));
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
            get { return new Guid("{B6926909-F749-41EC-B480-7A903E82AEB5}"); }
        }
    }
}
