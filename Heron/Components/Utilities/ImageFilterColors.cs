using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Drawing;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

using Rhino;
using Rhino.Geometry;

namespace Heron
{
    public class ImageFilterColors : GH_TaskCapableComponent<ImageFilterColors.SolveResults>
    {
        ///parallel processing based on code from https://github.com/mcneel/rhino-developer-samples/blob/6/grasshopper/cs/SampleGhTaskCapable/Components/SampleGhTaskCapableComponent.cs
        ///no need to worry about data trees, concurrent dictionaries or max concurrency, gh takes care of it!
        ///just think of what inputs you need per branch

        /// <summary>
        /// Initializes a new instance of the ImageColors class.
        /// </summary>
        public ImageFilterColors()
          : base("Image Filtered Colors", "ImageFC",
              "Get a filtered pixel count of colors contained in an image based on color list.",
              "Heron", "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Image File Location", "fileLoc", "File location(s) of the image(s).", GH_ParamAccess.item);
            pManager.AddColourParameter("Color Filter", "colors", "Filter the image for specific colors.  If no filter colors are provided, all colors in the image will be included.", GH_ParamAccess.list);
            pManager[1].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddIntegerParameter("Pixel Count", "PC", "Number of pixels in the image.", GH_ParamAccess.item);
            pManager.AddColourParameter("Top Colors", "TC", "Sorted list of colors in image.", GH_ParamAccess.list);
            pManager.AddPointParameter("Color Coordinates", "CC", "Coordinates of pixels in image of color.", GH_ParamAccess.tree);
            //pManager.AddPointParameter("Color Location", "CL", "Pixel locations grouped by color.", GH_ParamAccess.tree);
        }

        public class SolveResults
        {
            public GH_Integer PixCount { get; set; }
            public List<GH_Colour> TopColors { get; set; }
            public GH_Structure<GH_Point> ColorLocation { get; set; }
        }

        SolveResults Compute (string fileLoc, List<Color> colors, int tskId)
        {
            var rc = new SolveResults();
            bool filterColors = colors.Any();

            List<GH_Colour> topCols = new List<GH_Colour>();
            List<GH_Integer> colCount = new List<GH_Integer>();
            GH_Structure<GH_Point> colLocation = new GH_Structure<GH_Point>();

            
            try
            {
                using (Bitmap bitmap = new Bitmap(fileLoc))
                {
                    GH_Integer pixCount = new GH_Integer();
                    GH_Convert.ToGHInteger(bitmap.Height * bitmap.Width,0,ref pixCount);
                    rc.PixCount = pixCount;

                    ///https://www.grasshopper3d.com/forum/topics/unsafe?page=1&commentId=2985220%3AComment%3A808291&x=1#2985220Comment808291
                    GH_MemoryBitmap sampler = new GH_MemoryBitmap(bitmap);

                    Color col = Color.Transparent;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        for (int y = 0; y < bitmap.Height; y++)
                        {
                            ///GH_MemoryBitmap Sample is faster than GetPixel
                            //col = bitmap.GetPixel(x, y);
                            if (sampler.Sample(x,y,ref col))
                            {
                                if (colors.Contains(col))
                                {
                                    GH_Path path = new GH_Path(tskId, colors.IndexOf(col));
                                    colLocation.Append(new GH_Point(new Point3d(x,y,0)), path);
                                }
                                else if (!filterColors)
                                {
                                    colors.Add(col);
                                    GH_Path path = new GH_Path(tskId, colors.IndexOf(col));
                                    colLocation.Append(new GH_Point(new Point3d(x, y, 0)), path);
                                }

                            }

                        }
                    }

                    sampler.Release(false);
                    bitmap.Dispose();
                }

            }

            catch
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not load image from file path: "+fileLoc);
            }

            List<GH_Colour> ghColors = new List<GH_Colour>();
            foreach (var c in colors)
            {
                ghColors.Add(new GH_Colour(c));
            }

            rc.TopColors = ghColors;
            rc.ColorLocation = colLocation;

            return rc;

        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            if (InPreSolve)
            {
                ///First pass; collect data and construct tasks
                ///
                string fileLocList = "";
                List<Color> colors = new List<Color>();
                Task<SolveResults> tsk = null;
              
                if(DA.GetData<string>(0,ref fileLocList))
                {
                    DA.GetDataList<Color>(1, colors);
                    tsk = Task.Run(() => Compute(fileLocList, colors, tsk.Id), CancelToken);
                }

                ///Add a null task even if data collection fails.  This keeps the list size in sync with the iterations
                TaskList.Add(tsk);
                return;
            }

            if(!GetSolveResults(DA, out var results))
            {
                ///Compute right here, right now.
                ///1. Collect
                ///
                string fileLocList = "";
                List<Color> colors = new List<Color>();
                int tskId = 0;

                if (!DA.GetData<string>(0, ref fileLocList)) { return; }
                if(!DA.GetDataList<Color>(1, colors)) { return; }

                ///2. Compute
                ///
                results = Compute(fileLocList, colors, tskId);
            }

            ///3. Set
            ///
            if (results != null)
            {
                DA.SetData(0, results.PixCount);
                DA.SetDataList(1, results.TopColors);
                DA.SetDataTree(2, results.ColorLocation);
                //DA.SetDataTree(3, results.ColorLocation);
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
                return Properties.Resources.raster;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("DB0FE3BC-2471-45C3-BAAC-4BC0C83CB654"); }
        }
    }
}