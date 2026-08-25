using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Pipelines;using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CapturePipelineTests
    {
        [Fact]
        public async Task ExecuteAsync_flips_horizontally_before_saving_when_autoflip_enabled()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var camera = new CapturingCamera();
                var settings = new FakeSettingsService(new Settings { AutoFlip = true, SaveLocation = CameraSaveMode.PcOnly });
                var sessions = new FakeSessionRepository(new Session
                {
                    Id = Guid.NewGuid(),
                    SessionName = "Test",
                    SessionNumber = 1,
                    OutputDirectory = root,
                    StartedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local)
                });
                var pipeline = new CapturePipeline(camera, sessions, settings, new FakeMotionPhotoService());

                var result = await pipeline.ExecuteAsync(sessions.Session.Id, "cam", CancellationToken.None);

                var file = Assert.Single(sessions.Session.CapturedFiles);
                using (var image = new Bitmap(file))
                {
                    Assert.Equal(4, image.Width);
                    Assert.Equal(2, image.Height);
                    AssertNear(Color.Red, image.GetPixel(3, 0));
                    AssertNear(Color.Red, image.GetPixel(3, 1));
                    AssertNear(Color.Blue, image.GetPixel(0, 0));
                    AssertNear(Color.Blue, image.GetPixel(0, 1));
                }
                Assert.DoesNotContain(".staging.", file);
                Assert.False(File.Exists(file + ".staging.jpg"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_does_not_flip_when_disabled_and_saves_to_destination()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var camera = new CapturingCamera();
                var settings = new FakeSettingsService(new Settings { AutoFlip = false, SaveLocation = CameraSaveMode.PcOnly });
                var sessions = new FakeSessionRepository(new Session
                {
                    Id = Guid.NewGuid(),
                    SessionName = "Test",
                    SessionNumber = 2,
                    OutputDirectory = root,
                    StartedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local)
                });
                var pipeline = new CapturePipeline(camera, sessions, settings, new FakeMotionPhotoService());

                await pipeline.ExecuteAsync(sessions.Session.Id, "cam", CancellationToken.None);

                var file = Assert.Single(sessions.Session.CapturedFiles);
                using (var image = new Bitmap(file))
                {
                    AssertNear(Color.Red, image.GetPixel(0, 0));
                    AssertNear(Color.Blue, image.GetPixel(3, 0));
                }
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_saves_customer_capture_inside_requested_workspace()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "Session");
            Directory.CreateDirectory(root);
            try
            {
                var camera = new CapturingCamera();
                var settings = new FakeSettingsService(new Settings { SaveLocation = CameraSaveMode.PcOnly });
                var sessions = new FakeSessionRepository(new Session
                {
                    Id = Guid.NewGuid(),
                    SessionName = "Test",
                    SessionNumber = 3,
                    OutputDirectory = root,
                    StartedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local)
                });
                var pipeline = new CapturePipeline(camera, sessions, settings, new FakeMotionPhotoService());

                await pipeline.ExecuteAsync(sessions.Session.Id, "cam", workspace, CancellationToken.None);

                var file = Assert.Single(sessions.Session.CapturedFiles);
                Assert.Equal(Path.GetFullPath(workspace), Path.GetDirectoryName(Path.GetFullPath(file)));
                Assert.True(File.Exists(file));
                Assert.Empty(Directory.GetFiles(root, "*.jpg", SearchOption.TopDirectoryOnly));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_does_not_persist_photo_when_motion_packaging_fails()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var sessions = new FakeSessionRepository(new Session { Id = Guid.NewGuid(), SessionNumber = 4, OutputDirectory = root, StartedAtUtc = DateTime.UtcNow });
                var pipeline = new CapturePipeline(new CapturingCamera(), sessions, new FakeSettingsService(new Settings()), new FailingMotionPhotoService());
                await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(sessions.Session.Id, "cam", CancellationToken.None));
                Assert.Empty(sessions.Session.CapturedFiles);
                Assert.Empty(Directory.GetFiles(root, "*_MP.jpg"));
            }
            finally { Directory.Delete(root, true); }
        }

        static void AssertNear(Color expected, Color actual, int tolerance = 30)
        {
            Assert.InRange(Math.Abs(actual.R - expected.R), 0, tolerance);
            Assert.InRange(Math.Abs(actual.G - expected.G), 0, tolerance);
            Assert.InRange(Math.Abs(actual.B - expected.B), 0, tolerance);
        }

        sealed class CapturingCamera : ICameraService
        {
            public event EventHandler CamerasChanged;
            public Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<CameraInfo>>(Array.Empty<CameraInfo>());
            public Task ConnectAsync(CancellationToken t) => Task.CompletedTask;
            public Task ConnectAsync(string id, CancellationToken t) => Task.CompletedTask;
            public Task<IReadOnlyList<CameraInfo>> ScanAsync(CancellationToken t) => GetCamerasAsync(t);
            public Task DisconnectAsync(CancellationToken t) => Task.CompletedTask;
            public Task<CameraProperties> GetPropertiesAsync(string id, CancellationToken t) => Task.FromResult<CameraProperties>(null);
            public Task SetPropertyAsync(string id, CameraPropertyKind p, string v, CancellationToken t) => Task.CompletedTask;
            public Task<CaptureResult> CaptureAsync(string id, bool focus, CancellationToken t) => CaptureAsync(id, focus, null, t);
            public Task<CaptureResult> CaptureAsync(string id, bool focus, string destination, CancellationToken t) => CaptureAsync(id, focus, destination, CameraSaveMode.PcOnly, t);
            public Task<CaptureResult> CaptureAsync(string id, bool focus, string destination, CameraSaveMode mode, CancellationToken t)
            {
                using (var b = new Bitmap(4, 2, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.White);
                        g.FillRectangle(Brushes.Red, 0, 0, 2, 2);
                        g.FillRectangle(Brushes.Blue, 2, 0, 2, 2);
                    }
                    b.Save(destination, ImageFormat.Jpeg);
                }
                return Task.FromResult(new CaptureResult { Succeeded = true, CameraId = id, FileName = destination });
            }
        }

        sealed class FakeSessionRepository : ISessionRepository
        {
            public Session Session { get; private set; }
            public FakeSessionRepository(Session session) { Session = session; Session.CapturedFiles = Array.Empty<string>(); Session.CapturedImageIds = Array.Empty<string>(); }
            public Task<Session> GetAsync(Guid id, CancellationToken t) => Task.FromResult(Session);
            public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<Session>>(new[] { Session });
            public Task SaveAsync(Session s, CancellationToken t) { Session = s; return Task.CompletedTask; }
            public Task SetDefaultAsync(Guid id, CancellationToken t) => Task.CompletedTask;
            public Task<int> GetNextCaptureSequenceAsync(Guid id, CancellationToken t) => Task.FromResult(1);
            public Task AddCapturedImageAsync(Guid sessionId, int sequence, string imageId, string filePath, CancellationToken t)
            {
                Session.CapturedFiles = new[] { filePath };
                Session.CapturedImageIds = new[] { imageId };
                return Task.CompletedTask;
            }
        }

        sealed class FakeSettingsService : ISettingsService
        {
            readonly Settings value;
            public FakeSettingsService(Settings value) { this.value = value; }
            public Task<Settings> GetAsync(CancellationToken t) => Task.FromResult(value);
            public Task SaveAsync(Settings s, CancellationToken t) => Task.CompletedTask;
        }

        sealed class FakeMotionPhotoService : IMotionPhotoService
        {
            public void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc) { }
            public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, CancellationToken token)
            {
                File.Copy(stillImagePath, destinationPath, true);
                return Task.CompletedTask;
            }
        }

        sealed class FailingMotionPhotoService : IMotionPhotoService
        {
            public void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc) { }
            public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, CancellationToken token)
            {
                throw new InvalidOperationException("encoder failed");
            }
        }
    }
}
