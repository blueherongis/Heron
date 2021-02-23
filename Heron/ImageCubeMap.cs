using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;

using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;

namespace Heron
{
    public class ImageCubeMap : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the ImageCubeMap class.
        /// </summary>
        public ImageCubeMap()
          : base("View Cubemap", "CM",
              "Generate a cubemap from a given plane using the display mode in the active viewport.",
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
            pManager.AddBooleanParameter("Run", "run", "Go ahead and run the export", GH_ParamAccess.item);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
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

            bool run = false;
            DA.GetData<bool>(4, ref run);

            int pad = camPlanes.Count.ToString().Length;

            List<string> cubemaps = new List<string>();

            ///Save the intial camera
            saveCam = camFromVP(Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport);

            if (run)
            {
                for (int i = 0; i < camPlanes.Count; i++)
                {
                    ///Setup camera
                    Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView;
                    Rhino.Display.RhinoViewport vp = view.ActiveViewport;

                    Plane camPlane = camPlanes[i];
                    Point3d camPoint = camPlane.Origin;
                    Vector3d camDir = camPlane.YAxis;
                    Vector3d camUp = camPlane.ZAxis;
                    Point3d tarPoint = Transform.Translation(camDir * 10) * camPoint;


                    vp.SetCameraLocations(tarPoint, camPoint);
                    vp.CameraUp = camPlane.ZAxis;
                    vp.Camera35mmLensLength = 12;
                    view.Redraw();

                    ///Set up final bitmap
                    Bitmap cubemap = new Bitmap(imageWidth * 4, imageWidth * 3);

                    ///Place the images on cubemap bitmap
                    using (Graphics gr = Graphics.FromImage(cubemap))
                    {

                        int insertLoc = 0;

                        ///Grab bitmap
                        System.Drawing.Size size = new System.Drawing.Size(imageWidth, imageWidth);
                        ///Initiate camera direction to make left first image
                        camDir.Rotate(-180 * (Math.PI / 180), vp.CameraUp);
                        ///Set rotation amount at 90deg
                        double dirAngle = -90 * (Math.PI / 180);

                        ///Loop through pano directions
                        for (int d = 0; d < 4; d++)
                        {
                            ///Set camera direction
                            camDir.Rotate(dirAngle, vp.CameraUp);
                            vp.SetCameraDirection(camDir, true);

                            ///Redraw
                            view.Redraw();

                            gr.DrawImage(view.CaptureToBitmap(size, false, false, false), insertLoc, imageWidth);

                            insertLoc = insertLoc + imageWidth;

                        }
                        ///Get up and down views

                        ///Get up view
                        vp.SetCameraDirection(-camDir, true);
                        vp.SetCameraDirection(vp.CameraUp, true);
                        ///Redraw
                        view.Redraw();
                        gr.DrawImage(view.CaptureToBitmap(size, false, false, false), imageWidth, 0);

                        ///Get down view
                        camDir.Rotate(dirAngle * 2, camUp);
                        vp.SetCameraDirection(camDir, true);
                        vp.SetCameraDirection(-vp.CameraUp, true);

                        ///Redraw
                        view.Redraw();
                        gr.DrawImage(view.CaptureToBitmap(size, false, false, false), imageWidth, imageWidth * 2);

                    }
                    ///End pano directions loop
                    
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

        }

        camera camFromVP(Rhino.Display.RhinoViewport vp)
        {
            camera c = new camera();
            c.location = vp.CameraLocation;
            c.target = vp.CameraTarget;
            c.up = vp.CameraUp;
            c.lens = vp.Camera35mmLensLength;
            c.parallel = vp.IsParallelProjection;
            return c;
        }

        void setCamera(camera c, Rhino.Display.RhinoViewport vp)
        {
            vp.SetCameraLocations(c.target, c.location);
            vp.CameraUp = c.up;
            vp.Camera35mmLensLength = c.lens;
            if (c.parallel)
            {
                vp.ChangeToParallelProjection(true);
            }
            else
            {
                vp.ChangeToPerspectiveProjection(false, c.lens);
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
            get { return new Guid("69dfa4b6-8176-474f-a958-fe30a17d15d8"); }
        }
    }
}