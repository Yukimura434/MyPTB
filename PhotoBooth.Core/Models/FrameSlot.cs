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
    }
}
