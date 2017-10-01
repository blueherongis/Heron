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
    public class DDtoXY : GH_Component
    {
        //Class Constructor
        public DDtoXY() : base("Decimal Degrees to XY","DDtoXY","Convert Decimal Degrees Longitude/Latitude to X/Y","Heron","GIS Tools")
        { 
        
        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "LAT", "Decimal Degree Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Decimal Degree Longitude", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("xyPoint", "xyPoint", "Longitude/Latitude translated to X/Y", GH_ParamAccess.item);
            
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double lat = -1;
            double lon = -1;
            DA.GetData<double>("Latitude", ref lat);
            DA.GetData<double>("Longitude", ref lon);


            EarthAnchorPoint eap = new EarthAnchorPoint();
            eap = Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint;
            Rhino.UnitSystem us = new Rhino.UnitSystem();
            Transform xf = eap.GetModelToEarthTransform(us);

            //http://www.grasshopper3d.com/forum/topics/matrix-datatype-in-rhinocommon
            //Thanks Andrew

            Transform Inversexf = new Transform();
            xf.TryGetInverse(out Inversexf);
            Point3d ptMod = new Point3d(lon, lat, 0);
            ptMod = Inversexf * ptMod;
            DA.SetData("xyPoint", ptMod);
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.ddtoxy;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{78543216-14B5-422C-85F8-BB575FBED3D2}"); }
        }
    }
}
