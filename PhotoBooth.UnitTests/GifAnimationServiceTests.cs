using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using PhotoBooth.Infrastructure.Services;
using System.Windows.Media.Imaging;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class GifAnimationServiceTests
    {
        [Fact]
        public async System.Threading.Tasks.Task CreateAsync_WritesEveryInputAsAnAnimatedFrame()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PhotoBooth-Gif-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var first = Path.Combine(directory, "first.png");
                var second = Path.Combine(directory, "second.png");
                SaveSolid(first, Color.Red);
                SaveSolid(second, Color.Blue);
                var output = Path.Combine(directory, "capture.gif");

                await new GifAnimationService().CreateAsync(
                    new[] { first, second }, output, 1200, CancellationToken.None);

                using (var stream = File.OpenRead(output))
                {
                    var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    Assert.Equal(2, decoder.Frames.Count);
                    Assert.Equal((ushort)120, ((BitmapMetadata)decoder.Frames[0].Metadata).GetQuery("/grctlext/Delay"));
                    Assert.Equal((ushort)120, ((BitmapMetadata)decoder.Frames[1].Metadata).GetQuery("/grctlext/Delay"));
                }

                using (var image = Image.FromFile(output))
                {
                    Assert.Equal(2, image.GetFrameCount(FrameDimension.Time));
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void SaveSolid(string path, Color color)
        {
            using (var bitmap = new Bitmap(32, 24))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(color);
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}
