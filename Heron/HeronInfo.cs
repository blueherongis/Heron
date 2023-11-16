using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;

namespace Heron
{
    public class HeronInfo : GH_AssemblyInfo
    {
        public override string Name
        {
            get
            {
                return "Heron";
            }
        }
        public override string AuthorName
        {
            get
            {
                return "Brian Washburn";
            }
        }

        public override string Version
        {
            get
            {
                return "0.4.2-beta.4";
            }
        }

        public override string AuthorContact
        {
            get
            {
                return "blueheronGIS@gmail.com";
            }
        }

        public override Guid Id
        {
            get
            {
                return new System.Guid("{94830583-1656-43FB-8415-6FD290548DD1}");
            }
        }

        public override Bitmap Icon
        {
            get
            {
                return Properties.Resources.heron_favicon;
            }
        }
    }

    /// <summary>
    /// https://discourse.mcneel.com/t/add-a-custom-icon-image-to-grasshopper-plugin-tabs/61777/14
    /// </summary>
    public class HeronCategoryIcon : Grasshopper.Kernel.GH_AssemblyPriority
    {
        public override Grasshopper.Kernel.GH_LoadingInstruction PriorityLoad()
        {
            Grasshopper.Instances.ComponentServer.AddCategoryIcon("Heron", Properties.Resources.heron_favicon);
            Grasshopper.Instances.ComponentServer.AddCategorySymbolName("Heron", 'H');
            return Grasshopper.Kernel.GH_LoadingInstruction.Proceed;
        }
    }
}
