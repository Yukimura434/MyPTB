using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoBooth.Business.Pipelines;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class LocalBusinessPersistenceTests
    {
        [Fact]
        public void Version_two_creates_canonical_tables_and_sqlite_runtime_pragmas()
        {
            WithRoot(root=>
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();database.Initialize();
                using(var connection=database.OpenConnection())
                {
                    Assert.Equal("wal",Scalar(connection,"PRAGMA journal_mode").ToString().ToLowerInvariant());
                    Assert.Equal(5000,Convert.ToInt32(Scalar(connection,"PRAGMA busy_timeout")));
                    Assert.Equal(SqliteDatabase.SchemaVersion,Convert.ToInt32(Scalar(connection,"SELECT Version FROM SchemaInfo")));
                    Assert.Equal(4,Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('CaptureAttempts','MediaAssets','OutputJobs','SyncOutbox')")));
                    Assert.Equal(4,Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Events','BoothSessions','Deliverables','DeliverableAssets')")));
                    Assert.Equal(1,Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EventExportJobs'")));
                    Assert.Equal(0,Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM sqlite_master WHERE type='view' AND name IN ('Events','BoothSessions','Deliverables','DeliverableAssets')")));
                }
            });
        }

        [Fact]
        public async Task Customer_turn_gets_an_independent_managed_booth_session()
        {
            var root=NewRoot();
            try
            {
                var setup=CreateSetup(root);var eventId=Guid.NewGuid();
                await setup.Sessions.SaveAsync(new Session{Id=eventId,Kind=SessionKinds.Event,EventLifecycleState=PhotoEventLifecycleStates.Open,Status=BoothSessionStates.Active,SessionName="Wedding",StartedAtUtc=DateTime.UtcNow,OutputDirectory=Path.Combine(root,"Events","Wedding"),CapturedShots=new CapturedShot[0]},CancellationToken.None);
                var service=new SessionService(setup.Sessions,setup.Options,setup.Storage,setup.Assets);
                var booth=await service.StartBoothSessionAsync(eventId,null,CancellationToken.None);
                Assert.True(booth.IsBoothSession);Assert.Equal(eventId,booth.EventId);Assert.Equal(BoothSessionStates.Active,booth.Status);Assert.False(string.IsNullOrWhiteSpace(booth.DisplayCode));
                Assert.True(Directory.Exists(Path.Combine(booth.OutputDirectory,"Work")));Assert.True(Directory.Exists(Path.Combine(booth.OutputDirectory,"Originals")));Assert.True(Directory.Exists(Path.Combine(booth.OutputDirectory,"Final")));
                Assert.Equal(32,Path.GetFileName(booth.OutputDirectory).Length);
                Assert.Single(await setup.Sessions.GetAllAsync(CancellationToken.None));
                Assert.True((await setup.Sessions.GetAsync(booth.Id,CancellationToken.None)).IsBoothSession);
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Canonical_event_and_booth_session_services_do_not_expose_the_legacy_meaning()
        {
            var root=NewRoot();
            try
            {
                var setup=CreateSetup(root);var implementation=new SessionService(setup.Sessions,setup.Options,setup.Storage,setup.Assets);var eventService=(IEventService)implementation;var boothSessions=(IBoothSessionService)implementation;
                var draft=await eventService.CreateDraftAsync(null,CancellationToken.None);draft.Name="Khai trương chi nhánh";var photoEvent=await eventService.CreateAsync(draft,CancellationToken.None);photoEvent=await eventService.TransitionAsync(photoEvent.Id,PhotoEventLifecycleStates.Open,CancellationToken.None);await eventService.SetDefaultAsync(photoEvent.Id,CancellationToken.None);
                var boothSession=await boothSessions.StartAsync(photoEvent.Id,null,CancellationToken.None);
                Assert.IsType<BoothSession>(boothSession);
                Assert.Equal("Khai trương chi nhánh",(await eventService.GetDefaultAsync(CancellationToken.None)).Name);
                Assert.Equal(photoEvent.Id,boothSession.EventId);Assert.True(boothSession.IsBoothSession);
                using(var connection=setup.Database.OpenConnection())
                {
                    Assert.Equal("Khai trương chi nhánh",Convert.ToString(Scalar(connection,"SELECT Name FROM Events WHERE Id='"+photoEvent.Id+"'")));
                    Assert.Equal(photoEvent.Id.ToString(),Convert.ToString(Scalar(connection,"SELECT EventId FROM BoothSessions WHERE Id='"+boothSession.Id+"'")));
                }
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Event_lifecycle_is_independent_from_device_selection_and_closing_blocks_only_new_turns()
        {
            var root=NewRoot();
            try
            {
                var setup=CreateSetup(root);
                var implementation=new SessionService(setup.Sessions,setup.Options,setup.Storage,setup.Assets);
                var eventService=(IEventService)implementation;
                var boothSessions=(IBoothSessionService)implementation;
                var draft=await eventService.CreateDraftAsync(null,CancellationToken.None);
                draft.Name="Tiệc cưới Minh & An";
                var photoEvent=await eventService.CreateAsync(draft,CancellationToken.None);
                Assert.Equal(PhotoEventLifecycleStates.Draft,photoEvent.LifecycleState);
                await Assert.ThrowsAsync<InvalidOperationException>(()=>boothSessions.StartAsync(photoEvent.Id,null,CancellationToken.None));

                photoEvent=await eventService.TransitionAsync(photoEvent.Id,PhotoEventLifecycleStates.Open,CancellationToken.None);
                await eventService.SetDefaultAsync(photoEvent.Id,CancellationToken.None);
                var selected=await eventService.GetDefaultAsync(CancellationToken.None);
                Assert.Equal(PhotoEventLifecycleStates.Open,selected.LifecycleState);
                Assert.True(selected.IsSelectedForThisDevice);

                var existingTurn=await boothSessions.StartAsync(photoEvent.Id,null,CancellationToken.None);
                Assert.False(string.IsNullOrWhiteSpace(existingTurn.ConfigurationVersionId));
                photoEvent=await eventService.TransitionAsync(photoEvent.Id,PhotoEventLifecycleStates.Closed,CancellationToken.None);
                Assert.Equal(PhotoEventLifecycleStates.Closed,photoEvent.LifecycleState);
                Assert.False((await eventService.GetAllAsync(CancellationToken.None)).Single(x=>x.Id==photoEvent.Id).IsSelectedForThisDevice);
                await Assert.ThrowsAsync<InvalidOperationException>(()=>boothSessions.StartAsync(photoEvent.Id,null,CancellationToken.None));
                Assert.NotNull(await boothSessions.GetAsync(existingTurn.Id,CancellationToken.None));

                photoEvent=await eventService.TransitionAsync(photoEvent.Id,PhotoEventLifecycleStates.Delivered,CancellationToken.None);
                photoEvent=await eventService.TransitionAsync(photoEvent.Id,PhotoEventLifecycleStates.Archived,CancellationToken.None);
                Assert.True(photoEvent.IsArchived);
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Capture_uses_asset_ids_and_checkpoints_attempt_and_media()
        {
            var root=NewRoot();
            try
            {
                var setup=CreateSetup(root);var eventId=Guid.NewGuid();
                await setup.Sessions.SaveAsync(new Session{Id=eventId,Kind=SessionKinds.Event,EventLifecycleState=PhotoEventLifecycleStates.Open,Status=BoothSessionStates.Active,SessionName="Event",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=new CapturedShot[0]},CancellationToken.None);
                var service=new SessionService(setup.Sessions,setup.Options,setup.Storage,setup.Assets);var booth=await service.StartBoothSessionAsync(eventId,null,CancellationToken.None);var work=Path.Combine(booth.OutputDirectory,"Work");
                var attempts=new SqliteCaptureAttemptRepository(setup.Database);
                var pipeline=new CapturePipeline(new FileCamera(),setup.Sessions,new FixedSettings(),new NoVideo(),null,null,null,null,null,attempts,setup.Assets,setup.Storage);
                var captured=await pipeline.ExecuteAsync(booth.Id,"camera",work,false,CancellationToken.None);
                var shot=Assert.Single(captured.CapturedShots);Assert.Equal(32,shot.Id.Length);Assert.Equal(32,shot.PictureAssetId.Length);Assert.Equal(shot.PictureAssetId,Path.GetFileNameWithoutExtension(shot.PicturePath));
                using(var connection=setup.Database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="SELECT State FROM CaptureAttempts WHERE Id=$id";command.Parameters.AddWithValue("$id",shot.Id);Assert.Equal(CaptureAttemptStates.Accepted,Convert.ToString(command.ExecuteScalar()));}
                var asset=Assert.Single(await setup.Assets.GetBySessionAsync(booth.Id,CancellationToken.None));Assert.Equal(MediaAssetKinds.OriginalPicture,asset.Kind);Assert.False(Path.IsPathRooted(asset.RelativePath));Assert.Equal(MediaAssetStates.Ready,asset.Status);
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Retention_never_deletes_a_session_with_pending_output()
        {
            var root=NewRoot();
            try
            {
                var setup=CreateSetup(root);var eventId=Guid.NewGuid();await setup.Sessions.SaveAsync(new Session{Id=eventId,Kind=SessionKinds.Event,SessionName="Retention",StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=new CapturedShot[0]},CancellationToken.None);var sessionId=Guid.NewGuid();var started=DateTime.UtcNow.AddDays(-10);var paths=setup.Storage.CreateSessionStorage(sessionId,started);var finalId=Guid.NewGuid().ToString("N");var final=Path.Combine(paths.Final,finalId+".png");File.WriteAllText(final,"final");
                var booth=new Session{Id=sessionId,Kind=SessionKinds.Booth,EventId=eventId,Status=BoothSessionStates.Completed,StartedAtUtc=started,CompletedAtUtc=started.AddMinutes(2),UpdatedAtUtc=started.AddMinutes(2),OutputDirectory=paths.Root,FinalImagePath=final,CapturedShots=new CapturedShot[0]};
                await setup.Sessions.SaveAsync(booth,CancellationToken.None);
                await setup.Assets.SaveAsync(new MediaAssetRecord{Id=finalId,SessionId=sessionId,Kind=MediaAssetKinds.FinalComposite,RelativePath=setup.Storage.GetRelativePath(final),MimeType="image/png",FileLength=5,Status=MediaAssetStates.Ready,RetentionClass=MediaRetentionClasses.Deliverable,CreatedAtUtc=started,UpdatedAtUtc=started},CancellationToken.None);
                booth.FinalImageId=finalId;booth.StateVersion++;await setup.Sessions.SaveAsync(booth,CancellationToken.None);
                var jobs=new SqliteDurableOutputJobRepository(setup.Database);var job=await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=sessionId,AssetId=finalId,JobType="UploadAsset",IdempotencyKey="upload:"+sessionId.ToString("N"),ProtectedAuthorization="dpapi-test-value",CreatedAtUtc=started},CancellationToken.None);
                Assert.Equal("dpapi-test-value",job.ProtectedAuthorization);
                await setup.Settings.SaveAsync(new Settings{SessionRetentionDays=1,TemporaryFileRetentionHours=1},CancellationToken.None);
                Directory.Delete(paths.Work,true);
                await setup.Storage.CleanupAsync(CancellationToken.None);Assert.True(Directory.Exists(paths.Root));
                await jobs.SetStateAsync(job.Id,"Completed",null,CancellationToken.None);
                await setup.Storage.CleanupAsync(CancellationToken.None);Assert.True(Directory.Exists(paths.Root));
                using(var connection=setup.Database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText="UPDATE Events SET LifecycleState='Delivered',DeliveredAtUtc=$now,UpdatedAtUtc=$now WHERE Id=$id";command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",eventId.ToString());command.ExecuteNonQuery();
                }
                var replicas=new SqliteMediaAssetReplicaRepository(setup.Database);await replicas.ReplaceOnlineProviderAsync(finalId,MediaReplicaProviders.SystemLibrary,MediaReplicaStates.Verified,"remote-test",null,5,DateTime.UtcNow,CancellationToken.None);
                await setup.Storage.CleanupAsync(CancellationToken.None);Assert.False(Directory.Exists(paths.Root));Assert.Equal(MediaAssetStates.Deleted,(await setup.Assets.GetAsync(finalId,CancellationToken.None)).Status);
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Interrupted_local_work_is_reconciled_conservatively()
        {
            var root=NewRoot();
            try
            {
                var setup=CreateSetup(root);var eventId=Guid.NewGuid();await setup.Sessions.SaveAsync(new Session{Id=eventId,Kind=SessionKinds.Event,SessionName="Recovery",StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=new CapturedShot[0]},CancellationToken.None);var sessionId=Guid.NewGuid();var now=DateTime.UtcNow;var paths=setup.Storage.CreateSessionStorage(sessionId,now);var pictureId=Guid.NewGuid().ToString("N");var attemptId=Guid.NewGuid().ToString("N");var picture=Path.Combine(paths.Work,pictureId+".jpg");File.WriteAllText(picture,"image");
                await setup.Sessions.SaveAsync(new Session{Id=sessionId,Kind=SessionKinds.Booth,EventId=eventId,Status=BoothSessionStates.Finalizing,StartedAtUtc=now,UpdatedAtUtc=now,OutputDirectory=paths.Root,CapturedShots=new CapturedShot[0]},CancellationToken.None);
                var attempts=new SqliteCaptureAttemptRepository(setup.Database);await attempts.BeginAsync(new CaptureAttemptRecord{Id=attemptId,SessionId=sessionId,Sequence=1,AttemptNumber=1,PictureAssetId=pictureId,IntentAtUtc=now},CancellationToken.None);
                await setup.Assets.SaveAsync(new MediaAssetRecord{Id=pictureId,SessionId=sessionId,CaptureAttemptId=attemptId,Kind=MediaAssetKinds.OriginalPicture,RelativePath=setup.Storage.GetRelativePath(picture),MimeType="image/jpeg",FileLength=5,Status=MediaAssetStates.Ready,RetentionClass=MediaRetentionClasses.Original,CreatedAtUtc=now,UpdatedAtUtc=now},CancellationToken.None);
                var jobs=new SqliteDurableOutputJobRepository(setup.Database);var job=await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=sessionId,AssetId=pictureId,JobType="UploadAsset",IdempotencyKey="upload:"+sessionId.ToString("N"),CreatedAtUtc=now},CancellationToken.None);await jobs.SetStateAsync(job.Id,DurableOutputJobStates.Submitting,null,CancellationToken.None);

                foreach(var attempt in await attempts.GetIncompleteAsync(CancellationToken.None))await attempts.MarkFailedAsync(attempt.Id,"restart",true,CancellationToken.None);
                var recovery=(ILocalSessionRecoveryRepository)setup.Sessions;foreach(var session in await recovery.GetActiveBoothSessionsAsync(CancellationToken.None))await recovery.MarkRecoveredFailedAsync(session.Id,"restart",CancellationToken.None);
                await jobs.ReconcileInterruptedAsync(CancellationToken.None);

                Assert.Equal(BoothSessionStates.Failed,(await setup.Sessions.GetAsync(sessionId,CancellationToken.None)).Status);
                using(var connection=setup.Database.OpenConnection())
                {
                    Assert.Equal(CaptureAttemptStates.Unknown,Convert.ToString(Scalar(connection,"SELECT State FROM CaptureAttempts WHERE Id='"+attemptId+"'")));
                    Assert.Equal(DurableOutputJobStates.UnknownOutcome,Convert.ToString(Scalar(connection,"SELECT State FROM OutputJobs WHERE Id='"+job.Id+"'")));
                }
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Event_export_is_durable_idempotent_and_checksum_verified()
        {
            var root=NewRoot();
            try
            {
                var setup=CreateSetup(root);var eventId=Guid.NewGuid();var exportRoot=Path.Combine(root,"Ban-giao");
                await setup.Sessions.SaveAsync(new Session{Id=eventId,Kind=SessionKinds.Event,EventLifecycleState=PhotoEventLifecycleStates.Open,Status=BoothSessionStates.Active,SessionName="Tiệc cưới",StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=exportRoot,CapturedShots=new CapturedShot[0]},CancellationToken.None);
                var sessionService=new SessionService(setup.Sessions,setup.Options,setup.Storage,setup.Assets);var booth=await ((IBoothSessionService)sessionService).StartAsync(eventId,null,CancellationToken.None);
                var source=Path.Combine(booth.OutputDirectory,"Final","result.png");Directory.CreateDirectory(Path.GetDirectoryName(source));File.WriteAllText(source,"verified-event-export");
                var captures=new SqliteDeliverableRepository(setup.Database);IDeliverableService deliverableService=new DeliverableService(captures,setup.Sessions,setup.Assets,setup.Storage);
                var deliverable=await deliverableService.CreateAsync(booth.Id,null,Guid.NewGuid().ToString("N"),source,new CapturedShot[0],null,CancellationToken.None);
                var repository=new SqliteEventExportJobRepository(setup.Database);using(var queue=new EventExportQueue(repository,deliverableService,setup.Options,NullLogger<EventExportQueue>.Instance))
                {
                    await queue.StartAsync(CancellationToken.None);var photoEvent=new PhotoEvent{Id=eventId,Name="Tiệc cưới",OutputDirectory=exportRoot,LifecycleState=PhotoEventLifecycleStates.Open};
                    await queue.EnqueueAsync(photoEvent,booth,deliverable,CancellationToken.None);await queue.EnqueueAsync(photoEvent,booth,deliverable,CancellationToken.None);
                    var timeout=DateTime.UtcNow.AddSeconds(5);while(DateTime.UtcNow<timeout)
                    {
                        using(var connection=setup.Database.OpenConnection())if(Convert.ToString(Scalar(connection,"SELECT State FROM EventExportJobs LIMIT 1"))==DurableOutputJobStates.Completed)break;
                        await Task.Delay(25);
                    }
                    using(var connection=setup.Database.OpenConnection())
                    {
                        Assert.Equal(1,Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM EventExportJobs")));
                        Assert.Equal(DurableOutputJobStates.Completed,Convert.ToString(Scalar(connection,"SELECT State FROM EventExportJobs LIMIT 1")));
                    }
                    var destination=Path.Combine(exportRoot,booth.DisplayCode,"01-anh-thanh-pham.png");Assert.True(File.Exists(destination));Assert.Equal("verified-event-export",File.ReadAllText(destination));
                    await queue.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
                }
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        static Setup CreateSetup(string root)
        {
            var options=new ApplicationOptions{DataDirectory=root,DatabasePath=Path.Combine(root,"test.db")};var database=new SqliteDatabase(options.DatabasePath);database.Initialize();var sessions=new SqliteSessionRepository(database);var assets=new SqliteMediaAssetRepository(database);var settings=new SettingsService(new SqliteSettingsRepository(database));var storage=new StorageManager(options,settings,sessions,assets);return new Setup{Options=options,Database=database,Sessions=sessions,Assets=assets,Settings=settings,Storage=storage};
        }
        static object Scalar(Microsoft.Data.Sqlite.SqliteConnection connection,string sql){using(var command=connection.CreateCommand()){command.CommandText=sql;return command.ExecuteScalar();}}
        static string NewRoot(){var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
        static void WithRoot(Action<string> action){var root=NewRoot();try{action(root);}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}}
        sealed class Setup{public ApplicationOptions Options;public SqliteDatabase Database;public SqliteSessionRepository Sessions;public SqliteMediaAssetRepository Assets;public SettingsService Settings;public StorageManager Storage;}
        sealed class FixedSettings:ISettingsService{public Task<Settings> GetAsync(CancellationToken token)=>Task.FromResult(new Settings());public Task SaveAsync(Settings value,CancellationToken token)=>Task.CompletedTask;}
        sealed class NoVideo:IVideoService{public void AddLiveViewFrame(byte[] imageData,DateTime timestampUtc){}public void ClearLiveViewFrames(){}public Task CreateAsync(string stillImagePath,string destinationPath,DateTime shutterTimestampUtc,int durationSeconds,bool flipHorizontally,int rotationDegrees,CancellationToken token)=>Task.CompletedTask;public Task ComposeAsync(string stillCompositePath,Frame frame,IReadOnlyDictionary<int,string> slotAssignments,string destinationPath,CancellationToken token)=>Task.CompletedTask;public Task<string> CreatePreviewVideoAsync(string videoPath,string previewDirectory,CancellationToken token)=>Task.FromResult<string>(null);}
        sealed class FileCamera:ICameraService
        {
            public event EventHandler CamerasChanged;
            public Task<CaptureResult> CaptureAsync(string cameraId,bool autoFocus,CancellationToken token)=>CaptureAsync(cameraId,autoFocus,null,CameraSaveMode.PcOnly,token);
            public Task<CaptureResult> CaptureAsync(string cameraId,bool autoFocus,string destinationPath,CancellationToken token)=>CaptureAsync(cameraId,autoFocus,destinationPath,CameraSaveMode.PcOnly,token);
            public Task<CaptureResult> CaptureAsync(string cameraId,bool autoFocus,string destinationPath,CameraSaveMode saveMode,CancellationToken token){File.WriteAllText(destinationPath,"image");return Task.FromResult(new CaptureResult{Succeeded=true,CameraId=cameraId,FileName=destinationPath});}
            public Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken token)=>Task.FromResult<IReadOnlyList<CameraInfo>>(new CameraInfo[0]);public Task<IReadOnlyList<CameraInfo>> ScanAsync(CancellationToken token)=>GetCamerasAsync(token);public Task ConnectAsync(CancellationToken token)=>Task.CompletedTask;public Task ConnectAsync(string cameraId,CancellationToken token)=>Task.CompletedTask;public Task DisconnectAsync(CancellationToken token)=>Task.CompletedTask;public Task<CameraProperties> GetPropertiesAsync(string cameraId,CancellationToken token)=>Task.FromResult<CameraProperties>(null);public Task SetPropertyAsync(string cameraId,CameraPropertyKind property,string value,CancellationToken token)=>Task.CompletedTask;
        }
    }
}
