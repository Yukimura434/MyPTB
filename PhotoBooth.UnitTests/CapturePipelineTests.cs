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
                var settings = new FakeSettingsService(new Settings { AutoFlip = true, SaveLocation = CameraSaveMode.PcOnly, CountdownSeconds = 7 });
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
                Assert.Equal(7, videoService.LastDurationSeconds);
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

        [Fact]
        public async Task ExecuteAsync_when_video_is_excluded_keeps_admin_still_capture_working()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var sessions = new FakeSessionRepository(new Session { Id = Guid.NewGuid(), SessionNumber = 9, OutputDirectory = root, StartedAtUtc = DateTime.UtcNow });
                var pipeline = new CapturePipeline(new CapturingCamera(), sessions, new FakeSettingsService(new Settings()), new FailingVideoService());
                await pipeline.ExecuteAsync(sessions.Session.Id, "cam", null, false, CancellationToken.None);
                var shot = Assert.Single(sessions.Session.CapturedShots); Assert.False(shot.HasVideo);
                Assert.True(File.Exists(shot.PicturePath)); Assert.Empty(sessions.Session.CapturedVideoFiles);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_with_explicit_video_duration_uses_manual_capture_duration()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var sessions = new FakeSessionRepository(new Session { Id = Guid.NewGuid(), SessionNumber = 10, OutputDirectory = root, StartedAtUtc = DateTime.UtcNow });
                var video = new FakeVideoService();
                var pipeline = new CapturePipeline(
                    new CapturingCamera(),
                    sessions,
                    new FakeSettingsService(new Settings { CountdownSeconds = 8 }),
                    video);

                await pipeline.ExecuteAsync(sessions.Session.Id, "cam", null, true, 3, CancellationToken.None);

                Assert.Equal(3, video.LastDurationSeconds);
                Assert.Single(sessions.Session.CapturedShots);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task ExecuteAsync_runs_beauty_before_lut_and_keeps_video_raw()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var events=new List<string>();var sessions=new FakeSessionRepository(new Session{Id=Guid.NewGuid(),PresetId=Guid.NewGuid(),SessionNumber=7,OutputDirectory=root,StartedAtUtc=DateTime.UtcNow});
                var pipeline=new CapturePipeline(new CapturingCamera(),sessions,new FakeSettingsService(new Settings()),new FakeVideoService(),new OrderedLutService(events),null,new FakeBeautySettingsService(true),new OrderedBeautyService(events));
                await pipeline.ExecuteAsync(sessions.Session.Id,"cam",CancellationToken.None);
                Assert.Equal(new[]{"beauty","lut"},events);
                using(var video=new Bitmap(Assert.Single(sessions.Session.CapturedShots).VideoPath)){AssertNear(Color.Red,video.GetPixel(0,0));AssertNear(Color.Blue,video.GetPixel(3,0));}
            }
            finally{Directory.Delete(root,true);}
        }

        [Fact]
        public async Task ExecuteAsync_beauty_failure_is_fail_open()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var sessions=new FakeSessionRepository(new Session{Id=Guid.NewGuid(),SessionNumber=8,OutputDirectory=root,StartedAtUtc=DateTime.UtcNow});
                var pipeline=new CapturePipeline(new CapturingCamera(),sessions,new FakeSettingsService(new Settings()),new FakeVideoService(),null,null,new FakeBeautySettingsService(true),new FailingBeautyService());
                await pipeline.ExecuteAsync(sessions.Session.Id,"cam",CancellationToken.None);
                Assert.Single(sessions.Session.CapturedShots);Assert.True(File.Exists(sessions.Session.CapturedShots[0].PicturePath));
            }
            finally{Directory.Delete(root,true);}
        }

        [Fact]
        public async Task ProcessPendingAsync_finishes_media_before_atomic_session_commit()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var events=new List<string>();
                var sessions=new FakeSessionRepository(new Session{Id=Guid.NewGuid(),PresetId=Guid.NewGuid(),SessionNumber=11,OutputDirectory=root,StartedAtUtc=DateTime.UtcNow});
                var pipeline=new CapturePipeline(new CapturingCamera(),sessions,new FakeSettingsService(new Settings()),new FakeVideoService(),new OrderedLutService(events),null,new FakeBeautySettingsService(true),new OrderedBeautyService(events));

                var pending=await pipeline.CapturePendingAsync(sessions.Session.Id,"cam",root,false,3,CancellationToken.None);

                Assert.True(File.Exists(pending.RawPicturePath));
                Assert.False(File.Exists(pending.PicturePath));
                Assert.Empty(events);
                Assert.Empty(sessions.Session.CapturedShots);

                await pipeline.ProcessPendingAsync(pending,CancellationToken.None);

                Assert.Equal(new[]{"beauty","lut"},events);
                Assert.True(pending.IsProcessed);
                Assert.True(File.Exists(pending.RawPicturePath));
                Assert.True(File.Exists(pending.PicturePath));
                Assert.Empty(sessions.Session.CapturedShots);

                var finalized=await pipeline.CommitProcessedAsync(sessions.Session.Id,new[]{pending},CancellationToken.None);

                Assert.Single(finalized);
                Assert.Single(sessions.Session.CapturedShots);
                Assert.False(File.Exists(pending.RawPicturePath));
                Assert.True(File.Exists(pending.PicturePath));
            }
            finally{Directory.Delete(root,true);}
        }

        [Fact]
        public async Task DiscardPendingAsync_removes_unprocessed_capture_without_committing_session()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var sessions=new FakeSessionRepository(new Session{Id=Guid.NewGuid(),SessionNumber=12,OutputDirectory=root,StartedAtUtc=DateTime.UtcNow});
                var pipeline=new CapturePipeline(new CapturingCamera(),sessions,new FakeSettingsService(new Settings()),new FakeVideoService());
                var pending=await pipeline.CapturePendingAsync(sessions.Session.Id,"cam",root,false,3,CancellationToken.None);

                await pipeline.DiscardPendingAsync(new[]{pending},"test cleanup",CancellationToken.None);

                Assert.False(File.Exists(pending.RawPicturePath));
                Assert.Empty(sessions.Session.CapturedShots);
            }
            finally{Directory.Delete(root,true);}
        }

        [Fact]
        public async Task FinalizePendingAsync_processing_failure_does_not_commit_partial_batch()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var sessions=new FakeSessionRepository(new Session{Id=Guid.NewGuid(),PresetId=Guid.NewGuid(),SessionNumber=13,OutputDirectory=root,StartedAtUtc=DateTime.UtcNow});
                var pipeline=new CapturePipeline(new CapturingCamera(),sessions,new FakeSettingsService(new Settings()),new FakeVideoService(),new FailingSecondLutService());
                var first=await pipeline.CapturePendingAsync(sessions.Session.Id,"cam",root,false,3,CancellationToken.None);
                var second=await pipeline.CapturePendingAsync(sessions.Session.Id,"cam",root,false,3,CancellationToken.None);

                await Assert.ThrowsAsync<InvalidOperationException>(()=>pipeline.FinalizePendingAsync(sessions.Session.Id,new[]{first,second},CancellationToken.None));

                Assert.Empty(sessions.Session.CapturedShots);
                Assert.False(File.Exists(first.PicturePath));
                Assert.False(File.Exists(second.PicturePath));
                Assert.False(File.Exists(first.RawPicturePath));
                Assert.False(File.Exists(second.RawPicturePath));
            }
            finally{Directory.Delete(root,true);}
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
            public Task AddCapturedShotAsync(Guid sessionId, CapturedShot shot, CancellationToken t) => AddCapturedShotsAsync(sessionId,new[]{shot},t);
            public Task AddCapturedShotsAsync(Guid sessionId, IReadOnlyList<CapturedShot> added, CancellationToken t)
            {
                Session.CapturedShots = (Session.CapturedShots ?? Array.Empty<CapturedShot>()).Concat(added ?? Array.Empty<CapturedShot>()).OrderBy(x => x.Sequence).ToArray();
                Project();
                return Task.CompletedTask;
            }
            public Task ReplaceCapturedShotAsync(Guid sessionId,string previousShotId,CapturedShot replacement,CancellationToken t)=>ReplaceCapturedShotsAsync(sessionId,new Dictionary<string,CapturedShot>{{previousShotId,replacement}},t);
            public Task ReplaceCapturedShotsAsync(Guid sessionId,IReadOnlyDictionary<string,CapturedShot> replacements,CancellationToken t)
            {
                var shots=(Session.CapturedShots??Array.Empty<CapturedShot>()).ToList();
                foreach(var replacement in replacements){shots.RemoveAll(x=>x.Id==replacement.Value.Id&&x.Id!=replacement.Key);var index=shots.FindIndex(x=>x.Id==replacement.Key);if(index<0)throw new InvalidOperationException("Captured shot not found.");shots[index]=replacement.Value;}
                Session.CapturedShots=shots.OrderBy(x=>x.Sequence).ToArray();Project();return Task.CompletedTask;
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

        sealed class FakeBeautySettingsService:IBeautySettingsService
        {
            readonly bool enabled;public FakeBeautySettingsService(bool value){enabled=value;}
            public event EventHandler<BeautySettingsChangedEventArgs> SettingsChanged;
            public Task<BeautySettings> GetAsync(CancellationToken t)=>Task.FromResult(new BeautySettings{Enabled=enabled,SmoothSkin=30});
            public Task SaveAsync(BeautySettings value,CancellationToken t){SettingsChanged?.Invoke(this,new BeautySettingsChangedEventArgs(value));return Task.CompletedTask;}
        }
        sealed class OrderedBeautyService:IBeautyRetouchService
        {
            readonly List<string> events;public OrderedBeautyService(List<string> value){events=value;}
            public Task<BeautyRetouchResult> ProcessAsync(string input,string output,BeautySettings value,CancellationToken t){events.Add("beauty");using(var image=new Bitmap(4,2)){using(var g=Graphics.FromImage(image))g.Clear(Color.Red);image.Save(output,ImageFormat.Jpeg);}return Task.FromResult(new BeautyRetouchResult{Applied=true,FacesDetected=1});}
        }
        sealed class FailingBeautyService:IBeautyRetouchService{public Task<BeautyRetouchResult> ProcessAsync(string input,string output,BeautySettings value,CancellationToken t)=>Task.FromException<BeautyRetouchResult>(new InvalidOperationException("expected"));}
        sealed class OrderedLutService:IColorLutService
        {
            readonly List<string> events;public OrderedLutService(List<string> value){events=value;}
            public Task ApplyCaptureAsync(Guid id,string path,CancellationToken t){events.Add("lut");using(var image=new Bitmap(path))AssertNear(Color.Red,image.GetPixel(0,0));return Task.CompletedTask;}
            public Task ApplyToFileAsync(Guid id,string source,string destination,float strength,CancellationToken t){File.Copy(source,destination,true);return Task.CompletedTask;}
            public Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken t)=>Task.FromResult<IReadOnlyList<ColorLutAsset>>(new ColorLutAsset[0]);
            public Task<ColorLutData> GetLiveAsync(Guid id,CancellationToken t)=>Task.FromResult<ColorLutData>(null);
            public Task<byte[]> RenderPreviewAsync(Guid id,string path,float strength,CancellationToken t)=>Task.FromResult<byte[]>(null);
            public Task<ColorLutImportResult> ImportAsync(string p,string n,CancellationToken t)=>Task.FromResult<ColorLutImportResult>(null);
            public Task AttachAsync(Guid p,Guid l,CancellationToken t)=>Task.CompletedTask;
            public Task DetachAsync(Guid p,CancellationToken t)=>Task.CompletedTask;
            public Task DeleteAsync(Guid id,long version,CancellationToken t)=>Task.CompletedTask;
            public Task ReconcileAsync(CancellationToken t)=>Task.CompletedTask;
        }

        sealed class GreenLutService : IColorLutService
        {
            public Task ApplyCaptureAsync(Guid presetId, string imagePath, CancellationToken token) { using (var image = new Bitmap(4, 2)) { using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Lime); image.Save(imagePath, ImageFormat.Jpeg); } return Task.CompletedTask; }
            public Task ApplyToFileAsync(Guid presetId,string sourcePath,string destinationPath,float strength,CancellationToken token){File.Copy(sourcePath,destinationPath,true);return Task.CompletedTask;}
            public Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ColorLutAsset>>(new ColorLutAsset[0]);
            public Task<ColorLutData> GetLiveAsync(Guid presetId, CancellationToken token) => Task.FromResult<ColorLutData>(null);
            public Task<byte[]> RenderPreviewAsync(Guid assetId,string imagePath,float strength,CancellationToken token)=>Task.FromResult<byte[]>(null);
            public Task<ColorLutImportResult> ImportAsync(string sourcePath, string displayName, CancellationToken token) => Task.FromResult<ColorLutImportResult>(null);
            public Task AttachAsync(Guid presetId, Guid assetId, CancellationToken token) => Task.CompletedTask;
            public Task DetachAsync(Guid presetId, CancellationToken token) => Task.CompletedTask;
            public Task DeleteAsync(Guid assetId, long expectedRowVersion, CancellationToken token) => Task.CompletedTask;
            public Task ReconcileAsync(CancellationToken token) => Task.CompletedTask;
        }

        sealed class FailingSecondLutService:IColorLutService
        {
            int calls;
            public Task ApplyCaptureAsync(Guid presetId,string imagePath,CancellationToken token){if(++calls==2)throw new InvalidOperationException("expected second-image failure");return Task.CompletedTask;}
            public Task ApplyToFileAsync(Guid presetId,string sourcePath,string destinationPath,float strength,CancellationToken token){File.Copy(sourcePath,destinationPath,true);return Task.CompletedTask;}
            public Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken token)=>Task.FromResult<IReadOnlyList<ColorLutAsset>>(new ColorLutAsset[0]);
            public Task<ColorLutData> GetLiveAsync(Guid presetId,CancellationToken token)=>Task.FromResult<ColorLutData>(null);
            public Task<byte[]> RenderPreviewAsync(Guid assetId,string imagePath,float strength,CancellationToken token)=>Task.FromResult<byte[]>(null);
            public Task<ColorLutImportResult> ImportAsync(string sourcePath,string displayName,CancellationToken token)=>Task.FromResult<ColorLutImportResult>(null);
            public Task AttachAsync(Guid presetId,Guid assetId,CancellationToken token)=>Task.CompletedTask;
            public Task DetachAsync(Guid presetId,CancellationToken token)=>Task.CompletedTask;
            public Task DeleteAsync(Guid assetId,long expectedRowVersion,CancellationToken token)=>Task.CompletedTask;
            public Task ReconcileAsync(CancellationToken token)=>Task.CompletedTask;
        }

        sealed class FakeVideoService : IVideoService
        {
            public bool LastFlipHorizontally { get; private set; }
            public int LastDurationSeconds { get; private set; }
            public void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc) { }
            public void ClearLiveViewFrames() { }
            public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, int durationSeconds, bool flipHorizontally, int rotationDegrees, CancellationToken token)
            {
                LastFlipHorizontally = flipHorizontally;
                LastDurationSeconds = durationSeconds;
                File.Copy(stillImagePath, destinationPath, true);
                return Task.CompletedTask;
            }
            public Task ComposeAsync(string stillCompositePath, Frame frame, IReadOnlyDictionary<int, string> slotAssignments, string destinationPath, CancellationToken token) => Task.CompletedTask;
            public Task<string> CreatePreviewVideoAsync(string videoPath,string previewDirectory,CancellationToken token)=>Task.FromResult<string>(null);
        }

        sealed class FailingVideoService : IVideoService
        {
            public void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc) { }
            public void ClearLiveViewFrames() { }
            public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, int durationSeconds, bool flipHorizontally, int rotationDegrees, CancellationToken token)
            {
                throw new InvalidOperationException("encoder failed");
            }
            public Task ComposeAsync(string stillCompositePath, Frame frame, IReadOnlyDictionary<int, string> slotAssignments, string destinationPath, CancellationToken token) => throw new InvalidOperationException("encoder failed");
            public Task<string> CreatePreviewVideoAsync(string videoPath,string previewDirectory,CancellationToken token)=>throw new InvalidOperationException("encoder failed");
        }
    }
}
