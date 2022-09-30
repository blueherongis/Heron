using System;
using System.Collections.Generic;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Rhino.Geometry;
using OSGeo.OSR;
using System.Windows.Forms;

namespace Heron
{
    public sealed class SetSRS : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the SetSRS class.
        /// </summary>
        public SetSRS()
          : base("Set Spatial Reference System", "SRS",
              "Set the spatial reference system to be used by Heron SRS-enabled components.   Heron defaults to 'WGS84'.  " +
              "Recompute the Grasshopper definition to ensure Heron SRS-enabled components are updated.", "Heron",
              "GIS Tools")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("SRS", "SRS", "A string describing the SRS for use with Heron SRS-enabled components.  " +
                "This can be a well-known SRS such as 'WGS84' " +
                "or an EPSG code such as 'EPSG:4326 or be in WKT format.", GH_ParamAccess.item);
            pManager[0].Optional = true;
            Message = HeronSRS.Instance.SRS;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Well Known Text", "WKT", "Well Know Text (WKT) of the SRS which has been set for Heron components.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///GDAL setup
            //Heron.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();
            //Heron.GdalConfiguration.ConfigureGdal();


            string heronSRSstring = HeronSRS.Instance.SRS;
            DA.GetData(0, ref heronSRSstring);

            if (!String.IsNullOrEmpty(heronSRSstring))
            {
                OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
                heronSRS.SetFromUserInput(heronSRSstring);
                heronSRS.ExportToPrettyWkt(out string wkt, 0);

                try
                {
                    int sourceSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
                    Message = "EPSG:" + sourceSRSInt;
                }
                catch
                {
                }

                if (heronSRS.Validate() == 1  || string.IsNullOrEmpty(wkt))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid SRS.");
                    return;
                }

                else
                {
                    if (string.Equals(HeronSRS.Instance.SRS, heronSRSstring))
                    {
                        DA.SetData(0, wkt);                    
                        return;
                    }
                    HeronSRS.Instance.SRS = heronSRSstring;
                    DA.SetData(0, wkt);
                }
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Please enter a valid string.");
            }
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.eap;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("0d50a9bf-4b17-448c-82b9-920cfbe3a75d"); }
        }
    }
}