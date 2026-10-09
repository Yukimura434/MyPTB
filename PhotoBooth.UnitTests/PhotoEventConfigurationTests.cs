using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class PhotoEventConfigurationTests
    {
        [Fact]
        public void Fresh_event_defaults_match_the_ready_to_run_contract()
        {
            var settings = new Settings();
            var configuration = new PhotoEventConfiguration();
            Assert.Equal(8, settings.PhotoCount);
            Assert.Equal(5, settings.CountdownSeconds);
            Assert.Equal(800, settings.GifFrameDurationMilliseconds);
            Assert.Equal(60, settings.WaitingTimeoutSeconds);
            Assert.Null(settings.DefaultPresetId);
            Assert.Equal(8, configuration.PhotoCount);
            Assert.Equal(5, configuration.CountdownSeconds);
            Assert.Equal(800, configuration.GifFrameDurationMilliseconds);
            Assert.Equal(60, configuration.WaitingTimeoutSeconds);
            Assert.Empty(configuration.PresetIds);
        }

        [Fact]
        public async Task Saving_configuration_can_update_the_friendly_event_output_directory()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "event-output.db")); db.Initialize();
            var sessions = new SqliteSessionRepository(db); var frames = new SqliteFrameRepository(db);
            var configurations = new SqlitePhotoEventConfigurationRepository(db);
            var photoEvent = Event(root, "Output event", false); var frame = Frame("Output frame", false);
            await sessions.SaveAsync(photoEvent, CancellationToken.None); await frames.SaveAsync(frame, CancellationToken.None);
            var output = Path.Combine(root, "Exports", "Wedding");

            await configurations.SaveAsync("Wedding renamed", output, Configuration(photoEvent.Id, frame.Id, 8), CancellationToken.None);

            var loaded = (await sessions.GetAllAsync(CancellationToken.None)).Single(value => value.Id == photoEvent.Id);
            Assert.Equal("Wedding renamed", loaded.SessionName);
            Assert.Equal(Path.GetFullPath(output), loaded.OutputDirectory);
        }

        [Fact]
        public async Task System_default_event_cannot_be_deleted_even_when_not_selected()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "system-default.db")); db.Initialize();
            var sessions = new SqliteSessionRepository(db);
            var systemDefault = Event(root, "Sự kiện mặc định", false); systemDefault.SessionNumber = 0;
            await sessions.SaveAsync(systemDefault, CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SqlitePhotoEventConfigurationRepository(db).DeleteAsync(systemDefault.Id, CancellationToken.None));

            Assert.Contains("không thể xóa", error.Message);
            Assert.Contains((await sessions.GetAllAsync(CancellationToken.None)), value => value.Id == systemDefault.Id);
        }

        [Fact]
        public async Task Fresh_install_seeds_five_frames_and_a_ready_default_event()
        {
            var root = NewRoot();
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddPhotoBoothInfrastructure(new ApplicationOptions
            {
                ApplicationName = "Default content test",
                DataDirectory = root,
                DatabasePath = Path.Combine(root, "fresh.db"),
                UseFakeCamera = true,
                RestartLiveViewDuringRecovery = false
            });
            using (var provider = services.BuildServiceProvider())
            {
                provider.InitializePhotoBooth();

                var frames = await provider.GetRequiredService<IFrameService>().GetAllAsync(CancellationToken.None);
                Assert.Equal(5, frames.Count);
                Assert.All(frames, frame => { Assert.Equal(1024, frame.PixelWidth); Assert.Equal(1536, frame.PixelHeight); Assert.Equal(4, frame.Slots.Count); Assert.True(File.Exists(frame.SourcePath)); });
                var photoEvents = await provider.GetRequiredService<IPhotoEventManagementService>().GetAllAsync(CancellationToken.None);
                var systemDefault = Assert.Single(photoEvents, value => value.IsSystemDefault);
                Assert.True(systemDefault.IsSelectedForThisDevice);
                Assert.Equal(PhotoEventLifecycleStates.Open, systemDefault.LifecycleState);
                var configuration = await provider.GetRequiredService<IPhotoEventManagementService>().GetActiveConfigurationAsync(systemDefault.Id, CancellationToken.None);
                Assert.Equal(8, configuration.PhotoCount);
                Assert.Equal(5, configuration.CountdownSeconds);
                Assert.Equal(800, configuration.GifFrameDurationMilliseconds);
                Assert.Equal(60, configuration.WaitingTimeoutSeconds);
                Assert.Equal(5, configuration.FrameIds.Count);
                Assert.Empty(configuration.PresetIds);
            }
        }

        [Fact]
        public async Task Event_configuration_accepts_more_than_ten_local_frames()
        {
            var root = NewRoot();
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddPhotoBoothInfrastructure(new ApplicationOptions
            {
                ApplicationName = "Unlimited event frames test",
                DataDirectory = root,
                DatabasePath = Path.Combine(root, "unlimited-event-frames.db"),
                UseFakeCamera = true,
                RestartLiveViewDuringRecovery = false
            });
            using (var provider = services.BuildServiceProvider())
            {
                provider.InitializePhotoBooth();
                var events = provider.GetRequiredService<IPhotoEventManagementService>();
                var frameRepository = provider.GetRequiredService<PhotoBooth.Core.Persistence.IFrameRepository>();
                var photoEvent = (await events.GetAllAsync(CancellationToken.None)).Single(value => value.IsSystemDefault);
                var selected = Enumerable.Range(0, 12).Select(index => Frame("Frame " + index, false)).ToArray();
                foreach (var frame in selected) await frameRepository.SaveAsync(frame, CancellationToken.None);
                var configuration = await events.GetConfigurationAsync(photoEvent.Id, CancellationToken.None);
                configuration.FrameIds = selected.Select(frame => frame.Id).ToArray();

                await events.SaveAsync(photoEvent.Name, configuration, CancellationToken.None);

                Assert.Equal(12, (await events.GetConfigurationAsync(photoEvent.Id, CancellationToken.None)).FrameIds.Count);
            }
        }

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
                PrintPolicyMode = EventPrintPolicyMode.Automatic,
                IncludedPrintCopies = 2,
                MaximumPrintCopies = 6,
                ColorPresetStrengthPercent = 73,
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
            Assert.True(workflow.PrintingEnabled);
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
            var activeConfiguration = await configurations.GetActiveAsync(secondEvent.Id, CancellationToken.None);
            Assert.Equal(EventPrintPolicyMode.Automatic, activeConfiguration.PrintPolicyMode);
            Assert.Equal(2, activeConfiguration.IncludedPrintCopies);
            Assert.Equal(6, activeConfiguration.MaximumPrintCopies);
            Assert.Equal(73, activeConfiguration.ColorPresetStrengthPercent);
            using (var connection = db.OpenConnection())
            {
                using (var command = connection.CreateCommand()) { command.CommandText = "SELECT COUNT(*) FROM ConfigurationVersions WHERE EventId=$event AND State='Active' AND json_valid(ConfigurationJson)=1";command.Parameters.AddWithValue("$event",secondEvent.Id.ToString());Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar())); }
                using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", command.ExecuteScalar()); }
                using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA foreign_key_check"; using (var reader = command.ExecuteReader()) Assert.False(reader.Read()); }
            }
        }

        [Fact]
        public async Task Activation_switches_default_when_target_row_precedes_the_current_default()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "default-order.db"));
            db.Initialize();
            var sessions = new SqliteSessionRepository(db);
            var frames = new SqliteFrameRepository(db);
            var configurations = new SqlitePhotoEventConfigurationRepository(db);
            var target = Event(root, "Target created first", false);
            var current = Event(root, "Current created second", true);
            await sessions.SaveAsync(target, CancellationToken.None);
            await sessions.SaveAsync(current, CancellationToken.None);
            await sessions.SetDefaultAsync(current.Id,CancellationToken.None);
            var frame = Frame("Target frame", false);
            await frames.SaveAsync(frame, CancellationToken.None);
            await configurations.SaveAsync(target.SessionName, Configuration(target.Id, frame.Id, 4), CancellationToken.None);

            await configurations.ActivateAsync(target.Id, CancellationToken.None);

            var loaded = await sessions.GetAllAsync(CancellationToken.None);
            Assert.True(loaded.Single(x => x.Id == target.Id).IsDefault);
            Assert.False(loaded.Single(x => x.Id == current.Id).IsDefault);
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
            await sessions.SetDefaultAsync(current.Id,CancellationToken.None);
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
        public async Task Booth_session_keeps_the_configuration_version_active_when_it_started()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "configuration-version.db")); db.Initialize();
            var photoEvent = Event(root, "Versioned event", false);
            var frame = Frame("Versioned frame", false);
            await new SqliteSessionRepository(db).SaveAsync(photoEvent, CancellationToken.None);
            await new SqliteFrameRepository(db).SaveAsync(frame, CancellationToken.None);
            var configurations=new SqlitePhotoEventConfigurationRepository(db);
            var saved=await configurations.SaveAsync(photoEvent.SessionName, Configuration(photoEvent.Id, frame.Id, 4), CancellationToken.None);
            await configurations.ActivateAsync(photoEvent.Id,CancellationToken.None);
            string activeVersion;
            using (var connection = db.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id FROM ConfigurationVersions WHERE EventId=$event AND State='Active'";command.Parameters.AddWithValue("$event",photoEvent.Id.ToString());activeVersion=Convert.ToString(command.ExecuteScalar());
            }
            var boothId=Guid.NewGuid();var boothRoot=Path.Combine(root,"Sessions",boothId.ToString("N"));Directory.CreateDirectory(boothRoot);
            await new SqliteSessionRepository(db).SaveAsync(new Session{Id=boothId,Kind=SessionKinds.Booth,EventId=photoEvent.Id,Status=BoothSessionStates.Active,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=boothRoot,CapturedShots=new CapturedShot[0]},CancellationToken.None);
            saved.PhotoCount=5;await configurations.SaveAsync(photoEvent.SessionName,saved,CancellationToken.None);
            using(var connection=db.OpenConnection())
            {
                using(var command=connection.CreateCommand()){command.CommandText="SELECT ConfigurationVersionId FROM BoothSessions WHERE Id=$booth";command.Parameters.AddWithValue("$booth",boothId.ToString());Assert.Equal(activeVersion,Convert.ToString(command.ExecuteScalar()));}
                using(var command=connection.CreateCommand()){command.CommandText="SELECT COUNT(*) FROM ConfigurationVersions WHERE EventId=$event AND State='Ready'";command.Parameters.AddWithValue("$event",photoEvent.Id.ToString());Assert.Equal(1,Convert.ToInt32(command.ExecuteScalar()));}
            }
        }

        [Fact]
        public async Task Saved_remote_candidate_does_not_replace_the_active_runtime_configuration_until_activation()
        {
            var root = NewRoot();
            var db = new SqliteDatabase(Path.Combine(root, "staged-configuration.db")); db.Initialize();
            var photoEvent = Event(root, "Staged event", true);
            var frame = Frame("Staged frame", false);
            await new SqliteSessionRepository(db).SaveAsync(photoEvent, CancellationToken.None);
            await new SqliteFrameRepository(db).SaveAsync(frame, CancellationToken.None);
            var configurations = new SqlitePhotoEventConfigurationRepository(db);

            var saved = await configurations.SaveAsync(photoEvent.SessionName, Configuration(photoEvent.Id, frame.Id, 4), CancellationToken.None);
            await configurations.ActivateAsync(photoEvent.Id, CancellationToken.None);
            saved.PhotoCount = 7;
            await configurations.SaveAsync(photoEvent.SessionName, saved, CancellationToken.None);

            Assert.Equal(7, (await configurations.GetAsync(photoEvent.Id, CancellationToken.None)).PhotoCount);
            Assert.Equal(4, (await configurations.GetActiveAsync(photoEvent.Id, CancellationToken.None)).PhotoCount);

            await configurations.ActivateAsync(photoEvent.Id, CancellationToken.None);
            Assert.Equal(7, (await configurations.GetActiveAsync(photoEvent.Id, CancellationToken.None)).PhotoCount);
        }

        static PhotoEventConfiguration Configuration(Guid eventId, Guid frameId, int photoCount) => new PhotoEventConfiguration
        {
            EventId=eventId,PhotoCount=photoCount,CountdownSeconds=3,GifFrameDurationMilliseconds=800,WaitingTimeoutSeconds=30,
            CustomerLayoutMode=CustomerLayoutMode.Landscape,ImageRotationDegrees=0,Beauty=new BeautySettings(),FrameIds=new[]{frameId}
        };
        static Session Event(string root, string name, bool selected) => new Session { Id=Guid.NewGuid(),Kind=SessionKinds.Event,EventLifecycleState=PhotoEventLifecycleStates.Open,SessionName=name,SessionNumber=1,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=root,IsDefault=selected,CapturedShots=new CapturedShot[0] };
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
