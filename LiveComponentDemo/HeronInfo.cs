using System;
using System.Collections.Generic;
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
                return "0.1.0.0";
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
    }
}
