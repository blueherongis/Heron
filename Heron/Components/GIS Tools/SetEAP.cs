using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;
using System;
using System.Linq;

namespace Heron
{
    public class SetEAP : HeronComponent
    {
        //Class Constructor
        public SetEAP() : base("Set EarthAnchorPoint", "SetEAP", "Set the Rhino EarthAnchorPoint using either an address or Lat/Lon coordinates", "GIS Tools")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Set EAP", "set", "Set the EarthAnchorPoint", GH_ParamAccess.item, false);
            pManager.AddTextParameter("Point of Interest", "poi", "Point of interest or address with which to set the EarthAnchorPoint. " +
                "If a point of interest or address is supplied, this component will query the ESRI geolocation service and set the EAP to the first coordinates in the list of candidates.", GH_ParamAccess.item);
            pManager[1].Optional = true;
            pManager.AddTextParameter("Latitude", "LAT", "Latitude in either Decimal Degree (DD) or Degree Minute Second (DMS) format", GH_ParamAccess.item);
            pManager[2].Optional = true;
            pManager.AddTextParameter("Longitude", "LON", "Longitude in either Decimal Degree (DD) or Degree Minute Second (DMS) format", GH_ParamAccess.item);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Earth Anchor Point", "EAP", "EarthAnchorPoint Longitude/Latitude", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string latString = string.Empty;
            string lonString = string.Empty;
            double lat = Double.NaN;
            double lon = Double.NaN;
            bool EAP = false;
            string address = string.Empty;
            string lonlatString = string.Empty;
            string addressString = string.Empty;

            DA.GetData<bool>("Set EAP", ref EAP);
            DA.GetData<string>("Point of Interest", ref address);
            DA.GetData<string>("Latitude", ref latString);
            DA.GetData<string>("Longitude", ref lonString);



            if (EAP == true)
            {
                EarthAnchorPoint ePt = new EarthAnchorPoint();

                lat = Heron.Convert.DMStoDDLat(latString);
                lon = Heron.Convert.DMStoDDLon(lonString);

                if (Double.IsNaN(lat) && !string.IsNullOrEmpty(latString))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Latitude value is invalid. Please enter value in valid Decimal Degree format (-79.976666) " +
                        "or valid Degree Minute Second format (79°58′36″W | 079:56:55W | 079d 58′ 36″ W | 079 58 36.0 | 079 58 36.4 E)");
                    return;
                }

                if (Double.IsNaN(lon) && !string.IsNullOrEmpty(lonString))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Longitude value is invalid. Please enter value in valid Decimal Degree format (40.446388) " +
                        "or valid Degree Minute Second format (40°26′47″N | 40:26:46N | 40d 26m 47s N | 40 26 47.1 | 40 26 47.4141 N)");
                    return;
                }

                if (!string.IsNullOrEmpty(address) && string.IsNullOrEmpty(latString) && string.IsNullOrEmpty(lonString))
                {
                    string output = Heron.Convert.HttpToJson("https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/findAddressCandidates?Address=" + address + "&f=pjson");
                    JObject ja = JObject.Parse(output);

                    if (ja["candidates"].Count() < 1)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Cadidate location found for this address");
                        DA.SetData("Earth Anchor Point", lonlatString);
                        return;
                    }
                    else
                    {
                        if (ja["candidates"][0]["score"].Value<int>() > 99)
                        {
                            addressString = "EAP set to the following address: " + ja["candidates"][0]["address"].ToString() + "\r\n";
                            ePt.EarthBasepointLatitude = (double)ja["candidates"][0]["location"]["y"];
                            ePt.EarthBasepointLongitude = (double)ja["candidates"][0]["location"]["x"];
                        }
                    }
                }

                else
                {
                    if (!Double.IsNaN(lat) && !Double.IsNaN(lon))
                    {
                        ePt.EarthBasepointLatitude = lat;
                        ePt.EarthBasepointLongitude = lon;
                    }
                }
                
                if ((ePt.EarthBasepointLatitude > -90) && (ePt.EarthBasepointLatitude < 90) && (ePt.EarthBasepointLongitude > -180) && (ePt.EarthBasepointLongitude < 180))
                {
                    //set new EAP
                    Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint = ePt;
                }

            }

            //check if EAP has been set and if so what is it
            if (!Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthLocationIsSet())
            {
                lonlatString = "The Earth Anchor Point has not been set yet";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "EAP has not been set yet");
            }

            else lonlatString = "Longitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLongitude.ToString() +
                " / Latitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLatitude.ToString();

            DA.SetData("Earth Anchor Point", lonlatString);

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
            get { return new Guid("3A9B1B9D-9DED-4B5B-9101-ED57F5239EC8"); }
        }
    }
}
