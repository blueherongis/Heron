using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Heron
{
    public class SetSRS : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the SetSRS class.
        /// </summary>
        public SetSRS()
          : base("Set Spatial Reference System", "SRS",
              "Set the spatial reference system to be used by Heron SRS-enabled components.   Heron defaults to 'WGS84'.  " +
              "Click the 'Update' button or recompute the Grasshopper definition to ensure Heron SRS-enabled components are updated.", "Heron",
              "GIS Tools")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("SRS", "SRS", "A string describing the SRS for use with Heron SRS-enabled components.  " +
                "This can be a well-known SRS such as 'WGS84' " +
                "or an EPSG code such as 'EPSG:4326 or be in WKT format.", GH_ParamAccess.item);
            pManager[0].Optional = true;
            Message = HeronSRS.Instance.SRS;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Well Known Text", "WKT", "Well Know Text (WKT) of the SRS which has been set for Heron components.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ///GDAL setup
            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();
            RESTful.GdalConfiguration.ConfigureGdal();


            string heronSRSstring = HeronSRS.Instance.SRS;
            DA.GetData(0, ref heronSRSstring);

            if (!String.IsNullOrEmpty(heronSRSstring))
            {
                OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
                heronSRS.SetFromUserInput(heronSRSstring);
                heronSRS.ExportToPrettyWkt(out string wkt, 0);

                try
                {
                    int sourceSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
                    Message = "EPSG:" + sourceSRSInt;
                }
                catch
                {
                }

                if (heronSRS.Validate() == 1  || string.IsNullOrEmpty(wkt))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid SRS.");
                    return;
                }

                else
                {
                    if (string.Equals(HeronSRS.Instance.SRS, heronSRSstring))
                    {
                        DA.SetData(0, wkt);                    
                        return;
                    }
                    HeronSRS.Instance.SRS = heronSRSstring;
                    DA.SetData(0, wkt);
                }
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Please enter a valid string.");
            }
        }

        /// <summary>
        /// Enable custom attributes to enable creation of the Update button
        /// </summary>
        public override void CreateAttributes()
        {
            m_attributes = new SetSRSAttributes(this);
        }


        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.eap;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("F6F84573-99C2-4B65-A684-4D55175151B1"); }
        }        
    }

    ///Attempt to create a button to Recompute the definition to force other Heron components to pick up HeronSRS changes
    ///Based on the following posts
    ///https://www.grasshopper3d.com/forum/topics/create-radio-button-on-grasshopper-component
    ///https://discourse.mcneel.com/t/toggle-with-one-click/126957/51
    ///https://www.grasshopper3d.com/forum/topics/custom-attributes-draw-extra-items-on-canvas
    ///https://discourse.mcneel.com/t/recompute-button/82094/8
    ///and on code from Grasshopper.Kernel.Special.GH_ButtonObjectAttributes in Grasshopper.dll using ILSpy
    public class SetSRSAttributes : GH_ComponentAttributes
    {
        public SetSRSAttributes(SetSRS owner) : base(owner) { }

        protected override void Layout()
        {
            base.Layout();

            System.Drawing.Rectangle rec0 = GH_Convert.ToRectangle(Bounds);
            rec0.Height += 22;

            System.Drawing.Rectangle rec1 = rec0;
            rec1.Y = rec1.Bottom - 22;
            rec1.Height = 22;
            rec1.Inflate(-2, -2);

            Bounds = rec0;
            ButtonBounds = rec1;
        }
        private System.Drawing.Rectangle ButtonBounds { get; set; }

        private bool m_buttonDown;
        public bool ButtonDown
        {
            get
            {
                return this.m_buttonDown;
            }
            set
            {
                this.m_buttonDown = value;
            }
        }

        public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            if (ButtonBounds.Contains((int) point.X,(int) point.Y))
            {
                e.Icon = base.Owner.Icon_24x24;
                e.Title = "Update";
                e.Text = "Click here to recompute Heron SRS-enabled components.";
                //e.Description = "Recompute Heron components which use the HeronSRS";
                return;
            }
            base.SetupTooltip(point, e);
        }
        protected override void Render(GH_Canvas canvas, System.Drawing.Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);

            if (channel == GH_CanvasChannel.Objects)
            {               
                GH_PaletteStyle gH_PaletteStyle = GH_CapsuleRenderEngine.GetImpliedStyle(GH_Palette.Normal, this.Selected, base.Owner.Locked, true);
                GH_Capsule gH_Capsule = GH_Capsule.CreateTextCapsule(ButtonBounds, ButtonBounds, GH_Palette.Black, "Update", 2, 0);

                gH_PaletteStyle = GH_Skin.palette_black_standard;
                if (ButtonDown)
                {
                    LinearGradientBrush linearGradientBrush = new LinearGradientBrush(gH_Capsule.Box, GH_GraphicsUtil.OffsetColour(gH_PaletteStyle.Fill, 0), GH_GraphicsUtil.OffsetColour(gH_PaletteStyle.Fill, 100), LinearGradientMode.Vertical);
                    graphics.FillPath(linearGradientBrush, gH_Capsule.OutlineShape);
                    gH_Capsule = GH_Capsule.CreateTextCapsule(ButtonBounds, ButtonBounds, GH_Palette.Transparent, "Updating...", 2, 0);
                    gH_Capsule.Render(graphics, Selected, Owner.Locked, false);
                    linearGradientBrush.Dispose();
                }
                else
                {
                    gH_Capsule.Render(graphics, Selected, Owner.Locked, false);
                }

                gH_Capsule.RenderEngine.RenderOutlines(graphics, canvas.Viewport.Zoom, gH_PaletteStyle);
                gH_Capsule.Dispose();
            }
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                System.Drawing.RectangleF rec = ButtonBounds;
                if (rec.Contains(e.CanvasLocation))
                {
                    ButtonDown = true;
                    Owner.ExpireSolution(true);
                    return GH_ObjectResponse.Handled;
                }
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            GH_ObjectResponse result;
            if (ButtonDown)
            {
                ///Only recompute HeronSRS components
                var heronComponents = new List<string>() {
                        "RESTRaster", "RESTVector","RESTTopo", "OSMRest",
                        "ImportVector", "ImportRaster", "ImportOSM", "ImportTopo",
                        "DDtoXY", "XYtoDD",
                        "SlippyRaster", "SlippyTiles",
                        "MapboxRaster", "MapboxVector", "MapboxTopo"};
                foreach (var obj in Owner.OnPingDocument().FindObjects(heronComponents, 100))
                {
                    obj.ExpireSolution(false);
                }

                ButtonDown = false;
                Owner.ExpireSolution(true);
                result = GH_ObjectResponse.Release;
            }
            else
            {
                result = base.RespondToMouseUp(sender, e);
            }
            return result;
        }
    }
}