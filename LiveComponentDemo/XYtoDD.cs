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
    public class XYtoDD : GH_Component
    {
        //Class Constructor
        public XYtoDD() : base("XY to Decimal Degrees","XYtoDD","Convert X/Y to Decimal Degrees Longitude/Latitude","Heron","GIS Tools")
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
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Point3d xyPt = new Point3d();
            DA.GetData<Point3d>("xyPoint", ref xyPt);
            DA.SetData("Latitude", ConvertToWSG(xyPt).Y);
            DA.SetData("Longitude", ConvertToWSG(xyPt).X);
        }

        public static Point3d ConvertToWSG(Point3d xyz)
        {
            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);
            xyz = xyz * Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, UnitSystem.Meters);
            Point3d ptON = new Point3d(xyz.X, xyz.Y, xyz.Z);
            ptON = xf * ptON;
            return ptON;
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
