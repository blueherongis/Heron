using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using LASzip.Net;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;

namespace Heron
{
    public class ImportLAZ : HeronBoxPreviewComponent
    {
        ///Based on code from laszip.net
        ///https://github.com/shintadono/laszip.net/blob/master/Examples/TestLasZipCS/Program.cs

        /// <summary>
        /// Initializes a new instance of the ImportLAS class.
        /// </summary>
        public ImportLAZ()
          : base("Import LAZ", "ImportLAZ",
              "Import LAS & LAZ files.",
              "GIS Import | Export")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("LAS/LAZ Point File", "filePath", "File location of the LAS/LAZ source.", GH_ParamAccess.item);
            pManager.AddBrepParameter("Clipping Boundary", "boundary", "Bounding Brep converted to a boundary box for filtering points.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Preview Point Size", "pointSize", "Point size for the preview point cloud", GH_ParamAccess.item, 4);
            pManager.AddBooleanParameter("Run", "run", "", GH_ParamAccess.item, false);
            pManager[1].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Info", "Info", "Inforamtion about the point cloud.", GH_ParamAccess.list);
            pManager.AddPointParameter("Points", "Points", "Points from source file separated into classifications.", GH_ParamAccess.tree);
            pManager.AddColourParameter("Colors", "Colors", "Colors associated with points.", GH_ParamAccess.tree);
            pManager.AddTextParameter("Point Cloud SRS", "SRS", "Spatial reference system (SRS) of the point cloud if provided in the header data.", GH_ParamAccess.item);
            pManager.AddBoxParameter("Extents", "Extents", "Bounding box of the point cloud.", GH_ParamAccess.item);
            pManager.HideParameter(1);
            pManager.HideParameter(4);
        }


        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string filename = string.Empty;
            DA.GetData<string>(0, ref filename);

            bool webSource = false;
            string tempPath = Path.GetTempPath();
            if (filename.StartsWith("http")) webSource = true;

            if (!File.Exists(filename) && !webSource)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Cannot find " + filename);
                return;
            }

            Brep boundary = new Brep();
            DA.GetData<Brep>(1, ref boundary);

            bool filter = boundary.IsValid;
            BoundingBox bbox = boundary.GetBoundingBox(true);

            double pointSize = 4;
            DA.GetData<double>(2, ref pointSize);

            bool run = false;
            DA.GetData<bool>(3, ref run);

            List<string> info = new List<string>();

            var lazReader = new laszip();
            var compressed = true;
            lazReader.exploit_spatial_index(true);
            lazReader.request_compatibility_mode(true);

            if (webSource)
            {
                using (var webClient = new WebClient())
                {
                    string tempFile = Path.Combine(tempPath, Path.GetFileName(filename));
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    webClient.DownloadFile(new Uri(filename), tempFile);
                    webClient.Dispose();
                    lazReader.open_reader(tempFile, out compressed);
                }
            }

            else
            {
                lazReader.open_reader(filename, out compressed);
            }

            var numberOfPoints = lazReader.header.number_of_point_records;
            if (numberOfPoints < 1 && lazReader.header.extended_number_of_point_records > 0)
            {
                numberOfPoints = (uint)lazReader.header.extended_number_of_point_records;
            }

            int numReturns = 0;
            int pointFormat = lazReader.header.point_data_format;
            List<string> classificationDetails = new List<string>();

            ///Add more detail if desired about returns
            foreach(var r in lazReader.header.number_of_points_by_return)
            {
                if (r > 0)
                {
                    numReturns++;
                }
            }
            foreach(var re in lazReader.header.extended_number_of_points_by_return)
            {
                if (re > 0)
                {            
                    numReturns++;
                }
            }
            
            List<laszip_vlr> vlrs = lazReader.header.vlrs;
            double version = Double.Parse(lazReader.header.version_major + "." + lazReader.header.version_minor);

            ///According to v1.3 and v1.4 spec, color depth should be stored as 16 bit, not 8 bit, 
            ///so RGB color values need to be corrected by dividing by 256 to get values GH_Colour can use.
            int colorDepthCorrection = 1;
            if (version > 1.2) colorDepthCorrection = 256;

            info.Add("Points: " + numberOfPoints.ToString("N0"));
            info.Add("Returns: " + numReturns);
            info.Add("File source ID: " + lazReader.header.file_source_ID);
            info.Add("LAS/LAZ Version: " + version);
            info.Add("Created on: " + lazReader.header.file_creation_day + " day of " + lazReader.header.file_creation_year);
            info.Add("Created with: " + Encoding.Default.GetString(lazReader.header.generating_software));

            ///Try to fetch SRS of point cloud 
            string pcSRS = "Data does not have associated spatial reference system (SRS).";

            if (vlrs != null)
            {
                foreach (var vlr in vlrs)
                {
                    string description = Encoding.Default.GetString(vlr.description);
                    if (description.Contains("SRS") || description.Contains("WKT"))
                    {
                        pcSRS = Encoding.Default.GetString(vlr.data);
                    }
                    info.Add("VLR: " + Encoding.Default.GetString(vlr.data));
                }
            }

            if (lazReader.evlrs != null)
            {
                foreach (var evlr in lazReader.evlrs)
                {
                    string description = Encoding.Default.GetString(evlr.description);
                    if (description.Contains("SRS") || description.Contains("WKT"))
                    {
                        pcSRS = Encoding.Default.GetString(evlr.data);
                    }
                    info.Add("EVLR: " + Encoding.Default.GetString(evlr.data));
                }
            }

            Point3d min = new Point3d(lazReader.header.min_x, lazReader.header.min_y, lazReader.header.min_z);
            Point3d max = new Point3d(lazReader.header.max_x, lazReader.header.max_y, lazReader.header.max_z);
            BoundingBox maxBox = new BoundingBox(min, max);
            AddPreviewItem(maxBox);

            info.Add("Bounding box: {" + min.ToString() + "} to {" + max.ToString() +"}");



            Message = numberOfPoints.ToString("N0") + " points";

            ///Report if there is spatial indexing
            lazReader.has_spatial_index(out bool is_indexed, out bool is_appended);
            if (is_indexed)
            {
                info.Add("File has spatial indexing");
            }
            else { info.Add("File does not have spatial indexing"); }

            PointCloud pointCloud = new PointCloud();
            GH_Structure<GH_Point> ghPC = new GH_Structure<GH_Point>();
            GH_Structure<GH_Colour> ghColors = new GH_Structure<GH_Colour>();

            ///Faster to add points to a Rhino point cloud as a list than as individual points
            List<Point3d> laszipPointList = new List<Point3d>();
            List<Color> laszipColorList = new List<Color>();

            if (run)
            {
                var coordArray = new double[3];
                int pointCounter = 0;
                bool is_done = false;
                double x_offset = lazReader.header.x_offset;
                double y_offset = lazReader.header.y_offset;
                double z_offset = lazReader.header.z_offset;
                double x_scale = lazReader.header.x_scale_factor;
                double y_scale = lazReader.header.y_scale_factor;
                double z_scale = lazReader.header.z_scale_factor;

                if (filter)
                {
                    var intersectionBox = Rhino.Geometry.BoundingBox.Intersection(bbox, maxBox);
                    lazReader.inside_rectangle(bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y, out bool is_empty);

                    if (!intersectionBox.IsValid || is_empty)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping Boundary is outside the bounds of the point cloud.");
                    }
                    else
                    {

                        for (int pointIndex = 0; pointIndex < numberOfPoints; pointIndex++)
                        {
                            ///Read the point
                            //lazReader.read_point();

                            ///Read the point within the boundary
                            lazReader.read_inside_point(out is_done);
                            if (is_done) break;

                            ///Get precision coordinates
                            lazReader.get_coordinates(coordArray);
                            Point3d pt = new Point3d(coordArray[0], coordArray[1], coordArray[2]);

                            if (bbox.Contains(pt))
                            {
                            ///Get classification value for sorting into branches
                            int classification = lazReader.point.classification;
                            GH_Path path = new GH_Path(classification);

                            GH_Colour col = new GH_Colour(Color.FromArgb(
                                lazReader.point.rgb[0] / colorDepthCorrection,
                                lazReader.point.rgb[1] / colorDepthCorrection,
                                lazReader.point.rgb[2] / colorDepthCorrection));

                            ghPC.Append(new GH_Point(pt), new GH_Path(classification));
                            ghColors.Append(col, new GH_Path(classification));

                            laszipPointList.Add(pt);
                            laszipColorList.Add(col.Value);

                            pointCounter++;
                            //if (is_done) break;
                            }
                        }
                        

                        /*
                        laszip_point point = lazReader.get_point_pointer();

                        while (pointCounter < numberOfPoints)
                        {
                            ///Read the point within the boundary
                            lazReader.read_inside_point(out is_done);
                            if (is_done) break;
                            Point3d pt = new Point3d(point.X, point.Y, point.Z);
                            ghPC.Append(new GH_Point(pt));
                            pointCounter++;
                        }
                        */

                        Message = pointCounter.ToString("N0") + " of " + numberOfPoints.ToString("N0") + " points";

                    }
                }

                else
                {
                    
                    for (int pointIndex = 0; pointIndex < numberOfPoints; pointIndex++)
                    {
                        ///Read the point
                        lazReader.read_point();

                        laszip_point point = lazReader.get_point_pointer();

                        ///Get precision coordinates
                        lazReader.get_coordinates(coordArray);
                        Point3d pt = new Point3d(coordArray[0], coordArray[1], coordArray[2]);

                        ///Get classification value for sorting into branches
                        int classification = lazReader.point.classification; 
                        GH_Path path = new GH_Path(classification);

                        GH_Colour col = new GH_Colour(Color.FromArgb(
                            lazReader.point.rgb[0] / colorDepthCorrection,
                            lazReader.point.rgb[1] / colorDepthCorrection,
                            lazReader.point.rgb[2] / colorDepthCorrection));

                        ghPC.Append(new GH_Point(pt), new GH_Path(classification));
                        ghColors.Append(col, new GH_Path(classification));

                        laszipPointList.Add(pt);
                        laszipColorList.Add(col.Value);                      
                    }
                }

                pointCloud.AddRange(laszipPointList, laszipColorList);
                AddPreviewItem(pointCloud, pointSize);
                _box = maxBox;
                //ExpirePreview(true);
                
                for (int i = 0; i < ghPC.Branches.Count; i++)
                {
                    var branchID = ghPC.Paths[i].Indices[0];
                    classificationDetails.Add("[" + branchID + "] " + ClassificationMeaning(branchID, pointFormat) + " points: " + ghPC.Branches[i].Count);
                }

                info.Add("-----Return Points By Classification-----");
                info.AddRange(classificationDetails);
                info.Add("-----------------------------------------");
            }

            lazReader.close_reader();

            DA.SetDataList(0, info);
            DA.SetDataTree(1, ghPC);
            DA.SetDataTree(2, ghColors);
            DA.SetData(3, pcSRS);
            DA.SetData(4, maxBox);
        }


        public static string ClassificationMeaning(int classification, int pointFormat)
        {
            string meaning = string.Empty;
            if (0 <= pointFormat && pointFormat <= 5)
            {
                switch(classification)
                {
                    case 0:
                        meaning = "Created, Never Classified";
                        break;
                    case 1:
                        meaning = "Unclassified";
                        break;
                    case 2:
                        meaning = "Ground";
                        break;
                    case 3:
                        meaning = "Low Vegetation";
                        break;
                    case 4:
                        meaning = "Medium Vegetation";
                        break;
                    case 5:
                        meaning = "High Vegetation";
                        break;
                    case 6:
                        meaning = "Building";
                        break;
                    case 7:
                        meaning = "Low Point (Noise)";
                        break;
                    case 8:
                        meaning = "Model Key-Point (Mass Point)";
                        break;
                    case 9:
                        meaning = "Water";
                        break;
                    case int x when (x == 10 || x == 11):
                        meaning = "Reserved for ASPRS Definition";
                        break;
                    case 12:
                        meaning = "Overlap Points";
                        break;
                    case int x when (13 >= x && x <= 31):
                        meaning = "Reserved for ASPRS Definition";
                        break;
                    default:
                        meaning = "[Not in ASPRS Standar Point Classes]";
                        break;
                }
            }
            if (6 <= pointFormat && pointFormat <= 10)
            {
                switch (classification)
                {
                    case 0:
                        meaning = "Created, Never Classified";
                        break;
                    case 1:
                        meaning = "Unclassified";
                        break;
                    case 2:
                        meaning = "Ground";
                        break;
                    case 3:
                        meaning = "Low Vegetation";
                        break;
                    case 4:
                        meaning = "Medium Vegetation";
                        break;
                    case 5:
                        meaning = "High Vegetation";
                        break;
                    case 6:
                        meaning = "Building";
                        break;
                    case 7:
                        meaning = "Low Point (Noise)";
                        break;
                    case 8:
                        meaning = "Reserved";
                        break;
                    case 9:
                        meaning = "Water";
                        break;
                    case 10:
                        meaning = "Rail";
                        break;
                    case 11:
                        meaning = "Road Surface";
                        break;
                    case 12:
                        meaning = "Reserved";
                        break;
                    case 13:
                        meaning = "Wire - Guard (Shield)";
                        break;
                    case 14:
                        meaning = "Wire - Conductor (Phase)";
                        break;
                    case 15:
                        meaning = "Transmission Tower";
                        break;
                    case 16:
                        meaning = "Wire - Structure Connector";
                        break;
                    case 17:
                        meaning = "Bridge Deck";
                        break;
                    case 18:
                        meaning = "High Noise";
                        break;
                    case 19:
                        meaning = "Overhead Structure";
                        break;
                    case 20:
                        meaning = "Ignored Ground";
                        break;
                    case 21:
                        meaning = "Snow";
                        break;
                    case 22:
                        meaning = "Temporal Exclusion";
                        break;
                    case int x when (23 >= x && x <= 63):
                        meaning = "Reserved";
                        break;
                    case int x when (64 >= x && x <= 255):
                        meaning = "User Definable";
                        break;
                    default:
                        meaning = "[Not in ASPRS Standar Point Classes]";
                        break;
                }
            }
            return meaning;
        }

        /// <summary>
        /// Set the clipping box so that preview works properly
        /// https://discourse.mcneel.com/t/overwriting-drawviewportwires-in-c-plugin-shows-nothing/198198/2
        /// </summary>
        private BoundingBox _box = BoundingBox.Empty;
        public override BoundingBox ClippingBox
        {
            get
            {
                if (_box.IsValid)
                    return _box;
                return BoundingBox.Empty;
            }
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
                return Properties.Resources.shp;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("A07E48C1-5D2E-411A-BDCD-94A559522DF8"); }
        }
    }
}