using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.Collections.Generic;

namespace Heron
{
    public class DMStoDD : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the DMStoDD class.
        /// </summary>
        public DMStoDD()
          : base("DMStoDD", "DMS to DD",
              "Convert Latitude and Longitude coordinates formatted in Degree Mintue Seconds (DMS) to Decimal Degree (DD) format." +
                "Valid Degree Minute Second formats are: 79°58′36″W | 079:56:55W | 079d 58′ 36″ W | 079 58 36.0 | 079 58 36.4 E",
              "GIS Tools")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Latitude", "LAT", "Latitude in Degree Minute Second (DMS) format", GH_ParamAccess.item);
            pManager.AddTextParameter("Longitude", "LON", "Longitude in Degree Minute Second (DMS) format", GH_ParamAccess.item);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Latitude", "LAT", "Latitude in Decimal Degree (DD) format", GH_ParamAccess.item);
            pManager.AddTextParameter("Longitude", "LON", "Longitude in Decimal Degree (DD) format", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string latString = string.Empty;
            string lonString = string.Empty;
            double lat = Double.NaN;
            double lon = Double.NaN;

            DA.GetData<string>("Latitude", ref latString);
            DA.GetData<string>("Longitude", ref lonString);

            lat = Heron.Convert.DMStoDDLat(latString);
            lon = Heron.Convert.DMStoDDLon(lonString);

            if (!Double.IsNaN(lat) && Double.IsNaN(lon))
            {
                DA.SetData("Latitude", lat);
                return;
            }

            else if (Double.IsNaN(lat) && !Double.IsNaN(lon))
            {
                DA.SetData("Longitude", lon);
            }

            else if (Double.IsNaN(lat) && Double.IsNaN(lon))
            {
                return;
            }

            else
            {
                DA.SetData("Latitude", lat);
                DA.SetData("Longitude", lon);
            }
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.ddtoxy;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("570bd625-a7c6-4ec5-a6bc-a4b6ad70e528"); }
        }
    }
}