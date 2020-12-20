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
    public class ImageRotate : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the FlipImage class.
        /// </summary>
        public ImageRotate()
          : base("Rotate Image", "RotateImage", "Roate an image 90, 180 or 270 degrees.",
              "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Original Image", "image", "File path for the image to be rotated.", GH_ParamAccess.item);
            pManager.AddTextParameter("Suffix", "suffix", "Suffix to add the end of the original image.  If none is provided, a '_rotated' suffix will be added. " +
                "An existing flipped image path will be overwritten.", GH_ParamAccess.item, "_rotated");
            pManager.AddBooleanParameter("Run", "run", "Rotate the image.  An existing rotated image path will be overwritten.", GH_ParamAccess.item, false);
            pManager[1].Optional = true;
            Message = rotateStatus;

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Rotated Image", "rotated", "File path for the rotated image.", GH_ParamAccess.item);
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

                switch (rotateStatus)
                {
                    case "None":
                        break;
                    case "Rotate 90":
                        finalImage.RotateFlip(RotateFlipType.Rotate90FlipNone);
                        break;
                    case "Rotate 180":
                        finalImage.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        break;
                    case "Rotate 270":
                        finalImage.RotateFlip(RotateFlipType.Rotate270FlipNone);
                        break;
                }

                finalImage.Save(fOut, imgFormat);
                finalImage.Dispose();
            }

            DA.SetData(0, fOut);

        }


        ////////////////////////////
        //Menu Items

        private bool IsFlipSelected(string rotateString)
        {
            return rotateString.Equals(rotateStatus);
        }
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            ToolStripMenuItem sN = new ToolStripMenuItem("No Rotation");
            sN.Tag = "No Rotation";
            sN.Checked = IsFlipSelected("No Rotation");
            sN.ToolTipText = "Do not rotate the image";
            sN.Click += FlipItemOnClick;
            menu.Items.Add(sN);

            ToolStripMenuItem s90 = new ToolStripMenuItem("Rotate 90");
            s90.Tag = "Rotate 90";
            s90.Checked = IsFlipSelected("Rotate 90");
            s90.ToolTipText = "Rotate image 90 deg clockwise.";
            s90.Click += FlipItemOnClick;
            menu.Items.Add(s90);

            ToolStripMenuItem s180 = new ToolStripMenuItem("Rotate 180");
            s180.Tag = "Rotate 180";
            s180.Checked = IsFlipSelected("Rotate 180");
            s180.ToolTipText = "Rotate image 180 deg clockwise.";
            s180.Click += FlipItemOnClick;
            menu.Items.Add(s180);

            ToolStripMenuItem s270 = new ToolStripMenuItem("Rotate 270");
            s270.Tag = "Rotate 270";
            s270.Checked = IsFlipSelected("Rotate 270");
            s270.ToolTipText = "Rotate image 270 deg clockwise.";
            s270.Click += FlipItemOnClick;
            menu.Items.Add(s270);

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

            RecordUndoEvent("rotateStatus");

            rotateStatus = code;
            Message = rotateStatus;

            ExpireSolution(true);
        }

        ////////////////////////////
        //Sticky Parameters

        private string rotateStatus = "Rotate 270";

        public string RotateStatus
        {
            get { return rotateStatus; }
            set 
            { 
                rotateStatus = value;
                Message = rotateStatus;
            }
        }

        public override bool Write (GH_IWriter writer)
        {
            writer.SetString("rotateStatus", rotateStatus);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            rotateStatus = reader.GetString("rotateStatus");
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
            get { return new Guid("AA4281C3-74E4-46A8-A37D-F095F9EC1570"); }
        }
    }
}