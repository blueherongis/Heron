using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

using Grasshopper.Kernel;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace Heron
{
    public class ImageFlip : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the FlipImage class.
        /// </summary>
        public ImageFlip()
          : base("Flip Image", "FlipImage", "Flip an image along its vertical, horizontal axis or both.",
              "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Original Image", "image", "File path for the image to be flipped.", GH_ParamAccess.item);
            pManager.AddTextParameter("Suffix", "suffix", "Suffix to add the end of the original image.  If none is provided, a '_flipped' suffix will be added. " +
                "An existing flipped image path will be overwritten.", GH_ParamAccess.item, "_flipped");
            pManager.AddBooleanParameter("Run", "run", "Flip the image.  An existing flipped image path will be overwritten.", GH_ParamAccess.item, false);
            pManager[1].Optional = true;
            Message = flipStatus;

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Flipped Image", "flipped", "File path for the flipped image.", GH_ParamAccess.item);
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

                switch (flipStatus)
                {
                    case "None":
                        break;
                    case "Flip X":
                        finalImage.RotateFlip(RotateFlipType.RotateNoneFlipX);
                        break;
                    case "Flip Y":
                        finalImage.RotateFlip(RotateFlipType.RotateNoneFlipY);
                        break;
                    case "Flip X and Y":
                        finalImage.RotateFlip(RotateFlipType.RotateNoneFlipXY);
                        break;
                }

                finalImage.Save(fOut, imgFormat);
                finalImage.Dispose();
            }

            DA.SetData(0, fOut);

        }


        ////////////////////////////
        //Menu Items

        private bool IsFlipSelected(string flipString)
        {
            return flipString.Equals(flipStatus);
        }
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem fN = new ToolStripMenuItem("No Flip");
            fN.Tag = "No Flip";
            fN.Checked = IsFlipSelected("No Flip");
            fN.ToolTipText = "Do not flip the image";
            fN.Click += FlipItemOnClick;
            menu.Items.Add(fN);

            ToolStripMenuItem fX = new ToolStripMenuItem("Flip X");
            fX.Tag = "Flip X";
            fX.Checked = IsFlipSelected("Flip X");
            fX.ToolTipText = "Flip image along its vertical axis (left becomes right).";
            fX.Click += FlipItemOnClick;
            menu.Items.Add(fX);

            ToolStripMenuItem fY = new ToolStripMenuItem("Flip Y");
            fY.Tag = "Flip Y";
            fY.Checked = IsFlipSelected("Flip Y");
            fY.ToolTipText = "Flip image along its horizontal axis (top becomes bottom).";
            fY.Click += FlipItemOnClick;
            menu.Items.Add(fY);

            ToolStripMenuItem fXY = new ToolStripMenuItem("Flip X and Y");
            fXY.Tag = "Flip X and Y";
            fXY.Checked = IsFlipSelected("Flip X and Y");
            fXY.ToolTipText = "Flip image along both X and Y axises.";
            fXY.Click += FlipItemOnClick;
            menu.Items.Add(fXY);

            base.AppendAdditionalComponentMenuItems(menu);
        }

        private void FlipItemOnClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
                return;

            string code = (string)item.Tag;
            if (IsFlipSelected(code))
                return;

            RecordUndoEvent("FlipStatus");

            flipStatus = code;
            Message = flipStatus;

            ExpireSolution(true);
        }

        ////////////////////////////
        //Sticky Parameters

        private string flipStatus = "Flip Y";

        public string FlipStatus
        {
            get { return flipStatus; }
            set 
            { 
                flipStatus = value;
                Message = flipStatus;
            }
        }

        public override bool Write (GH_IWriter writer)
        {
            writer.SetString("FlipStatus", FlipStatus);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            FlipStatus = reader.GetString("FlipStatus");
            return base.Read(reader);
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