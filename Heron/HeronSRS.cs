using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Heron
{
    /// <summary>
    /// https://discourse.mcneel.com/t/defining-plugin-wide-variables-for-grasshopper-plugin/67582/2
    /// </summary>
    public sealed class HeronSRS
    {
        private static HeronSRS _instance;
        private HeronSRS() { } // private constructor, should only access through Instance. Any necessary initialization here, too.
        public static HeronSRS Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new HeronSRS();

                return _instance;
            }
        }

        public string SRS { get; set; } = "WGS84"; // initialize to min value of int. Can use any valid value here. Or init in the private constructor.
    }
}