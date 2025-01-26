using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render;

namespace Heron
{
    internal struct HeronBoxPreviewItem
    {
        public BoundingBox bbox;
        public PointCloud pointCloud;
        public double radius;
    }

    public abstract class HeronBoxPreviewComponent : HeronComponent
    {
        private List<HeronBoxPreviewItem> _previewItems;
        private BoundingBox _box;

        public HeronBoxPreviewComponent(string name, string nickName, string description, string subCategory) : base(name, nickName, description, subCategory)
        {
            _previewItems = new List<HeronBoxPreviewItem>();
        }

        protected override void BeforeSolveInstance()
        {
            _previewItems.Clear();
        }

        public override bool IsPreviewCapable => true;


        internal void AddPreviewItem(BoundingBox bbox)
        {
            _previewItems.Add(new HeronBoxPreviewItem()
            {
                bbox = bbox
            });
        }

        internal void AddPreviewItem(PointCloud pointCloud, double radius)
        {
            _previewItems.Add(new HeronBoxPreviewItem()
            {
                pointCloud = pointCloud,
                radius = radius,
            });
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            foreach (var item in _previewItems)
            {
                if (item.bbox.IsValid) args.Display.DrawBox(item.bbox, Color.Red);  //args.Display.DrawLines(item.bbox.GetEdges(), Color.Red); 
                if (item.pointCloud != null) args.Display.DrawPointCloud(item.pointCloud, (float) item.radius);
            }

            base.DrawViewportWires(args);
        }

    }
}
