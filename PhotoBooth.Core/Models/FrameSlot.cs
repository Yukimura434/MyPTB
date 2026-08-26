using System;

namespace PhotoBooth.Core.Models
{
    public sealed class FrameSlot
    {
        public Guid Id { get; set; }
        public int Index { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;
        // Runtime-only crop state. 1.0 is the initial Cover crop; 2.0 is the
        // maximum supported magnification. The database intentionally keeps
        // storing only frame geometry.
        public double MediaZoom { get; set; } = 1d;
        public double MediaCenterX { get; set; } = 0.5d;
        public double MediaCenterY { get; set; } = 0.5d;
    }
}
