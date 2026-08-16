using System;
using System.Drawing;
using Canon.Eos.Framework.Helper;
using Canon.Eos.Framework.Internal.SDK;

namespace Canon.Eos.Framework
{
    public class EosLiveImage : EosObject
    {
        internal static EosLiveImage CreateFromStream(IntPtr stream)
        {
            IntPtr imagePtr;
            Util.Assert(Edsdk.EdsCreateEvfImageRef(stream, out imagePtr), "Failed to create evf image.");
            return new EosLiveImage(imagePtr);    
        }

        internal EosLiveImage(IntPtr imagePtr)
            : base(imagePtr) { }

        [EosProperty(Edsdk.PropID_Evf_ImagePosition)]
        public Point ImagePosition
        {
            get { Edsdk.EdsPoint value; return TryGetPropertyStruct(Edsdk.PropID_Evf_ImagePosition, out value) ? new Point(value.x, value.y) : Point.Empty; }
        }

        [EosProperty(Edsdk.PropID_Evf_Zoom)]
        public long[] Histogram
        {
            get { return this.GetPropertyIntegerArrayData(Edsdk.PropID_Evf_Histogram); }
        }

        [EosProperty(Edsdk.PropID_Evf_Zoom)]
        public long Zoom
        {
            get { uint value; return Edsdk.EdsGetPropertyData(Handle, Edsdk.PropID_Evf_Zoom, 0, out value) == Edsdk.EDS_ERR_OK ? value : 0; }
        }

        [EosProperty(Edsdk.PropID_Evf_ZoomRect)]
        public Rectangle ZoomBounds
        {
            get { Edsdk.EdsRect value; return TryGetPropertyStruct(Edsdk.PropID_Evf_ZoomRect, out value) ? new Rectangle(value.x, value.y, value.width, value.height) : Rectangle.Empty; }
        }

        [EosProperty(Edsdk.PropID_Evf_ZoomPosition)]
        public Point ZoomPosition
        {
            get { Edsdk.EdsPoint value; return TryGetPropertyStruct(Edsdk.PropID_Evf_ZoomPosition, out value) ? new Point(value.x, value.y) : Point.Empty; }
        }

        [EosProperty(Edsdk.PropID_Evf_CoordinateSystem)]
        public Size Size
        {
            get { Edsdk.EdsSize value; return TryGetPropertyStruct(Edsdk.PropID_Evf_CoordinateSystem, out value) ? new Size(value.width, value.height) : Size.Empty; }
        }
    }
}
