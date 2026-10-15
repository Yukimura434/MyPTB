using System;
namespace PhotoBooth.Core.Models
{
    public sealed class FrameQrSettings
    {
        public bool Enabled { get; set; }
        public double X { get; set; } = 0.8;
        public double Y { get; set; } = 0.8;
        public double Size { get; set; } = 0.1;
        public FrameQrSettings Normalize(int width, int height)
        {
            var w = Math.Max(1, width); var h = Math.Max(1, height);
            var size = Clamp(Size, Math.Min(10d/150,(double)Math.Min(w,h)/Math.Max(w,h)), Math.Min(35d/150, (double)Math.Min(w,h)/Math.Max(w,h)));
            return new FrameQrSettings { Enabled=Enabled, Size=size,
                X=Clamp(X,0,1-size*Math.Max(w,h)/w), Y=Clamp(Y,0,1-size*Math.Max(w,h)/h) };
        }
        static double Clamp(double value,double min,double max) => double.IsNaN(value)||double.IsInfinity(value)?min:Math.Max(min,Math.Min(max,value));
    }
}
