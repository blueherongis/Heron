using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

using Grasshopper.Kernel;
using Rhino.Geometry;

namespace Heron
{
    public class FlipImage : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the FlipImage class.
        /// </summary>
        public FlipImage()
          : base("FlipImage", "FlipImage", "Flip an image along its vertical or horizontal axis.",
              "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Original Image", "originalPath", "File path for the image to be flipped.", GH_ParamAccess.item);
            pManager.AddTextParameter("Suffix", "suffix", "Suffix to add the end of the original image.  If none is provided, a '_flipped' suffix will be added.", GH_ParamAccess.item, "_flipped");
            pManager.AddBooleanParameter("Flip Vertical", "flipX", "Flip image along its vertical axis (left becomes right).", GH_ParamAccess.item, false);
            pManager.AddBooleanParameter("Flip Horizontal", "flipY", "Flip image along its horizontal axis (top becomes bottom).", GH_ParamAccess.item, false);
            pManager.AddBooleanParameter("Run", "run", "Flip the image.  An existing flipped image path will be overwritten.", GH_ParamAccess.item, false);
            pManager[1].Optional = true;


        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Flipped Image", "flippedPath", "File path for the flipped image.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string originalPath = string.Empty;
            DA.GetData<string>(0, ref originalPath);
            if (!File.Exists(originalPath)) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Cannot find the original image."); }
            string fDir = Path.GetDirectoryName(originalPath);
            string fName = Path.GetFileNameWithoutExtension(originalPath);
            string fExt = Path.GetExtension(originalPath);

            string suffix = string.Empty;
            DA.GetData<string>(1, ref suffix);

            string fOut = Path.Combine(fDir, fName + suffix + fExt);
            if (!File.Exists(fOut)) { fOut = string.Empty; }

            bool flipX = false;
            DA.GetData<bool>("Flip Vertical", ref flipX);

            bool flipY = false;
            DA.GetData<bool>("Flip Horizontal", ref flipY);

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            Bitmap originalBitmap = new Bitmap(originalPath, true);
            ImageFormat imgFormat = originalBitmap.RawFormat;

            if (run)
            {
                Bitmap finalImage = new Bitmap(originalBitmap);
                originalBitmap.Dispose();

                fOut = Path.Combine(fDir, fName + suffix + fExt);
                if (File.Exists(fOut)) { File.Delete(fOut); }

                if (flipX)
                {
                    finalImage.RotateFlip(RotateFlipType.RotateNoneFlipX);
                }
                if (flipY)
                {
                    finalImage.RotateFlip(RotateFlipType.RotateNoneFlipY);
                }

                finalImage.Save(fOut, imgFormat);
                finalImage.Dispose();
            }

            DA.SetData(0, fOut);

        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.raster;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("f1e9c8ad-2500-48de-9692-54e7d6f6379d"); }
        }
    }
}