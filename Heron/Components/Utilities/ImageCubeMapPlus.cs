using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;
using Rhino.Display;
using Rhino.DocObjects.Tables;

namespace Heron
{
    public class ImageCubeMapPlus : HeronComponent
    {
        /// <summary>
        /// Initializes a new instance of the ImageCubeMap class.
        /// </summary>
        public ImageCubeMapPlus()
          : base("Cubemap From View Plus", "CM+",
              "Generate a cubemap from a given plane using the specified display mode.  This component is also able to visualize ray casting based on colors in the cubemap.",
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
            pManager.AddTextParameter("Folder Path", "folderPath", "Folder path for exported cube maps. If no path is provided, no cubemap will be saved.  A ray casting visualization is still possible by added meshes to the Mesh Obstacles input.", GH_ParamAccess.item);
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for exported cube maps.", GH_ParamAccess.item, "cubemap");
            pManager.AddIntegerParameter("Resolution", "res", "The width resolution of the cube map.", GH_ParamAccess.item, 1024);
            pManager.AddTextParameter("Display Mode", "displayMode", "Set the display mode to be used when creating the cubemap.  If no display mode is set or does not exist in the document, the active view's display mode will be used.", GH_ParamAccess.item);
            pManager.AddColourParameter("Color Filter", "colors", "Filter the analysis for specific colors.  If no filter colors are provided, all colors in the cubemap will be included.", GH_ParamAccess.list);
            pManager.AddMeshParameter("Mesh Obstacles", "obstacles", "Mesh obstacles for ray casting vizualization. If no meshes are provided, no visualization will show.", GH_ParamAccess.tree);
            pManager.AddBooleanParameter("Run", "run", "Go ahead and run the component", GH_ParamAccess.item);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
            pManager[6].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Cubemap", "cubemap", "File location for the cubemap.", GH_ParamAccess.list);
            pManager.AddLineParameter("Rays", "rays", "Rays resulting from analysis.", GH_ParamAccess.tree);
            pManager.AddColourParameter("Ray Colors", "resultColors", "Colors from analysis", GH_ParamAccess.tree);

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
            bool saveCubemaps = !string.IsNullOrEmpty(folder);
            if (saveCubemaps)
            {
                folder = Path.GetFullPath(folder);
                if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString())) { folder += Path.DirectorySeparatorChar; }
            }

            string prefix = string.Empty;
            DA.GetData<string>(2, ref prefix);

            int imageWidth = 0;
            DA.GetData<int>(3, ref imageWidth);
            imageWidth = imageWidth / 4;
            Size size = new Size(imageWidth, imageWidth);

            string displayMode = string.Empty;
            DA.GetData<string>(4, ref displayMode);

            List<Color> colors = new List<Color>();
            DA.GetDataList<Color>(5, colors);
            bool filterColors = colors.Any();

            GH_Structure<GH_Mesh> ghObstacles = new GH_Structure<GH_Mesh>();
            DA.GetDataTree<GH_Mesh>(6, out ghObstacles);

            ///Flatten obstacle meshes and join them into one mesh
            ghObstacles.FlattenData();
            Mesh obstacles = new Mesh();
            bool showRays = false;
            if (ghObstacles.DataCount > 0)
            {
                showRays = true;
                foreach (var obstacle in ghObstacles)
                {
                    Mesh temp = new Mesh();
                    GH_Convert.ToMesh(obstacle, ref temp, GH_Conversion.Primary);
                    obstacles.Append(temp);
                }
            }


            bool run = false;
            DA.GetData<bool>(7, ref run);

            int pad = camPlanes.Count.ToString().Length;

            List<string> cubemaps = new List<string>();

            GH_Structure<GH_Line> rayTree = new GH_Structure<GH_Line>();
            GH_Structure<GH_Colour> colorTree = new GH_Structure<GH_Colour>();

            ///Save the intial camera
            saveCam = camFromVP(Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport);

            ///Set the display mode to be used for bitmaps
            ///TODO: Add menu item to use "Heron View Analysis" display mode
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
                    ///TODO: setup ability to save cameras to the Rhino doc
                    ///Setup camera
                    Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView;
                    Rhino.Display.RhinoViewport vp = view.ActiveViewport;

                    ///Get the bounding box of all visible object in the doc for use in setting up the camera 
                    ///target so that the far frustrum plane doesn't clip anything
                    double zoomDistance = Rhino.RhinoDoc.ActiveDoc.Objects.BoundingBoxVisible.Diagonal.Length;

                    Plane camPlane = camPlanes[i];
                    Point3d camPoint = camPlane.Origin;
                    Vector3d camDir = camPlane.YAxis;
                    Point3d tarPoint = Transform.Translation(camDir * zoomDistance / 2) * camPoint;


                    vp.ChangeToPerspectiveProjection(false, 12.0);
                    vp.Size = size;
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
                            //view.Redraw();

                            Bitmap bitmap = new Bitmap(view.CaptureToBitmap(size, viewMode));

                            if (saveCubemaps) { gr.DrawImage(bitmap, insertLoc, imageWidth); }

                            if (showRays)
                            {
                                GH_MemoryBitmap sampler = new GH_MemoryBitmap(bitmap);
                                Color col = Color.Transparent;
                                for (int x = 0; x < bitmap.Width; x++)
                                {
                                    for (int y = 0; y < bitmap.Height; y++)
                                    {
                                        if (sampler.Sample(x, y, ref col))
                                        {
                                            if (colors.Contains(col))
                                            {
                                                GH_Path path = new GH_Path(i, colors.IndexOf(col));

                                                Line line = vp.ClientToWorld(new System.Drawing.Point(x, y));

                                                Ray3d ray = new Ray3d(vp.CameraLocation, -line.Direction);
                                                double rayEnd = (double)Rhino.Geometry.Intersect.Intersection.MeshRay(obstacles, ray);
                                                Point3d rayIntersection = ray.PointAt(rayEnd);
                                                Line ln = new Line(camPoint, rayIntersection);

                                                if (ln.IsValid & rayEnd > 0)
                                                {
                                                    rayTree.Append(new GH_Line(ln), path);
                                                    colorTree.Append(new GH_Colour(col), path);
                                                }

                                            }
                                            else if (!filterColors)
                                            {
                                                colors.Add(col);
                                                GH_Path path = new GH_Path(i, colors.IndexOf(col));

                                                Line line = vp.ClientToWorld(new System.Drawing.Point(x, y));

                                                Ray3d ray = new Ray3d(vp.CameraLocation, -line.Direction);
                                                double rayEnd = (double)Rhino.Geometry.Intersect.Intersection.MeshRay(obstacles, ray);
                                                Point3d rayIntersection = ray.PointAt(rayEnd);
                                                Line ln = new Line(camPoint, rayIntersection);

                                                if (ln.IsValid & rayEnd > 0)
                                                {
                                                    rayTree.Append(new GH_Line(ln), path);
                                                    colorTree.Append(new GH_Colour(col), path);
                                                }

                                            }
                                        }
                                    }
                                }
                                sampler.Release(false);
                            }

                            insertLoc = insertLoc + imageWidth;

                            bitmap.Dispose();

                        }


                        ///Get up and down views

                        ///Get up view
                        vp.SetCameraLocations(tarUp, camPoint);
                        view.Redraw();

                        Bitmap bitmapUp = new Bitmap(view.CaptureToBitmap(size, viewMode));

                        if (showRays)
                        {
                            GH_MemoryBitmap sampler = new GH_MemoryBitmap(bitmapUp);
                            Color col = Color.Transparent;
                            for (int x = 0; x < bitmapUp.Width; x++)
                            {
                                for (int y = 0; y < bitmapUp.Height; y++)
                                {
                                    if (sampler.Sample(x, y, ref col))
                                    {
                                        if (colors.Contains(col))
                                        {
                                            GH_Path path = new GH_Path(i, colors.IndexOf(col));

                                            Line line = vp.ClientToWorld(new System.Drawing.Point(x, y));

                                            Ray3d ray = new Ray3d(vp.CameraLocation, -line.Direction);
                                            double rayEnd = (double)Rhino.Geometry.Intersect.Intersection.MeshRay(obstacles, ray);
                                            Point3d rayIntersection = ray.PointAt(rayEnd);
                                            Line ln = new Line(camPoint, rayIntersection);

                                            if (ln.IsValid & rayEnd > 0)
                                            {
                                                rayTree.Append(new GH_Line(ln), path);
                                                colorTree.Append(new GH_Colour(col), path);
                                            }

                                        }
                                        else if (!filterColors)
                                        {
                                            colors.Add(col);
                                            GH_Path path = new GH_Path(i, colors.IndexOf(col));

                                            Line line = vp.ClientToWorld(new System.Drawing.Point(x, y));

                                            Ray3d ray = new Ray3d(vp.CameraLocation, -line.Direction);
                                            double rayEnd = (double)Rhino.Geometry.Intersect.Intersection.MeshRay(obstacles, ray);
                                            Point3d rayIntersection = ray.PointAt(rayEnd);
                                            Line ln = new Line(camPoint, rayIntersection);

                                            if (ln.IsValid & rayEnd > 0)
                                            {
                                                rayTree.Append(new GH_Line(ln), path);
                                                colorTree.Append(new GH_Colour(col), path);
                                            }

                                        }
                                    }
                                }
                            }
                            sampler.Release(false);
                        }

                        bitmapUp.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        if (saveCubemaps) { gr.DrawImage(bitmapUp, imageWidth, 0); }

                        bitmapUp.Dispose();


                        ///Get down view
                        vp.SetCameraLocations(tarDown, camPoint);
                        view.Redraw();

                        Bitmap bitmapDown = new Bitmap(view.CaptureToBitmap(size, viewMode));

                        if (saveCubemaps) { gr.DrawImage(bitmapDown, imageWidth, imageWidth * 2); }

                        if (showRays)
                        {
                            GH_MemoryBitmap sampler = new GH_MemoryBitmap(bitmapDown);
                            Color col = Color.Transparent;
                            for (int x = 0; x < bitmapDown.Width; x++)
                            {
                                for (int y = 0; y < bitmapDown.Height; y++)
                                {
                                    if (sampler.Sample(x, y, ref col))
                                    {
                                        if (colors.Contains(col))
                                        {
                                            GH_Path path = new GH_Path(i, colors.IndexOf(col));

                                            Line line = vp.ClientToWorld(new System.Drawing.Point(x, y));

                                            Ray3d ray = new Ray3d(vp.CameraLocation, -line.Direction);
                                            double rayEnd = (double)Rhino.Geometry.Intersect.Intersection.MeshRay(obstacles, ray);
                                            Point3d rayIntersection = ray.PointAt(rayEnd);
                                            Line ln = new Line(camPoint, rayIntersection);

                                            if (ln.IsValid & rayEnd > 0)
                                            {
                                                rayTree.Append(new GH_Line(ln), path);
                                                colorTree.Append(new GH_Colour(col), path);
                                            }

                                        }

                                        else if (!filterColors)
                                        {
                                            colors.Add(col);
                                            GH_Path path = new GH_Path(i, colors.IndexOf(col));

                                            Line line = vp.ClientToWorld(new System.Drawing.Point(x, y));

                                            Ray3d ray = new Ray3d(vp.CameraLocation, -line.Direction);
                                            double rayEnd = (double)Rhino.Geometry.Intersect.Intersection.MeshRay(obstacles, ray);
                                            Point3d rayIntersection = ray.PointAt(rayEnd);
                                            Line ln = new Line(camPoint, rayIntersection);

                                            if (ln.IsValid & rayEnd > 0)
                                            {
                                                rayTree.Append(new GH_Line(ln), path);
                                                colorTree.Append(new GH_Colour(col), path);
                                            }

                                        }
                                    }
                                }
                            }
                            sampler.Release(false);
                        }

                        bitmapDown.Dispose();

                    }
                    ///End pano directions loop

                    if (saveCubemaps)
                    {
                        ///Save cubemap bitmap
                        string s = i.ToString().PadLeft(pad, '0');
                        string saveText = folder + prefix + "_" + s + ".png";
                        cubemap.Save(saveText, System.Drawing.Imaging.ImageFormat.Png);
                        cubemaps.Add(saveText);
                    }
                    cubemap.Dispose();

                }
            }

            ///Restore initial camera
            setCamera(saveCam, Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport);
            Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.Redraw();

            DA.SetDataList(0, cubemaps);
            DA.SetDataTree(1, rayTree);
            DA.SetDataTree(2, colorTree);
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
            get { return new Guid("B1624853-BA20-442D-9EAA-C2D215B14C30"); }
        }
    }
}