using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Heron
{
    public class HexToColor : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the ColorToHex class.
        /// </summary>
        public HexToColor()
          : base("Hex to Color", "H2C", "Convert a hexidecimal color to RGBA format.", "Utilities")
        {
        }
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.quarternary; }
        }


        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("colorHexidecmial", "colorHex", "Hexidecimal color converted from RGBA", GH_ParamAccess.item);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddColourParameter("colorRGBA", "colorRGBA", "RGBA color to convert to hexidecimal format", GH_ParamAccess.item);

        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            //Color color = Color.Empty;
            string hex = string.Empty;
            DA.GetData<string>(0, ref hex);
            //string hex = String.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.R, color.G, color.B, color.A);
            Regex reg = new Regex("^#(?:[0-9a-fA-F]{3,4}){1,2}$");
            
            if (!reg.IsMatch(hex))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "colorHex input string is not in a hexidecimal format.");
                return;
            }

            ///Parsing hex string from Stack Overflow
            ///https://stackoverflow.com/questions/2109756/how-do-i-get-the-color-from-a-hexadecimal-color-code-using-net?rq=1
            
            hex = hex.TrimStart('#');

            Color color; 
            if (hex.Length == 6)
                color = Color.FromArgb(255, // hardcoded opaque
                            int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                            int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                            int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
            else 
                color = Color.FromArgb(
                            int.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber),
                            int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                            int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                            int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
            
            DA.SetData(0, color);
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                //You can add image files to your project resources and access them like this:
                // return Resources.IconForThisComponent;
                return Properties.Resources.raster;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("190F4F13-49C0-41F9-88BA-B13D2317F4BD"); }
        }
    }
}