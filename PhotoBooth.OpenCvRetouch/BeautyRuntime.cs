using System;
using OpenCvSharp;
namespace PhotoBooth.OpenCvRetouch
{
    internal static class BeautyRuntime
    {
        // Process-wide OpenCV budget, initialized once before either beauty engine.
        static BeautyRuntime() { Cv2.SetNumThreads(Math.Max(1, Math.Min(2, Environment.ProcessorCount))); }
        internal static void EnsureConfigured() { }
    }
}
