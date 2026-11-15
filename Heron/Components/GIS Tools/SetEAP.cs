using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;
using System;
using System.Linq;
using OSGeo.OSR;
using OSGeo.OGR;

namespace Heron
{
    public class SetEAP : HeronComponent
    {
        // Class Constructor
        public SetEAP() : base("Set EarthAnchorPoint", "SetEAP", "Set the Rhino EarthAnchorPoint using either an address or Lat/Lon coordinates", "GIS Tools")
        {

        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Set EAP", "set", "Set the EarthAnchorPoint", GH_ParamAccess.item, false);
            pManager.AddTextParameter("Point of Interest", "poi", "Point of interest or address with which to set the EarthAnchorPoint. " +
                "If a point of interest or address is supplied, this component will query the ESRI geolocation service and set the EAP to the first coordinates in the list of candidates.  " +
                "If the Point of Interest starts with \"EPSG:\" or \"epsg:\", the component will set the EAP to the origin of the specified coordinate system.", GH_ParamAccess.item);
            pManager[1].Optional = true;
            pManager.AddGenericParameter("Latitude", "LAT", "Latitude as a number (full double precision) or text in either Decimal Degree (DD) or Degree Minute Second (DMS) format", GH_ParamAccess.item);
            pManager[2].Optional = true;
            pManager.AddGenericParameter("Longitude", "LON", "Longitude as a number (full double precision) or text in either Decimal Degree (DD) or Degree Minute Second (DMS) format", GH_ParamAccess.item);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Earth Anchor Point", "EAP", "EarthAnchorPoint Longitude/Latitude", GH_ParamAccess.item);
            pManager.AddNumberParameter("Latitude", "LAT", "Latitude of the EarthAnchorPoint", GH_ParamAccess.item);
            pManager.AddNumberParameter("Longitude", "LON", "Longitude of the EarthAnchorPoint", GH_ParamAccess.item);
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

            double latNum = Double.NaN;
            double lonNum = Double.NaN;
            latString = GetCoordinate(DA, "Latitude", out latNum);
            lonString = GetCoordinate(DA, "Longitude", out lonNum);            

            if (EAP == true)
            {
                EarthAnchorPoint ePt = new EarthAnchorPoint();

                lat = !Double.IsNaN(latNum) ? latNum : Heron.Convert.DMStoDDLat(latString);
                lon = !Double.IsNaN(lonNum) ? lonNum : Heron.Convert.DMStoDDLon(lonString);

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

                // If the user has provided a Point of Interest that starts with "EPSG:" or "epsg:", we will parse the EPSG code and use it to transform the origin (0,0)
                // of that coordinate system to WGS84 coordinates, which will then be used to set the EAP.
                if (!string.IsNullOrEmpty(address) && (address.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) || address.StartsWith("epsg:", StringComparison.OrdinalIgnoreCase)))
                {
                    // GDAL setup
                    Heron.GdalConfiguration.ConfigureOgr();

                    // Parse EPSG code from Point of Interest input
                    string[] parts = address.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int epsgCode))
                    {
                        try
                        {
                            // Transform origin (0, 0) of the coordinate system to WGS84
                            var sourceCoords = new double[] { 0, 0 }; // Origin of source coordinate system
                            var targetSRS = new SpatialReference(null);
                            targetSRS.ImportFromEPSG(4326);
                            var sourceSRS = new SpatialReference(null);
                            sourceSRS.ImportFromEPSG(epsgCode);
                            using (var coordTransform = new OSGeo.OSR.CoordinateTransformation(sourceSRS, targetSRS))
                            {
                                coordTransform.TransformPoint(sourceCoords);
                                //var targetCoords = coordTransform.TransformPoint(sourceCoords);
                                ePt.EarthBasepointLatitude = sourceCoords[1];
                                ePt.EarthBasepointLongitude = sourceCoords[0];
                            }
                            addressString = $"EAP set to origin of EPSG:{epsgCode} in WGS84\r\n";
                        }
                        catch (Exception ex)
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Failed to transform EPSG:{epsgCode} to WGS84: {ex.Message}");
                            return;
                        }
                    }
                    else
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid EPSG code format. Please use 'EPSG:XXXX' where XXXX is the coordinate system code.");
                        return;
                    }
                }
                else if (!string.IsNullOrEmpty(address) && string.IsNullOrEmpty(latString) && string.IsNullOrEmpty(lonString))
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

            else
            {
                lonlatString = "Longitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLongitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " / Latitude: " + Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLatitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }

            DA.SetData("Earth Anchor Point", lonlatString);
            DA.SetData("Latitude", Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLatitude);
            DA.SetData("Longitude", Rhino.RhinoDoc.ActiveDoc.EarthAnchorPoint.EarthBasepointLongitude);

        }

        // Numbers are used as-is (no text round trip); anything else is passed on as text for DD/DMS parsing.
        private static string GetCoordinate(IGH_DataAccess DA, string name, out double number)
        {
            number = Double.NaN;
            Grasshopper.Kernel.Types.IGH_Goo goo = null;
            if (!DA.GetData(name, ref goo) || goo == null) return string.Empty;
            double d;
            if (goo is Grasshopper.Kernel.Types.GH_Number || goo is Grasshopper.Kernel.Types.GH_Integer)
            {
                if (goo.CastTo(out d)) number = d;
                return string.Empty;
            }
            return goo.ToString();
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
            get { return new Guid("4C6994AD-B4EA-47A7-ACE3-BA8C5DFF66BB"); }
        }
    }
}
