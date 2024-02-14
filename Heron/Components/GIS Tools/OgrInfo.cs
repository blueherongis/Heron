using System;
using System.Collections.Generic;
using System.Runtime;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Grasshopper.Kernel;
using Rhino.Geometry;

using OSGeo.GDAL;
using OSGeo.OSR;
using OSGeo.OGR;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Heron
{
    public class OgrInfo : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the GdalTranslate class.
        /// </summary>
        public OgrInfo()
          : base("Ogr Info", "OI",
              "The OgrInfo program lists various information about a GDAL supported vector dataset. " +
                "More information about OgrInfo options can be found at https://gdal.org/programs/ogrinfo.html.",
              "GIS Tools")
        {
        }

        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.tertiary; }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Source dataset", "S", "File location for the source vector dataset.", GH_ParamAccess.item);
            pManager.AddTextParameter("Options", "O", "String of options with a space separating each term. " +
                "The default options are set to list a summary of all layers.", GH_ParamAccess.item, "-ro -al -geom=NO -so");
            pManager[1].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Source Info", "I", "List of information about the source dataset.", GH_ParamAccess.list);
            pManager.AddTextParameter("Source Info JSON", "J", "List of information about the source dataset in JSON format.", GH_ParamAccess.item);
            pManager.AddCurveParameter("Source Extents", "E", "Extents of layers in the source dataset in the layer's coordinate system.", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string datasourceFileLocation = string.Empty;
            DA.GetData<string>(0, ref datasourceFileLocation);

            string options = string.Empty;
            DA.GetData<string>(1, ref options);

            var re = new System.Text.RegularExpressions.Regex("(?<=\")[^\"]*(?=\")|[^\" ]+");
            string[] infoOptions = re.Matches(options).Cast<Match>().Select(m => m.Value).ToArray();

            string optionsJson = options + " -json";
            string[] infoOptionsJson = re.Matches(optionsJson).Cast<Match>().Select(m => m.Value).ToArray();

            string datasourceInfoJson = string.Empty;

            Heron.GdalConfiguration.ConfigureGdal();
            OSGeo.GDAL.Gdal.AllRegister();

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Look for more information about options at:");
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "https://gdal.org/programs/ogrinfo.html");

            List<string> pvs = new List<string>();
            List<Curve> extentsCurves = new List<Curve>();

            if (!string.IsNullOrEmpty(datasourceFileLocation))
            {
                using (Dataset datasource = Gdal.OpenEx(datasourceFileLocation,0, null,null,null))
                {
                    if (datasource == null)
                    {
                        throw new Exception("Can't open GDAL dataset: " + datasourceFileLocation);
                    }

                    datasourceInfoJson = Gdal.GDALVectorInfo(datasource, new GDALVectorInfoOptions(infoOptionsJson.ToArray()));
                    DA.SetData(1, datasourceInfoJson);

                    using (JsonDocument doc = JsonDocument.Parse(datasourceInfoJson))
                    {
                        JsonElement root = doc.RootElement;
                        var layers = root.GetProperty("layers").EnumerateArray();

                        pvs.Add("Description: " + root.GetProperty("description").GetString());
                        pvs.Add("Driver: " + root.GetProperty("driverLongName").GetString());
                        pvs.Add("Layer Count: " + layers.Count());

                        foreach (var layer in layers)
                        {
                            ///Layer info
                            pvs.Add("--------------------");
                            if(layer.TryGetProperty("name", out var layerName))
                            pvs.Add("Layer Name: " + layerName.GetString());

                            if(layer.TryGetProperty("featureCount", out var featureCount))
                            pvs.Add("Feature Count: " + featureCount.GetInt64());

                            ///Fields info
                            if (layer.TryGetProperty("fields", out var fieldArray))
                            {
                                var fields = fieldArray.EnumerateArray();
                                pvs.Add("Fields Count: " + fields.Count());
                                pvs.Add(" ");

                                pvs.Add("Fields: ");
                                foreach (var field in fields)
                                {
                                    if (field.TryGetProperty("name", out var fieldName))
                                    pvs.Add("  " + fieldName.GetString());
                                }
                            }

                            ///Geometry info
                            var geometryFields = layer.GetProperty("geometryFields").EnumerateArray();
                            pvs.Add(" ");
                            foreach (var gField in geometryFields)
                            {
                                pvs.Add("Geometry Type: " + gField.GetProperty("type").GetString());

                                if (gField.TryGetProperty("extent", out var extents))
                                {
                                    var lowerleft = new Point3d(extents[0].GetDouble(), extents[1].GetDouble(), 0.0);
                                    var upperright = new Point3d(extents[2].GetDouble(), extents[3].GetDouble(), 0.0);
                                    pvs.Add("Geometry Extents: "
                                        + "(" + lowerleft.X + "," + lowerleft.Y + ")"
                                        + "(" + upperright.X + "," + upperright.Y + ")");

                                    Rectangle3d ext = new Rectangle3d(Plane.WorldXY, lowerleft, upperright);
                                    extentsCurves.Add(ext.ToNurbsCurve());
                                }

                                if (gField.TryGetProperty("coordinateSystem", out var coordSys))
                                {
                                    if (coordSys.TryGetProperty("projjson", out var projjson))
                                    {
                                        if (projjson.TryGetProperty("type", out var projtype))
                                        {
                                            pvs.Add("Coordinate System Type: " + projtype.GetString());
                                        }

                                        pvs.Add("Coordinate Sytem Authority & Code:");
                                        if(projjson.TryGetProperty("id", out var projId))
                                        {
                                            if (projId.TryGetProperty("authority", out var auth) && projId.TryGetProperty("code", out var code))
                                            {
                                                pvs.Add(auth.GetString() + ":" + code.GetInt64().ToString());
                                            }

                                            
                                        }
                                    }
                                    pvs.Add("Coordinate System WKT:");
                                    if(coordSys.TryGetProperty("wkt", out var wkt))
                                    {
                                        pvs.Add(wkt.GetString());
                                    }
                                }
                            }
                        }
                    }

                    datasource.Dispose();
                }
            }

            DA.SetDataList(0, pvs);
            DA.SetDataList(2, extentsCurves);
        }


        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.vector;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("6E98DCEB-578F-4026-ACE4-1F9B3D5CFA80"); }
        }
    }
}