using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

using LASzip.Net;
using System.Net;

namespace Heron
{
    public class ImportLAZ_DEPRECATED20250120_OBSOLETE : HeronBoxPreviewComponent
    {
        ///Based on code from laszip.net
        ///https://github.com/shintadono/laszip.net/blob/master/Examples/TestLasZipCS/Program.cs

        /// <summary>
        /// Initializes a new instance of the ImportLAS class.
        /// </summary>
        public ImportLAZ_DEPRECATED20250120_OBSOLETE()
          : base("Import LAZ", "ImportLAZ",
              "Import LAS & LAZ files.",
              "GIS Import | Export")
        {
        }

        ///Retiring this component to add Run toggle, create point cloud preview (not just points),
        public override Grasshopper.Kernel.GH_Exposure Exposure
        {
            get { return GH_Exposure.hidden; }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("LAS/LAZ Point File", "filePath", "File location of the LAS/LAZ source.", GH_ParamAccess.item);
            pManager.AddBrepParameter("Clipping Boundary", "boundary", "Bounding Brep converted to a boundary box for filtering points.", GH_ParamAccess.item);
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
            pManager.HideParameter(1);
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

            bool filter = (boundary.IsValid);
            BoundingBox bbox = boundary.GetBoundingBox(true);
            if (filter) AddPreviewItem(bbox);

            List<string> info = new List<string>();

            var lazReader = new laszip();
            var compressed = true;

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
            foreach(var r in lazReader.header.number_of_points_by_return)
            {
                if (r > 0) numReturns++;
            }
            foreach(var re in lazReader.header.extended_number_of_points_by_return)
            {
                if (re > 0) numReturns++;
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

            foreach (var vlr in vlrs)
            {
                string description = Encoding.Default.GetString(vlr.description);
                if (description.Contains("SRS") || description.Contains("WKT"))
                {
                    pcSRS = Encoding.Default.GetString(vlr.data);
                }
            }

            Point3d min = new Point3d(lazReader.header.min_x, lazReader.header.min_y, lazReader.header.min_z);
            Point3d max = new Point3d(lazReader.header.max_x, lazReader.header.max_y, lazReader.header.max_z);
            BoundingBox maxBox = new BoundingBox(min, max);
            AddPreviewItem(maxBox);

            Message = numberOfPoints.ToString("N0") + " points";

            PointCloud pointCloud = new PointCloud();
            GH_Structure<GH_Point> ghPC = new GH_Structure<GH_Point>();
            GH_Structure<GH_Colour> ghColors = new GH_Structure<GH_Colour>();

            var coordArray = new double[3];
            int pointCounter = 0;

            if (filter)
            {
                var intersectionBox = Rhino.Geometry.BoundingBox.Intersection(bbox, maxBox);

                if (!intersectionBox.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping Boundary is outside the bounds of the point cloud.");
                }
                else
                {
                    for (int pointIndex = 0; pointIndex < numberOfPoints; pointIndex++)
                    {
                        ///Read the point
                        lazReader.read_point();

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
                            pointCloud.Add(pt, col.Value);
                            pointCounter++;
                        }
                    }
                    Message = pointCounter.ToString("N0") + " of " + numberOfPoints.ToString("N0") + " points";
                }
            }

            else
            {
                for (int pointIndex = 0; pointIndex < numberOfPoints; pointIndex++)
                {
                    ///Read the point
                    lazReader.read_point();

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
                    pointCloud.Add(pt, col.Value);
                }
            }

            lazReader.close_reader();

            AddPreviewItem(pointCloud, 4);

            DA.SetDataList(0, info);
            DA.SetDataTree(1, ghPC);
            DA.SetDataTree(2, ghColors);
            DA.SetData(3, pcSRS);
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
            get { return new Guid("c1559136-c952-457d-bf67-c1aa52cd61ca"); }
        }
    }
}