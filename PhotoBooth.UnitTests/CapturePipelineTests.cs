using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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
                var videoService = new FakeVideoService();
                var pipeline = new CapturePipeline(camera, sessions, settings, videoService);

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
                Assert.True(videoService.LastFlipHorizontally);
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
                var pipeline = new CapturePipeline(camera, sessions, settings, new FakeVideoService());

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
                var pipeline = new CapturePipeline(camera, sessions, settings, new FakeVideoService());

                await pipeline.ExecuteAsync(sessions.Session.Id, "cam", workspace, CancellationToken.None);

                var file = Assert.Single(sessions.Session.CapturedFiles);
                Assert.Equal(Path.GetFullPath(workspace), Path.GetDirectoryName(Path.GetFullPath(file)));
                Assert.True(File.Exists(file));
                Assert.Empty(Directory.GetFiles(root, "*.jpg", SearchOption.TopDirectoryOnly));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_does_not_persist_photo_when_video_packaging_fails()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var sessions = new FakeSessionRepository(new Session { Id = Guid.NewGuid(), SessionNumber = 4, OutputDirectory = root, StartedAtUtc = DateTime.UtcNow });
                var pipeline = new CapturePipeline(new CapturingCamera(), sessions, new FakeSettingsService(new Settings()), new FailingVideoService());
                await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(sessions.Session.Id, "cam", CancellationToken.None));
                Assert.Empty(sessions.Session.CapturedFiles);
                Assert.Empty(Directory.GetFiles(root, "*.mp4"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_applies_lut_only_to_picture_and_keeps_video_primary_raw()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var sessions = new FakeSessionRepository(new Session { Id = Guid.NewGuid(), PresetId = Guid.NewGuid(), SessionNumber = 5, OutputDirectory = root, StartedAtUtc = DateTime.UtcNow });
                var pipeline = new CapturePipeline(new CapturingCamera(), sessions, new FakeSettingsService(new Settings()), new FakeVideoService(), new GreenLutService());
                await pipeline.ExecuteAsync(sessions.Session.Id, "cam", CancellationToken.None);
                var shot = Assert.Single(sessions.Session.CapturedShots); var picture = shot.PicturePath; var video = shot.VideoPath;
                using (var image = new Bitmap(picture)) AssertNear(Color.Lime, image.GetPixel(0, 0));
                using (var image = new Bitmap(video)) { AssertNear(Color.Red, image.GetPixel(0, 0)); AssertNear(Color.Blue, image.GetPixel(3, 0)); }
                Assert.Equal(shot.Id, Assert.Single(sessions.Session.CapturedImageIds));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_when_video_module_is_disabled_keeps_picture_flow_working()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var sessions = new FakeSessionRepository(new Session { Id = Guid.NewGuid(), SessionNumber = 6, OutputDirectory = root, StartedAtUtc = DateTime.UtcNow });
                var pipeline = new CapturePipeline(new CapturingCamera(), sessions, new FakeSettingsService(new Settings()), new FailingVideoService(), null, new FakeFeatureFlagService(false));
                await pipeline.ExecuteAsync(sessions.Session.Id, "cam", CancellationToken.None);
                var shot = Assert.Single(sessions.Session.CapturedShots); Assert.False(shot.HasVideo);
                Assert.Single(sessions.Session.CapturedFiles); Assert.Empty(sessions.Session.CapturedVideoFiles);
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
            public FakeSessionRepository(Session session) { Session = session; Session.CapturedShots = Array.Empty<CapturedShot>(); Project(); }
            public Task<Session> GetAsync(Guid id, CancellationToken t) => Task.FromResult(Session);
            public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<Session>>(new[] { Session });
            public Task SaveAsync(Session s, CancellationToken t) { Session = s; return Task.CompletedTask; }
            public Task SetDefaultAsync(Guid id, CancellationToken t) => Task.CompletedTask;
            public Task<int> GetNextCaptureSequenceAsync(Guid id, CancellationToken t) => Task.FromResult(1);
            public Task AddCapturedShotAsync(Guid sessionId, CapturedShot shot, CancellationToken t)
            {
                Session.CapturedShots = (Session.CapturedShots ?? Array.Empty<CapturedShot>()).Concat(new[] { shot }).OrderBy(x => x.Sequence).ToArray();
                Project();
                return Task.CompletedTask;
            }
            public Task ReplaceCapturedShotAsync(Guid sessionId, string previousShotId, CapturedShot replacement, CancellationToken t)
            {
                var shots = (Session.CapturedShots ?? Array.Empty<CapturedShot>()).Where(x => x.Id == previousShotId || x.Id != replacement.Id).ToList();
                var index = shots.FindIndex(x => x.Id == previousShotId); if (index < 0) throw new InvalidOperationException("Captured shot not found.");
                shots[index] = replacement; Session.CapturedShots = shots.OrderBy(x => x.Sequence).ToArray(); Project(); return Task.CompletedTask;
            }
            void Project() { var shots = Session.CapturedShots ?? Array.Empty<CapturedShot>(); Session.CapturedFiles = shots.Select(x => x.PicturePath).ToArray(); Session.CapturedVideoFiles = shots.Where(x => x.HasVideo).Select(x => x.VideoPath).ToArray(); Session.CapturedImageIds = shots.Select(x => x.Id).ToArray(); }
        }

        sealed class FakeSettingsService : ISettingsService
        {
            readonly Settings value;
            public FakeSettingsService(Settings value) { this.value = value; }
            public Task<Settings> GetAsync(CancellationToken t) => Task.FromResult(value);
            public Task SaveAsync(Settings s, CancellationToken t) => Task.CompletedTask;
        }

        sealed class FakeFeatureFlagService : IFeatureFlagService
        {
            readonly bool enabled; public FakeFeatureFlagService(bool enabled) { this.enabled = enabled; }
            public Task<bool> IsEnabledAsync(string feature, CancellationToken token) => Task.FromResult(enabled);
        }

        sealed class GreenLutService : IColorLutService
        {
            public Task ApplyCaptureAsync(Guid presetId, string imagePath, CancellationToken token) { using (var image = new Bitmap(4, 2)) { using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Lime); image.Save(imagePath, ImageFormat.Jpeg); } return Task.CompletedTask; }
            public Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ColorLutAsset>>(new ColorLutAsset[0]);
            public Task<ColorLutData> GetLiveAsync(Guid presetId, CancellationToken token) => Task.FromResult<ColorLutData>(null);
            public Task<ColorLutImportResult> ImportAsync(string sourcePath, string displayName, CancellationToken token) => Task.FromResult<ColorLutImportResult>(null);
            public Task AttachAsync(Guid presetId, Guid assetId, float strength, CancellationToken token) => Task.CompletedTask;
            public Task DetachAsync(Guid presetId, CancellationToken token) => Task.CompletedTask;
            public Task DeleteAsync(Guid assetId, long expectedRowVersion, CancellationToken token) => Task.CompletedTask;
            public Task ReconcileAsync(CancellationToken token) => Task.CompletedTask;
        }

        sealed class FakeVideoService : IVideoService
        {
            public bool LastFlipHorizontally { get; private set; }
            public void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc) { }
            public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, bool flipHorizontally, CancellationToken token)
            {
                LastFlipHorizontally = flipHorizontally;
                File.Copy(stillImagePath, destinationPath, true);
                return Task.CompletedTask;
            }
            public Task ComposeAsync(string stillCompositePath, Frame frame, IReadOnlyDictionary<int, string> slotAssignments, string destinationPath, CancellationToken token) => Task.CompletedTask;
            public Task<string> CreatePreviewVideoAsync(string videoPath,string previewDirectory,CancellationToken token)=>Task.FromResult<string>(null);
        }

        sealed class FailingVideoService : IVideoService
        {
            public void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc) { }
            public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, bool flipHorizontally, CancellationToken token)
            {
                throw new InvalidOperationException("encoder failed");
            }
            public Task ComposeAsync(string stillCompositePath, Frame frame, IReadOnlyDictionary<int, string> slotAssignments, string destinationPath, CancellationToken token) => throw new InvalidOperationException("encoder failed");
            public Task<string> CreatePreviewVideoAsync(string videoPath,string previewDirectory,CancellationToken token)=>throw new InvalidOperationException("encoder failed");
        }
    }
}
