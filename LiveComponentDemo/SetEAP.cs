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

using Newtonsoft.Json.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Serialization;

namespace Heron
{
    public class SetEAP : GH_Component
    {
        //Class Constructor
        public SetEAP() : base("Set EarthAnchorPoint","SetEAP","Set the Rhino EarthAnchorPoint","Heron","GIS Tools")
        { 
        
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Set EAP", "set", "Set the EarthAnchorPoint", GH_ParamAccess.item, false);
            pManager.AddNumberParameter("Latitude", "LAT", "Decimal Degree Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Decimal Degree Longitude", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("EAP", "EAP", "EarthAnchorPoint Longitude/Latitude", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double lat = -1;
            double lon = -1;
            bool EAP = false;
            
            DA.GetData<bool>("Set EAP", ref EAP);
            DA.GetData<double>("Latitude", ref lat);
            DA.GetData<double>("Longitude", ref lon);

            if (EAP == true)
            {
                Rhino.RhinoApp.RunScript("_EarthAnchorPoint L " + lat.ToString() + " o " + lon.ToString() + " _Enter _Enter _Enter _Enter _Enter _Enter _Enter", false);
            }

            DA.SetData("EAP", "Longitude: "+ Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLongitude.ToString() + 
                " / Latitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLatitude.ToString());
           
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.eap;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{6577DC68-200C-4B3C-ADB4-78DE61D76870}"); }
        }
    }
}
