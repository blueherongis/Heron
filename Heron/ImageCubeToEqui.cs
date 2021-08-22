using System;
using System.IO;
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
    public class CubeToEqui : GH_TaskCapableComponent<CubeToEqui.SolveResults>
    {
        /// <summary>
        /// Initializes a new instance of the CubeToEqui class.
        /// </summary>
        public CubeToEqui()
          : base("Cubemap To Equirectangular", "CubeToEqui",
              "Convert a cube map panorama to an equirectangular panorama.",
              "Heron", "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Cubemap Image", "cubeMap", "File path of the cubemap image to convert.", GH_ParamAccess.item);
            pManager.AddTextParameter("Equirectangular Image", "equiMap", "Output file path for the converted equirectangular image.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Equirectangular Width", "equiWidth", "Set the horizontal resolution of equirectangular image.  If no width is provided, the width will default to cubemap width.", GH_ParamAccess.item);
            pManager[2].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddBooleanParameter("Converted", "C", "True if the cubemap was succesffuly converted to equirectangular.", GH_ParamAccess.item);
            pManager.AddTextParameter("Equirectangular File Location", "F", "File location of equiMap if successfully converted.", GH_ParamAccess.item);
        }

        public class SolveResults
        {
            public Boolean Converted { get; set; } 
            public string EquiFileLoc { get; set; }
        }

        SolveResults Compute (string cubeMap, string equiMap, int w)
        {
            var rc = new SolveResults();
            Boolean converted = false;
            string equiFileLoc = string.Empty;
  
            if (!File.Exists(cubeMap))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Cubemap file path does not exist.");
                rc.Converted = converted;
                rc.EquiFileLoc = equiFileLoc;
                return rc;
            }

            else if (cubeMap == equiMap)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Cubemap file path and equirectangular file path are the same. Cubemap not converted.");
                rc.Converted = converted;
                rc.EquiFileLoc = equiFileLoc;
                return rc;
            }

            else
            {
                if (File.Exists(equiMap))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Existing file overwritten: " + equiMap);
                }

                if (!Directory.Exists(Path.GetDirectoryName(equiMap)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(equiMap));
                }

                try
                {
                    using (Bitmap bm = new Bitmap(cubeMap))
                    {
                        Bitmap equi = new Bitmap(bm.Width, bm.Height, bm.PixelFormat);
                        if ((w < 1) || (w.Equals(null)))
                        {
                            w = bm.Width;
                        }
                        equi = ConvertCubicToEquirectangular(bm,w);
                        equi.Save(equiMap);
                        equi.Dispose();
                        bm.Dispose();
                        equiFileLoc = equiMap;
                        converted = true;
                    }
                }

                catch
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not load image from file path: " + cubeMap);
                }

                rc.Converted = converted;
                rc.EquiFileLoc = equiFileLoc;

                return rc;
            }

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
                string cubeLoc = "";
                string equiLoc = "";
                ///allow for no input for width
                int w = 0;
                DA.GetData<int>(2, ref w);

                Task<SolveResults> tsk = null;

                if (DA.GetData<string>(0, ref cubeLoc) && DA.GetData<string>(1, ref equiLoc))
                {
                    tsk = Task.Run(() => Compute(cubeLoc, equiLoc, w), CancelToken);
                }

                ///Add a null task even if data collection fails.  This keeps the list size in sync with the iterations
                TaskList.Add(tsk);
                return;
            }


            if (!GetSolveResults(DA, out var results))
            {
                ///Compute right here, right now.
                ///1. Collect
                ///
                string cubeLoc = "";
                string equiLoc = "";
                int w = 0;

                if (!DA.GetData<string>(0, ref cubeLoc)) { return; }
                if (!DA.GetData<string>(1, ref equiLoc)) { return; }
                if (!DA.GetData<int>(1, ref w)) { return; }

                ///2. Compute
                ///
                results = Compute(cubeLoc, equiLoc, w);
            }

            ///3. Set
            ///
            if (results != null)
            {
                DA.SetData(0, results.Converted);
                DA.SetData(1, results.EquiFileLoc);
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
            get { return new Guid("b223faa4-c788-4e15-b29a-70d0413532b1"); }
        }


        public static Bitmap ConvertCubicToEquirectangular(Bitmap bm, int w)
        {
            //algorithm from https://stackoverflow.com/questions/34250742/converting-a-cubemap-into-equirectangular-panorama
            //for cube maps with the following format:
            //  empty top empty empty
            //  left forward right backward
            //  empty bottom empty empty

            Bitmap equiTexture = new Bitmap(w, w / 2, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            GH_MemoryBitmap ghEquiMap = new GH_MemoryBitmap(equiTexture);

            ///GH_MemoryBitmap Sample faster than GetPixel
            ///https://www.grasshopper3d.com/forum/topics/unsafe?page=1&commentId=2985220%3AComment%3A808291&x=1#2985220Comment808291
            GH_MemoryBitmap ghBitmap = new GH_MemoryBitmap(bm);

            double u, v; //Normalised texture coordinates, from 0 to 1, starting at lower left corner
            double phi, theta; //Polar coordinates
            int cubeFaceWidth, cubeFaceHeight;


            cubeFaceWidth = bm.Width / 4; //4 horizontal faces
            cubeFaceHeight = bm.Height / 3; //3 vertical faces


            for (int j = 0; j < equiTexture.Height; j++)
            {
                //Rows start from the bottom
                v = 1 - ((double)j / equiTexture.Height);
                theta = v * Math.PI;

                for (int i = 0; i < equiTexture.Width; i++)
                {
                    //Columns start from the left
                    u = ((double)i / equiTexture.Width);
                    phi = u * 2 * Math.PI;

                    double x, y, z; //Unit vector
                    x = Math.Sin(phi) * Math.Sin(theta) * -1;
                    y = Math.Cos(theta);
                    z = Math.Cos(phi) * Math.Sin(theta) * -1;

                    double xa, ya, za;
                    double a;

                    double[] maxArr = new double[3] { Math.Abs(x), Math.Abs(y), Math.Abs(z) };
                    //a = Math.Max(new double[3] { Math.Abs(x), Math.Abs(y), Math.Abs(z) });
                    a = maxArr.Max();

                    //Vector Parallel to the unit vector that lies on one of the cube faces
                    xa = x / a;
                    ya = y / a;
                    za = z / a;

                    Color color = Color.Transparent;
                    int xPixel, yPixel;
                    int xOffset, yOffset;

                    if (xa == 1)
                    {
                        //Right
                        xPixel = (int)((((za + 1) / 2) - 1) * cubeFaceWidth);
                        xOffset = 2 * cubeFaceWidth; //Offset
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight; //Offset
                    }
                    else if (xa == -1)
                    {
                        //Left
                        xPixel = (int)((((za + 1) / 2)) * cubeFaceWidth);
                        xOffset = 0;
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight;
                    }
                    else if (ya == 1)
                    {
                        //Up
                        xPixel = (int)((((xa + 1) / 2)) * cubeFaceWidth);
                        xOffset = cubeFaceWidth;
                        yPixel = (int)((((za + 1) / 2) - 1) * cubeFaceHeight);
                        yOffset = 2 * cubeFaceHeight;
                    }
                    else if (ya == -1)
                    {
                        //Down
                        xPixel = (int)((((xa + 1) / 2)) * cubeFaceWidth);
                        xOffset = cubeFaceWidth;
                        yPixel = (int)((((za + 1) / 2)) * cubeFaceHeight);
                        yOffset = 0;
                    }
                    else if (za == 1)
                    {
                        //Front
                        xPixel = (int)((((xa + 1) / 2)) * cubeFaceWidth);
                        xOffset = cubeFaceWidth;
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight;
                    }
                    else if (za == -1)
                    {
                        //Back
                        xPixel = (int)((((xa + 1) / 2) - 1) * cubeFaceWidth);
                        xOffset = 3 * cubeFaceWidth;
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight;
                    }
                    else
                    {
                        //Debug.LogWarning("Unknown face, something went wrong");
                        xPixel = 0;
                        yPixel = 0;
                        xOffset = 0;
                        yOffset = 0;
                    }

                    xPixel = Math.Abs(xPixel);
                    yPixel = Math.Abs(yPixel);

                    xPixel += xOffset;
                    yPixel += yOffset;

                    if (xPixel < 0 || yPixel < 0)
                    {
                        throw new ArgumentNullException("x = " + xPixel.ToString() + " y = " + yPixel.ToString());
                    }

                    if (xPixel > (bm.Width - 1))
                    {
                        xPixel = xPixel - 1;
                        //throw new ArgumentNullException("x = " + xPixel.ToString() + " y = " + yPixel.ToString()+" i="+i+" j="+j);
                    }

                    if (yPixel > (bm.Height - 1))
                    {
                        yPixel = yPixel - 1;
                        //throw new ArgumentNullException("x = " + xPixel.ToString() + " y = " + yPixel.ToString()+" i="+i+" j="+j);
                    }

                    //sampler.Sample(xPixel, yPixel, ref color);
                    color = ghBitmap.Colour(xPixel, yPixel);

                    ghEquiMap.Colour(i, j, color);

                }
            }

            ghBitmap.Release(false);
            ghEquiMap.Release(true);

            return equiTexture;
        }


        
        /*public unsafe static Bitmap ConvertCubicToEquirectangularLockbits(Bitmap bm, int w)
        {
            //algorithm from https://stackoverflow.com/questions/34250742/converting-a-cubemap-into-equirectangular-panorama
            //for cube maps with the following format:
            //  empty top empty empty
            //  left forward right backward
            //  empty bottom empty empty

            Bitmap equiTexture = new Bitmap(w, w / 2, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            GH_MemoryBitmap ghEquiTexture = new GH_MemoryBitmap(equiTexture);

            ///GH_MemoryBitmap Sample faster than GetPixel
            ///https://www.grasshopper3d.com/forum/topics/unsafe?page=1&commentId=2985220%3AComment%3A808291&x=1#2985220Comment808291
            GH_MemoryBitmap ghBitmap = new GH_MemoryBitmap(bm);

            double u, v; //Normalised texture coordinates, from 0 to 1, starting at lower left corner
            double phi, theta; //Polar coordinates
            int cubeFaceWidth, cubeFaceHeight;


            cubeFaceWidth = bm.Width / 4; //4 horizontal faces
            cubeFaceHeight = bm.Height / 3; //3 vertical faces


            for (int j = 0; j < equiTexture.Height; j++)
            {
                //Rows start from the bottom
                v = 1 - ((double)j / equiTexture.Height);
                theta = v * Math.PI;

                for (int i = 0; i < equiTexture.Width; i++)
                {
                    //Columns start from the left
                    u = ((double)i / equiTexture.Width);
                    phi = u * 2 * Math.PI;

                    double x, y, z; //Unit vector
                    x = Math.Sin(phi) * Math.Sin(theta) * -1;
                    y = Math.Cos(theta);
                    z = Math.Cos(phi) * Math.Sin(theta) * -1;

                    double xa, ya, za;
                    double a;

                    double[] maxArr = new double[3] { Math.Abs(x), Math.Abs(y), Math.Abs(z) };
                    //a = Math.Max(new double[3] { Math.Abs(x), Math.Abs(y), Math.Abs(z) });
                    a = maxArr.Max();

                    //Vector Parallel to the unit vector that lies on one of the cube faces
                    xa = x / a;
                    ya = y / a;
                    za = z / a;

                    Color color = Color.Transparent;
                    int xPixel, yPixel;
                    int xOffset, yOffset;

                    if (xa == 1)
                    {
                        //Right
                        xPixel = (int)((((za + 1) / 2) - 1) * cubeFaceWidth);
                        xOffset = 2 * cubeFaceWidth; //Offset
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight; //Offset
                    }
                    else if (xa == -1)
                    {
                        //Left
                        xPixel = (int)((((za + 1) / 2)) * cubeFaceWidth);
                        xOffset = 0;
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight;
                    }
                    else if (ya == 1)
                    {
                        //Up
                        xPixel = (int)((((xa + 1) / 2)) * cubeFaceWidth);
                        xOffset = cubeFaceWidth;
                        yPixel = (int)((((za + 1) / 2) - 1) * cubeFaceHeight);
                        yOffset = 2 * cubeFaceHeight;
                    }
                    else if (ya == -1)
                    {
                        //Down
                        xPixel = (int)((((xa + 1) / 2)) * cubeFaceWidth);
                        xOffset = cubeFaceWidth;
                        yPixel = (int)((((za + 1) / 2)) * cubeFaceHeight);
                        yOffset = 0;
                    }
                    else if (za == 1)
                    {
                        //Front
                        xPixel = (int)((((xa + 1) / 2)) * cubeFaceWidth);
                        xOffset = cubeFaceWidth;
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight;
                    }
                    else if (za == -1)
                    {
                        //Back
                        xPixel = (int)((((xa + 1) / 2) - 1) * cubeFaceWidth);
                        xOffset = 3 * cubeFaceWidth;
                        yPixel = (int)((((ya + 1) / 2)) * cubeFaceHeight);
                        yOffset = cubeFaceHeight;
                    }
                    else
                    {
                        //Debug.LogWarning("Unknown face, something went wrong");
                        xPixel = 0;
                        yPixel = 0;
                        xOffset = 0;
                        yOffset = 0;
                    }

                    xPixel = Math.Abs(xPixel);
                    yPixel = Math.Abs(yPixel);

                    xPixel += xOffset;
                    yPixel += yOffset;

                    if (xPixel < 0 || yPixel < 0)
                    {
                        throw new ArgumentNullException("x = " + xPixel.ToString() + " y = " + yPixel.ToString());
                    }

                    if (xPixel > (bm.Width - 1))
                    {
                        xPixel = xPixel - 1;
                        //throw new ArgumentNullException("x = " + xPixel.ToString() + " y = " + yPixel.ToString()+" i="+i+" j="+j);
                    }

                    if (yPixel > (bm.Height - 1))
                    {
                        yPixel = yPixel - 1;
                        //throw new ArgumentNullException("x = " + xPixel.ToString() + " y = " + yPixel.ToString()+" i="+i+" j="+j);
                    }

                    //sampler.Sample(xPixel, yPixel, ref color);
                    color = ghBitmap.Colour(xPixel, yPixel);

                    ghEquiTexture.Colour(i, j, color);

                }
            }

            ghBitmap.Release(false);
            ghEquiTexture.Release(true);

            return equiTexture;
        }
        */
    }
}