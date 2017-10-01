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
using OSGeo.GDAL;
using OSGeo.OSR;
using OSGeo.OGR;

namespace Heron
{
    public class OSM : GH_Component 
    {
        public OSM() : base("OSM","OSM","Import OSM files","Heron","GIS Tools")
        {

    }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
{
    pManager.AddTextParameter("OSM file", "OSMfile", "File location of *.osm", GH_ParamAccess.item);

}
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("o", "o", "output", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Ogr.RegisterAll();
            DataSource ds = Ogr.Open(@"C:\Users\bwashburn\Desktop\GH Tests\REST\osm\sf2.osm", 0);
            DA.SetData(0, "0");
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
            get { return new Guid("{6F672F36-2EB9-40AD-89F3-1BA94B57C026}"); }
        }
    }
}
