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
            ///Custom configuration options
            OSGeo.GDAL.Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES");
            ///To be backwards compatible with the way Heron uses coordinate order
            OSGeo.GDAL.Gdal.SetConfigOption("OSR_DEFAULT_AXIS_MAPPING_STRATEGY", "TRADITIONAL_GIS_ORDER"); //or use AUTHORITY_COMPLIANT

        }
    }
}
