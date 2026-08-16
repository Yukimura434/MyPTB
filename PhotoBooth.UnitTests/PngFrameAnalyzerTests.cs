using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using PhotoBooth.Core.Models;
using PhotoBooth.FrameEngine;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class PngFrameAnalyzerTests
    {
        [Fact]
        public void Analyze_detects_internal_transparent_holes_and_filters_small_regions()
        {
            using (var image = OpaqueImage(300, 200))
            {
                Clear(image, 20, 30, 100, 120);
                Clear(image, 180, 40, 80, 100);
                Clear(image, 150, 10, 3, 3);
                var frame = Analyze(image, new FrameAnalysisOptions { MinimumArea = 100, MinimumWidth = 10, MinimumHeight = 10 });
                Assert.Equal(2, frame.Slots.Count);
                Assert.Equal(20, frame.Slots[0].X);
                Assert.Equal(100, frame.Slots[0].Width);
            }
        }

        [Fact]
        public void Analyze_ignores_border_transparency()
        {
            using (var image = OpaqueImage(100, 100))
            {
                Clear(image, 0, 10, 30, 30);
                var frame = Analyze(image, new FrameAnalysisOptions { MinimumArea = 1, MinimumWidth = 1, MinimumHeight = 1, IgnoreBorderConnectedRegions = true });
                Assert.Empty(frame.Slots);
            }
        }

        [Fact]
        public void Analyze_never_returns_more_than_eight_slots()
        {
            using (var image = OpaqueImage(500, 100))
            {
                for (var i = 0; i < 10; i++) Clear(image, 5 + i * 49, 20, 30, 40);
                var frame = Analyze(image, new FrameAnalysisOptions { MinimumArea = 1, MinimumWidth = 1, MinimumHeight = 1, MaximumSlots = 99 });
                Assert.Equal(8, frame.Slots.Count);
            }
        }

        private static Frame Analyze(Bitmap image, FrameAnalysisOptions options)
        {
            using (var stream = new MemoryStream()) { image.Save(stream, ImageFormat.Png); stream.Position = 0; return new PngFrameAnalyzer().Analyze(stream, "test.png", options); }
        }
        private static Bitmap OpaqueImage(int width, int height) { var value = new Bitmap(width, height, PixelFormat.Format32bppArgb); using (var g = Graphics.FromImage(value)) g.Clear(Color.Black); return value; }
        private static void Clear(Bitmap image, int x, int y, int width, int height) { for (var yy = y; yy < y + height; yy++) for (var xx = x; xx < x + width; xx++) image.SetPixel(xx, yy, Color.Transparent); }
    }
}
