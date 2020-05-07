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
    public class DDtoXY : HeronComponent
    {
        //Class Constructor
        public DDtoXY() : base("Decimal Degrees to XY", "DDtoXY", "Convert Decimal Degrees Longitude/Latitude to X/Y", "GIS Tools")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Latitude", "LAT", "Decimal Degree Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Decimal Degree Longitude", GH_ParamAccess.item);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("xyPoint", "xyPoint", "Longitude/Latitude translated to X/Y", GH_ParamAccess.item);
            pManager.AddTransformParameter("Transform", "xForm", "The transform from WGS to XYZ", GH_ParamAccess.item);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///Dump out the transform first
            DA.SetData("Transform", Heron.Convert.WGSToWorldTransform());

            /// Then, we need to retrieve all data from the input parameters.
            /// We'll start by declaring variables and assigning them starting values.
            double lat = -1;
            double lon = -1;

            /// Then we need to access the input parameters individually. 
            /// When data cannot be extracted from a parameter, we should abort this method.
            if (!DA.GetData("Latitude", ref lat)) return;
            if (!DA.GetData("Longitude", ref lon)) return;

            /// We should now validate the data and warn the user if invalid data is supplied.
            if (lat < -90.0 || lat > 90.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Latitude should be between -90.0 deg and 90.0 deg");
                return;
            }
            if (lon < -180.0 || lon > 180.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Longitude should be between -180.0 deg and 180.0 deg");
                return;
            }

            /// Finally assign the point to the output parameter.
            DA.SetData("xyPoint", Heron.Convert.WGSToWorld(new Point3d(lon, lat, 0)));
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
