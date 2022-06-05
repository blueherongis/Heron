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
    public class SetEAP_DEPRECATED20220416 : HeronComponent
    {
        //Class Constructor
        public SetEAP_DEPRECATED20220416() : base("Set EarthAnchorPoint", "SetEAP", "Set the Rhino EarthAnchorPoint", "GIS Tools")
        {

        }

        ///Retiring this component to add point of interest and DMS formatted lat/lon as input 
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.hidden; }
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Set EAP", "set", "Set the EarthAnchorPoint", GH_ParamAccess.item, false);
            pManager.AddNumberParameter("Latitude", "LAT", "Decimal Degree Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Decimal Degree Longitude", GH_ParamAccess.item);
            pManager[1].Optional = true;
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Earth Anchor Point", "EAP", "EarthAnchorPoint Longitude/Latitude", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double lat = -1;
            double lon = -1;
            bool EAP = false;
            string lonlatString = string.Empty;

            //check if EAP has been set and if so what is it
            if (!Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthLocationIsSet())
            {
                lonlatString = "The Earth Anchor Point has not been set yet";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "EAP has not been set yet");
            }

            else lonlatString = "Longitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLongitude.ToString() +
                " / Latitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLatitude.ToString();

            DA.GetData<bool>("Set EAP", ref EAP);
            DA.GetData<double>("Latitude", ref lat);
            DA.GetData<double>("Longitude", ref lon);

            if (EAP == true)
            {
                EarthAnchorPoint ePt = new EarthAnchorPoint();
                ePt.EarthBasepointLatitude = lat;
                ePt.EarthBasepointLongitude = lon;

                //set new EAP
                Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint = ePt;

                //new EAP to string for output
                lonlatString = "Longitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLongitude.ToString() +
                " / Latitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLatitude.ToString();
            }


            DA.SetData("Earth Anchor Point", lonlatString);
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
