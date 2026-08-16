using System;

namespace PhotoBooth.Core.Cameras
{
    public sealed class LiveViewFrame
    {
        public byte[] ImageData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Rotation { get; set; }
        public int FocusX { get; set; }
        public int FocusY { get; set; }
        public bool IsFocused { get; set; }
        public DateTime TimestampUtc { get; set; }
    }
}
