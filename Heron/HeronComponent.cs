using Grasshopper.Kernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace Heron
{
    public abstract class HeronComponent : GH_Component
    {
        public HeronComponent(string name, string nickName, string description, string subCategory) : base(name, nickName, description, "Heron", subCategory)
        {

        }
    }
}
