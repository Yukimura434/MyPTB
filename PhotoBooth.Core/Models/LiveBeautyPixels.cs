using System;

namespace PhotoBooth.Core.Models
{
    /// <summary>Owned packed BGR24 pixels. Consumers must not mutate the buffer.</summary>
    public sealed class LiveBeautyPixels
    {
        public LiveBeautyPixels(byte[] pixels, int width, int height, bool beautyApplied)
        {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length != checked(width * height * 3))
                throw new ArgumentException("Invalid packed BGR24 frame.");
            Pixels = pixels; Width = width; Height = height; BeautyApplied = beautyApplied;
        }
        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 3;
        public bool BeautyApplied { get; }
    }
}
