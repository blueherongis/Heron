using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;

using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;
using Rhino.Display;

namespace Heron
{
    public class ImageCubeMap : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the ImageCubeMap class.
        /// </summary>
        public ImageCubeMap()
          : base("Cubemap From View", "CM",
              "Generate a cubemap from a given plane using the specified display mode.",
              "Utilities")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddPlaneParameter("Camera Plane", "cameraPlane", "Plane whose origin is the camera location, " +
                "Y vector is the camera target direction and Z vector is the camera up direction.", GH_ParamAccess.list);
            pManager.AddTextParameter("Folder Path", "folderPath", "Folder path for exported cube maps.", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for exported cube maps.", GH_ParamAccess.item, "cubemap");
            pManager.AddIntegerParameter("Resolution", "res", "The width resolution of the cube map.", GH_ParamAccess.item, 1024);
            pManager.AddTextParameter("Display Mode", "displayMode", "Set the display mode to be used when creating the cubemap.  If no display mode is set or does not exist in the document, the active view's display mode will be used.", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "run", "Go ahead and run the export", GH_ParamAccess.item);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Cubemap", "cubemap", "File location for the cubemap.", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Plane> camPlanes = new List<Plane>();
            DA.GetDataList<Plane>(0, camPlanes);

            string folder = string.Empty;
            DA.GetData<string>(1, ref folder);
            folder = Path.GetFullPath(folder);
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString())) { folder += Path.DirectorySeparatorChar; }
            
            string prefix = string.Empty;
            DA.GetData<string>(2, ref prefix);

            int imageWidth = 0;
            DA.GetData<int>(3, ref imageWidth);
            imageWidth = imageWidth / 4;
            Size size = new Size(imageWidth, imageWidth);

            string displayMode = string.Empty;
            DA.GetData<string>(4, ref displayMode);

            bool run = false;
            DA.GetData<bool>(5, ref run);

            int pad = camPlanes.Count.ToString().Length;

            List<string> cubemaps = new List<string>();

            ///Save the intial camera
            saveCam = camFromVP(Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport);

            ///Set the display mode to be used for bitmaps
            DisplayModeDescription viewMode = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport.DisplayMode;

            if (DisplayModeDescription.FindByName(displayMode) != null)
            {
                viewMode = DisplayModeDescription.FindByName(displayMode);
            }

            Message = viewMode.EnglishName;

            if (run)
            {
                for (int i = 0; i < camPlanes.Count; i++)
                {
                    ///Setup camera
                    Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView;
                    Rhino.Display.RhinoViewport vp = view.ActiveViewport;

                    ///Get the bounding box of all visible object in the doc for use in setting up the camera 
                    ///target so that the far frustrum plane doesn't clip anything
                    double zoomDistance = Rhino.RhinoDoc.ActiveDoc.Objects.BoundingBoxVisible.Diagonal.Length;

                    Plane camPlane = camPlanes[i];
                    Point3d camPoint = camPlane.Origin;
                    Vector3d camDir = camPlane.YAxis;
                    Point3d tarPoint = Transform.Translation(camDir * zoomDistance/2) * camPoint;

                    
                    vp.ChangeToPerspectiveProjection(false, 12.0);
                    //vp.Size = size;
                    vp.DisplayMode = viewMode;
                    //view.Redraw();


                    ///Set up final bitmap
                    Bitmap cubemap = new Bitmap(imageWidth * 4, imageWidth * 3);

                    ///Place the images on cubemap bitmap
                    using (Graphics gr = Graphics.FromImage(cubemap))
                    {
                        ///Grab bitmap

                        ///Set up camera directions
                        Point3d tarLeft = Transform.Translation(-camPlane.XAxis * zoomDistance / 2) * camPoint;
                        Point3d tarFront = Transform.Translation(camPlane.YAxis * zoomDistance / 2) * camPoint;
                        Point3d tarRight = Transform.Translation(camPlane.XAxis * zoomDistance / 2) * camPoint;
                        Point3d tarBack = Transform.Translation(-camPlane.YAxis * zoomDistance / 2) * camPoint;
                        Point3d tarUp = Transform.Translation(camPlane.ZAxis * zoomDistance / 2) * camPoint;
                        Point3d tarDown = Transform.Translation(-camPlane.ZAxis * zoomDistance / 2) * camPoint;
                        List<Point3d> camTargets = new List<Point3d>() { tarLeft, tarFront, tarRight, tarBack, tarUp, tarDown };

                        ///Loop through pano directions
                        int insertLoc = 0;
                        for (int d = 0; d < 4; d++)
                        {
                            ///Set camera direction
                            vp.SetCameraLocations(camTargets[d], camPoint);

                            ///Redraw
                            //view.Redraw();

                            gr.DrawImage(view.CaptureToBitmap(size, viewMode), insertLoc, imageWidth);

                            insertLoc = insertLoc + imageWidth;
                        }
                        
                        ///Get up and down views
                        ///Get up view
                        vp.SetCameraLocations(tarUp, camPoint);
                        ///Redraw
                        view.Redraw();
                        var bmTop = view.CaptureToBitmap(size, viewMode);
                        bmTop.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        gr.DrawImage(bmTop, imageWidth, 0);

                        ///Get down view
                        vp.SetCameraLocations(tarDown, camPoint);

                        ///Redraw
                        view.Redraw();
                        var bmBottom = view.CaptureToBitmap(size, viewMode);
                        gr.DrawImage(view.CaptureToBitmap(size, viewMode), imageWidth, imageWidth * 2);

                    }
                    ///End cubemap construction loop
                    
                    ///Save cubemap bitmap
                    string s = i.ToString().PadLeft(pad, '0');
                    string saveText = folder + prefix + "_" + s + ".png";
                    cubemap.Save(saveText, System.Drawing.Imaging.ImageFormat.Png);
                    cubemaps.Add(saveText);
                    cubemap.Dispose();
                    
                }
            }

            ///Restore initial camera
            setCamera(saveCam, Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport);
            Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.Redraw();

            DA.SetDataList(0, cubemaps);
        }



        static camera saveCam;
        struct camera
        {
            public Point3d location;
            public Point3d target;
            public Vector3d up;
            public double lens;
            public bool parallel;
            public Size size;
            public DisplayModeDescription displayMode;
        }

        camera camFromVP(Rhino.Display.RhinoViewport vp)
        {
            camera c = new camera();
            c.location = vp.CameraLocation;
            c.target = vp.CameraTarget;
            c.up = vp.CameraUp;
            c.lens = vp.Camera35mmLensLength;
            c.parallel = vp.IsParallelProjection;
            c.size = vp.Size;
            c.displayMode = vp.DisplayMode;
            return c;
        }

        void setCamera(camera c, Rhino.Display.RhinoViewport vp)
        {
            if (c.parallel)
            {
                vp.ChangeToParallelProjection(true);
            }
            else
            {
                vp.ChangeToPerspectiveProjection(false, c.lens);
            }
            vp.SetCameraLocations(c.target, c.location);
            vp.CameraUp = c.up;
            vp.Camera35mmLensLength = c.lens;
            vp.Size = c.size;
            vp.DisplayMode = c.displayMode;
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
            get { return new Guid("69dfa4b6-8176-474f-a958-fe30a17d15d8"); }
        }
    }
}