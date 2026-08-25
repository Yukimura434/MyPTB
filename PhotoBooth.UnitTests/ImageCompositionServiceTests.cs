using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class ImageCompositionServiceTests
    {
        [Fact]
        public async Task ComposeAsync_UsesExplicitAssignments_AndAllowsPhotoReuse()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PhotoBooth-compose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var overlay = Path.Combine(directory, "frame.png");
                var red = Path.Combine(directory, "red.png");
                var blue = Path.Combine(directory, "blue.png");
                SaveSolid(overlay, Color.Transparent, 20, 10);
                SaveSolid(red, Color.Red, 10, 10);
                SaveSolid(blue, Color.Blue, 10, 10);
                var frame = new Frame
                {
                    SourcePath = overlay, PixelWidth = 20, PixelHeight = 10,
                    Slots = new[]
                    {
                        new FrameSlot { Index = 0, X = 0, Y = 0, Width = 10, Height = 10 },
                        new FrameSlot { Index = 1, X = 10, Y = 0, Width = 10, Height = 10 }
                    }
                };
                var session = new Session { OutputDirectory = directory, CapturedFiles = new[] { red, blue } };
                var assignments = new Dictionary<int, string> { [0] = blue, [1] = blue };

                var output = await new ImageCompositionService().ComposeAsync(
                    session, frame, null, false, assignments, CancellationToken.None);

                using (var result = new Bitmap(output))
                {
                    Assert.Equal(Color.Blue.ToArgb(), result.GetPixel(5, 5).ToArgb());
                    Assert.Equal(Color.Blue.ToArgb(), result.GetPixel(15, 5).ToArgb());
                }
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        static void SaveSolid(string path, Color color, int width, int height)
        {
            using (var image = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
                image.Save(path, ImageFormat.Png);
            }
        }
    }
}
