using Grasshopper.Kernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
//using Gdal = OSGeo.GDAL.Gdal;
//using Ogr = OSGeo.OGR.Ogr;

namespace Heron
{
    public abstract class HeronComponent : GH_Component
    {
        public HeronComponent(string name, string nickName, string description, string subCategory) : base(name, nickName, description, "Heron", subCategory)
        {
        }
    }
}
