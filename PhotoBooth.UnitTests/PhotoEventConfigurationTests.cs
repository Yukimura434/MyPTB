using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class PhotoEventConfigurationTests
    {
        [Fact]
        public async Task Activation_applies_settings_beauty_and_exact_frame_set_atomically()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "event.db"));
            db.Initialize();
            var sessions = new SqliteSessionRepository(db);
            var frames = new SqliteFrameRepository(db);
            var configurations = new SqlitePhotoEventConfigurationRepository(db);
            var firstEvent = Event(root, "First", true);
            var secondEvent = Event(root, "Second", false);
            await sessions.SaveAsync(firstEvent, CancellationToken.None);
            await sessions.SaveAsync(secondEvent, CancellationToken.None);
            var firstFrame = Frame("One", true);
            var secondFrame = Frame("Two", false);
            var oldPinnedFrame = Frame("Old", true);
            await frames.SaveAsync(firstFrame, CancellationToken.None);
            await frames.SaveAsync(secondFrame, CancellationToken.None);
            await frames.SaveAsync(oldPinnedFrame, CancellationToken.None);

            var saved = await configurations.SaveAsync("Second renamed", new PhotoEventConfiguration
            {
                EventId = secondEvent.Id,
                PhotoCount = 6,
                CountdownSeconds = 5,
                GifFrameDurationMilliseconds = 600,
                WaitingTimeoutSeconds = 300,
                CustomerLayoutMode = CustomerLayoutMode.Portrait,
                ImageRotationDegrees = 90,
                Beauty = new BeautySettings { Enabled=true,SmoothSkin=21,BrightenSkin=22,SkinTone=23,Sharpen=24,EyeSize=25,SlimFace=26 },
                FrameIds = new[] { secondFrame.Id, firstFrame.Id }
            }, CancellationToken.None);

            await configurations.ActivateAsync(secondEvent.Id, CancellationToken.None);

            var workflow = await new SqliteSettingsRepository(db).GetAsync(CancellationToken.None);
            Assert.Equal(6, workflow.PhotoCount);
            Assert.Equal(5, workflow.CountdownSeconds);
            Assert.Equal(600, workflow.GifFrameDurationMilliseconds);
            Assert.Equal(300, workflow.WaitingTimeoutSeconds);
            Assert.Equal(CustomerLayoutMode.Portrait, workflow.CustomerLayoutMode);
            Assert.Equal(90, workflow.ImageRotationDegrees);
            Assert.Equal(secondFrame.Id, workflow.DefaultFrameId);
            var beauty = await new SqliteBeautySettingsRepository(db).GetAsync(CancellationToken.None);
            Assert.True(beauty.Enabled);
            Assert.Equal(21, beauty.SmoothSkin);
            Assert.Equal(26, beauty.SlimFace);
            var loadedFrames = await frames.GetAllAsync(CancellationToken.None);
            Assert.Equal(new[] { firstFrame.Id, secondFrame.Id }.OrderBy(x => x), loadedFrames.Where(x => x.IsPinned).Select(x => x.Id).OrderBy(x => x));
            var loadedEvents = await sessions.GetAllAsync(CancellationToken.None);
            Assert.True(loadedEvents.Single(x => x.Id == secondEvent.Id).IsDefault);
            Assert.False(loadedEvents.Single(x => x.Id == firstEvent.Id).IsDefault);
            Assert.Equal("Second renamed", loadedEvents.Single(x => x.Id == secondEvent.Id).SessionName);
            Assert.Equal(new[] { secondFrame.Id, firstFrame.Id }, saved.FrameIds);
            using (var connection = db.OpenConnection())
            {
                using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", command.ExecuteScalar()); }
                using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA foreign_key_check"; using (var reader = command.ExecuteReader()) Assert.False(reader.Read()); }
            }
        }

        [Fact]
        public async Task Failed_activation_rolls_back_default_event_and_frame_pins()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "rollback.db")); db.Initialize();
            var sessions = new SqliteSessionRepository(db); var frames = new SqliteFrameRepository(db);
            var configurations = new SqlitePhotoEventConfigurationRepository(db);
            var current = Event(root, "Current", true); var target = Event(root, "Target", false);
            await sessions.SaveAsync(current, CancellationToken.None); await sessions.SaveAsync(target, CancellationToken.None);
            var currentFrame = Frame("Current frame", true); var targetFrame = Frame("Target frame", false);
            await frames.SaveAsync(currentFrame, CancellationToken.None); await frames.SaveAsync(targetFrame, CancellationToken.None);
            await configurations.SaveAsync("Target", Configuration(target.Id, targetFrame.Id, 7), CancellationToken.None);
            using (var connection = db.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TRIGGER FailEventActivation BEFORE UPDATE ON WorkflowSettings WHEN NEW.PhotoCount=7 BEGIN SELECT RAISE(ABORT,'forced activation failure'); END;";
                command.ExecuteNonQuery();
            }

            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => configurations.ActivateAsync(target.Id, CancellationToken.None));

            var loadedEvents = await sessions.GetAllAsync(CancellationToken.None);
            Assert.True(loadedEvents.Single(x => x.Id == current.Id).IsDefault);
            Assert.False(loadedEvents.Single(x => x.Id == target.Id).IsDefault);
            var loadedFrames = await frames.GetAllAsync(CancellationToken.None);
            Assert.True(loadedFrames.Single(x => x.Id == currentFrame.Id).IsPinned);
            Assert.False(loadedFrames.Single(x => x.Id == targetFrame.Id).IsPinned);
        }

        [Fact]
        public async Task Saving_with_a_stale_row_version_is_rejected()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "concurrency.db")); db.Initialize();
            var sessions = new SqliteSessionRepository(db); var frames = new SqliteFrameRepository(db);
            var configurations = new SqlitePhotoEventConfigurationRepository(db);
            var photoEvent = Event(root, "Concurrent", false); var frame = Frame("Frame", false);
            await sessions.SaveAsync(photoEvent, CancellationToken.None); await frames.SaveAsync(frame, CancellationToken.None);
            var saved = await configurations.SaveAsync("Concurrent", Configuration(photoEvent.Id, frame.Id, 2), CancellationToken.None);
            var stale = Configuration(photoEvent.Id, frame.Id, 3); stale.RowVersion = saved.RowVersion;
            saved.PhotoCount = 4;
            await configurations.SaveAsync("Concurrent", saved, CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => configurations.SaveAsync("Concurrent", stale, CancellationToken.None));
            Assert.Contains("cập nhật", error.Message);
            Assert.Equal(4, (await configurations.GetAsync(photoEvent.Id, CancellationToken.None)).PhotoCount);
        }

        static PhotoEventConfiguration Configuration(Guid eventId, Guid frameId, int photoCount) => new PhotoEventConfiguration
        {
            EventId=eventId,PhotoCount=photoCount,CountdownSeconds=3,GifFrameDurationMilliseconds=800,WaitingTimeoutSeconds=30,
            CustomerLayoutMode=CustomerLayoutMode.Landscape,ImageRotationDegrees=0,Beauty=new BeautySettings(),FrameIds=new[]{frameId}
        };
        static Session Event(string root, string name, bool selected) => new Session { Id=Guid.NewGuid(),Kind=SessionKinds.Event,SessionName=name,SessionNumber=1,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=root,IsDefault=selected,CapturedShots=new CapturedShot[0] };
        static Frame Frame(string name, bool pinned) => new Frame { Id=Guid.NewGuid(),Name=name,CreatedAtUtc=DateTime.UtcNow,IsPinned=pinned,Slots=new[]{new FrameSlot{Id=Guid.NewGuid(),Index=0,X=0,Y=0,Width=100,Height=100}} };
        static string NewRoot() { var value=Path.Combine(Path.GetTempPath(),"PhotoBoothEventTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(value);return value; }
    }
}
