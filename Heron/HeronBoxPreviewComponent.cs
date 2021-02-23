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
    }

    public abstract class HeronBoxPreviewComponent : HeronComponent
    {
        private List<HeronBoxPreviewItem> _previewItems;

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

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            foreach (var item in _previewItems)
            {
                args.Display.DrawBox(item.bbox, Color.Red);
            }
            base.DrawViewportMeshes(args);
        }

    }
}
