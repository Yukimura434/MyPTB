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
            var firstPreset = await Preset(db, "Warm", false, 'b');
            var secondPreset = await Preset(db, "Cool", false, 'c');
            var oldPinnedPreset = await Preset(db, "Old", true, 'd');

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
                FrameIds = new[] { secondFrame.Id, firstFrame.Id },
                PresetIds = new[] { secondPreset.Id, firstPreset.Id }
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
            Assert.Equal(secondPreset.Id, workflow.DefaultPresetId);
            var beauty = await new SqliteBeautySettingsRepository(db).GetAsync(CancellationToken.None);
            Assert.True(beauty.Enabled);
            Assert.Equal(21, beauty.SmoothSkin);
            Assert.Equal(26, beauty.SlimFace);
            var loadedFrames = await frames.GetAllAsync(CancellationToken.None);
            Assert.Equal(new[] { firstFrame.Id, secondFrame.Id }.OrderBy(x => x), loadedFrames.Where(x => x.IsPinned).Select(x => x.Id).OrderBy(x => x));
            var loadedPresets = await new SqlitePresetRepository(db).GetAllAsync(CancellationToken.None);
            Assert.Equal(new[] { firstPreset.Id, secondPreset.Id }.OrderBy(x => x), loadedPresets.Where(x => x.IsPinned).Select(x => x.Id).OrderBy(x => x));
            Assert.False(loadedPresets.Single(x => x.Id == oldPinnedPreset.Id).IsPinned);
            var loadedEvents = await sessions.GetAllAsync(CancellationToken.None);
            Assert.True(loadedEvents.Single(x => x.Id == secondEvent.Id).IsDefault);
            Assert.False(loadedEvents.Single(x => x.Id == firstEvent.Id).IsDefault);
            Assert.Equal("Second renamed", loadedEvents.Single(x => x.Id == secondEvent.Id).SessionName);
            Assert.Equal(new[] { secondFrame.Id, firstFrame.Id }, saved.FrameIds);
            Assert.Equal(new[] { secondPreset.Id, firstPreset.Id }, saved.PresetIds);
            Assert.Equal(saved.PresetIds, (await configurations.GetAsync(secondEvent.Id, CancellationToken.None)).PresetIds);
            using (var connection = db.OpenConnection())
            {
                using (var command = connection.CreateCommand()) { command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Version=15 AND Name='photo_event_presets'"; Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar())); }
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

        [Fact]
        public async Task Event_preset_migration_backfills_the_legacy_event_preset()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "preset-migration.db")); db.Initialize();
            var preset = await Preset(db, "Legacy event preset", false, 'e');
            var photoEvent = Event(root, "Legacy event", false); photoEvent.PresetId = preset.Id;
            var frame = Frame("Legacy frame", false);
            await new SqliteSessionRepository(db).SaveAsync(photoEvent, CancellationToken.None);
            await new SqliteFrameRepository(db).SaveAsync(frame, CancellationToken.None);
            await new SqlitePhotoEventConfigurationRepository(db).SaveAsync(photoEvent.SessionName, Configuration(photoEvent.Id, frame.Id, 4), CancellationToken.None);
            using (var connection = db.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TABLE EventPresets; DELETE FROM SchemaMigrations WHERE Version=15;";
                command.ExecuteNonQuery();
            }

            db.Initialize();

            var migrated = await new SqlitePhotoEventConfigurationRepository(db).GetAsync(photoEvent.Id, CancellationToken.None);
            Assert.Equal(new[] { preset.Id }, migrated.PresetIds);
        }

        static PhotoEventConfiguration Configuration(Guid eventId, Guid frameId, int photoCount) => new PhotoEventConfiguration
        {
            EventId=eventId,PhotoCount=photoCount,CountdownSeconds=3,GifFrameDurationMilliseconds=800,WaitingTimeoutSeconds=30,
            CustomerLayoutMode=CustomerLayoutMode.Landscape,ImageRotationDegrees=0,Beauty=new BeautySettings(),FrameIds=new[]{frameId}
        };
        static Session Event(string root, string name, bool selected) => new Session { Id=Guid.NewGuid(),Kind=SessionKinds.Event,SessionName=name,SessionNumber=1,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=root,IsDefault=selected,CapturedShots=new CapturedShot[0] };
        static Frame Frame(string name, bool pinned) => new Frame { Id=Guid.NewGuid(),Name=name,CreatedAtUtc=DateTime.UtcNow,IsPinned=pinned,Slots=new[]{new FrameSlot{Id=Guid.NewGuid(),Index=0,X=0,Y=0,Width=100,Height=100}} };
        static async Task<Preset> Preset(SqliteDatabase db, string name, bool pinned, char hashCharacter)
        {
            var now = DateTime.UtcNow;
            var asset = new ColorLutAsset
            {
                Id=Guid.NewGuid(),DisplayName=name+" LUT",RelativePath="Assets/Presets/Cubes/"+Guid.NewGuid().ToString("N")+".cube",
                ContentHashSha256=new string(hashCharacter,64),FileLength=128,CubeSize=33,DomainMaxR=1,DomainMaxG=1,DomainMaxB=1,
                Status=ColorLutAssetStatus.Ready,LastValidatedAtUtc=now,CreatedAtUtc=now,ModifiedAtUtc=now,RowVersion=1
            };
            await new SqliteColorLutAssetRepository(db).InsertAsync(asset,CancellationToken.None);
            var preset = new Preset { Id=Guid.NewGuid(),Name=name,CaptureCountdownSeconds=3,CreatedAtUtc=now,ModifiedAtUtc=now,IsPinned=pinned };
            await new SqlitePresetRepository(db).SaveAsync(preset,CancellationToken.None);
            await new SqlitePresetColorRepository(db).SaveAsync(new PresetColorSettings { PresetId=preset.Id,LutAssetId=asset.Id,ModifiedAtUtc=now },null,CancellationToken.None);
            return await new SqlitePresetRepository(db).GetAsync(preset.Id,CancellationToken.None);
        }
        static string NewRoot() { var value=Path.Combine(Path.GetTempPath(),"PhotoBoothEventTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(value);return value; }
    }
}
