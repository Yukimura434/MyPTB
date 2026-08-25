using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class MotionPhotoServiceTests
    {
        [Fact]
        public async Task CreateAsync_WhenEncoderIsDisabled_DoesNotCreateFakeMotionPhoto()
        {
            var options = new ApplicationOptions();
            options.Features["MotionPhotoNativeEncoder"] = false;
            var service = new MotionPhotoService(options);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync("still.jpg", "capture_MP.jpg", DateTime.UtcNow, CancellationToken.None));

            Assert.Contains("static JPEG will not be saved", exception.Message);
            Assert.False(File.Exists("capture_MP.jpg"));
        }

        [Fact]
        public async Task CreateAsync_writes_single_motion_photo_with_xmp_and_appended_mp4()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var still = Path.Combine(root, "still.jpg");
                var destination = Path.Combine(root, "capture_MP.jpg");
                byte[] frame;
                using (var bitmap = new Bitmap(64, 64))
                {
                    using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue);
                    using (var stream = new MemoryStream()) { bitmap.Save(stream, ImageFormat.Jpeg); frame = stream.ToArray(); }
                    bitmap.Save(still, ImageFormat.Jpeg);
                }

                var service = new MotionPhotoService();
                var shutter = DateTime.UtcNow;
                for (var i = 0; i <= 54; i++) service.AddLiveViewFrame(frame, shutter.AddSeconds(-3).AddTicks(TimeSpan.TicksPerSecond * i / 18));

                await service.CreateAsync(still, destination, shutter, CancellationToken.None);

                var bytes = File.ReadAllBytes(destination);
                Assert.True(bytes.Length > new FileInfo(still).Length);
                var text = Encoding.UTF8.GetString(bytes);
                Assert.Contains("Camera:MotionPhoto=\"1\"", text);
                Assert.Contains("Item:Semantic=\"MotionPhoto\"", text);
                Assert.True(Find(bytes, Encoding.ASCII.GetBytes("ftyp")) > 0);
                using (var image = new Bitmap(destination)) Assert.Equal(new Size(64, 64), image.Size);
            }
            finally { Directory.Delete(root, true); }
        }

        static int Find(byte[] source, byte[] value)
        {
            for (var i = 0; i <= source.Length - value.Length; i++)
            {
                var match = true;
                for (var j = 0; j < value.Length; j++) if (source[i + j] != value[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }
    }
}
