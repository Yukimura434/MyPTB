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
    public sealed class VideoServiceTests
    {
        [Fact]
        public async Task CreateAsync_WhenEncoderIsDisabled_DoesNotCreateFakeVideo()
        {
            var options = new ApplicationOptions();
            options.Features["VideoNativeEncoder"] = false;
            var service = new VideoService(options);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync("still.jpg", "capture.mp4", DateTime.UtcNow, 3, false, 0, CancellationToken.None));

            Assert.Contains("MP4 video encoding is disabled", exception.Message);
            Assert.False(File.Exists("capture.mp4"));
        }

        [Fact]
        public async Task CreateAsync_writes_standalone_mp4_video()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var still = Path.Combine(root, "still.jpg");
                var destination = Path.Combine(root, "capture.mp4");
                byte[] frame;
                using (var bitmap = new Bitmap(64, 64))
                {
                    using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue);
                    using (var stream = new MemoryStream()) { bitmap.Save(stream, ImageFormat.Jpeg); frame = stream.ToArray(); }
                    bitmap.Save(still, ImageFormat.Jpeg);
                }

                var service = new VideoService();
                var shutter = DateTime.UtcNow;
                for (var i = 0; i <= 72; i++) service.AddLiveViewFrame(frame, shutter.AddSeconds(-4).AddTicks(TimeSpan.TicksPerSecond * i / 18));

                await service.CreateAsync(still, destination, shutter, 4, false, 0, CancellationToken.None);

                var bytes = File.ReadAllBytes(destination);
                Assert.True(bytes.Length > 12);
                Assert.Equal(4, Find(bytes, Encoding.ASCII.GetBytes("ftyp")));
                var preview = await service.CreatePreviewVideoAsync(destination, Path.Combine(root, "preview"), CancellationToken.None);
                Assert.Equal(destination, preview); Assert.True(File.Exists(preview));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Live_view_buffer_is_bounded_and_can_be_released()
        {
            var service = new VideoService();
            var frame = new byte[1024];
            var start = DateTime.UtcNow;
            for (var i = 0; i < 400; i++)
                service.AddLiveViewFrame(frame, start.AddTicks(TimeSpan.TicksPerSecond * i / 18));

            Assert.InRange(service.BufferedFrameCount, 1, VideoService.MaximumBufferedFrames);
            Assert.Equal((long)service.BufferedFrameCount * frame.Length, service.BufferedBytes);

            service.ClearLiveViewFrames();
            Assert.Equal(0, service.BufferedFrameCount);
            Assert.Equal(0, service.BufferedBytes);
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
