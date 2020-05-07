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
    public class XYtoDD : HeronComponent
    {
        //Class Constructor
        public XYtoDD() : base("XY to Decimal Degrees", "XYtoDD", "Convert X/Y to Decimal Degrees Longitude/Latitude", "GIS Tools")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("xyPoint", "xyPoint", "Point to translate to Longitude/Latitude", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "LAT", "Decimal Degree Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Decimal Degree Longitude", GH_ParamAccess.item);
            pManager.AddTransformParameter("Transform", "xForm", "The transform from XYZ to WGS", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///Dump out the transform first
            DA.SetData("Transform", Heron.Convert.WorldToWGSTransform());

            /// First, we need to retrieve all data from the input parameters.
            /// We'll start by declaring variables and assigning them starting values.
            Point3d xyPt = new Point3d();

            /// When data cannot be extracted from a parameter, we should abort this method.
            if (!DA.GetData<Point3d>("xyPoint", ref xyPt)) return;

            /// Finally assign the output parameters.
            DA.SetData("Latitude", Heron.Convert.WorldToWGS(xyPt).Y);
            DA.SetData("Longitude", Heron.Convert.WorldToWGS(xyPt).X);
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.xytodd;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{0B461B47-632B-4145-AA06-157AAFAC1DDA}"); }
        }
    }
}
