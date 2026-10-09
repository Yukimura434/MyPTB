using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class DatabaseNormalizationTests
    {
        [Fact]
        public void Version_three_schema_contains_only_canonical_operational_tables()
        {
            WithDatabase((root,database)=>
            {
                database.Initialize();database.Initialize();
                using(var connection=database.OpenConnection())
                {
                    Assert.Equal(3,Convert.ToInt32(Scalar(connection,"SELECT Version FROM SchemaInfo")));
                    Assert.Equal(12,Convert.ToInt32(Scalar(connection,@"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN
('Events','ConfigurationVersions','BoothSessions','CaptureAttempts','MediaAssets','AssetLineage','Deliverables','DeliverableAssets','OutputJobs','PrintJobs','SyncOutbox','RemoteCommandExecutions')")));
                    Assert.Equal(2,Convert.ToInt32(Scalar(connection,@"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN
('ConfigurationDeployments','ConfigurationDeploymentAssets')")));
                    Assert.Equal(1,Convert.ToInt32(Scalar(connection,@"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='MediaAssetReplicas'")));
                    Assert.Equal(0,Convert.ToInt32(Scalar(connection,@"SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name IN
('SchemaMigrations','CustomerSessions','CapturedImages','Captures','CapturePhotos','CaptureAssetSources','UploadQueue')")));
                    Assert.Equal("ok",Convert.ToString(Scalar(connection,"PRAGMA integrity_check")));
                }
            });
        }

        [Fact]
        public async Task Preferred_camera_survives_settings_round_trip_and_schema_reinitialization()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();
                var repository=new SqliteSettingsRepository(database);
                const string cameraId="camera://booth-primary";
                await repository.SaveAsync(new Settings{PreferredCameraId=cameraId},CancellationToken.None);
                database.Initialize();
                Assert.Equal(cameraId,(await repository.GetAsync(CancellationToken.None)).PreferredCameraId);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public void Legacy_event_frame_sort_cap_is_removed_during_schema_reinitialization()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"event-frame-cap.db"));database.Initialize();
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText=@"DROP TABLE EventFrames;
CREATE TABLE EventFrames(EventId TEXT NOT NULL,FrameId TEXT NOT NULL,SortOrder INTEGER NOT NULL CHECK(SortOrder BETWEEN 0 AND 9),PRIMARY KEY(EventId,FrameId),UNIQUE(EventId,SortOrder),FOREIGN KEY(EventId) REFERENCES EventConfigurations(EventId) ON DELETE CASCADE,FOREIGN KEY(FrameId) REFERENCES Frames(Id) ON DELETE CASCADE);";
                    command.ExecuteNonQuery();
                }

                database.Initialize();

                using(var connection=database.OpenConnection())
                {
                    var definition=Convert.ToString(Scalar(connection,"SELECT sql FROM sqlite_master WHERE type='table' AND name='EventFrames'"));
                    Assert.DoesNotContain("BETWEEN 0 AND 9",definition,StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("SortOrder>=0",definition,StringComparison.OrdinalIgnoreCase);
                    Assert.Equal("ok",Convert.ToString(Scalar(connection,"PRAGMA integrity_check")));
                }
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public void Version_two_device_identity_is_migrated_without_losing_credential()
        {
            var root=NewRoot();var path=Path.Combine(root,"legacy-v2.db");
            try
            {
                using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
                {connection.Open();using(var command=connection.CreateCommand()){command.CommandText=@"CREATE TABLE SchemaInfo(Version INTEGER PRIMARY KEY CHECK(Version=2),AppliedAtUtc TEXT NOT NULL,AppBuild TEXT NOT NULL);INSERT INTO SchemaInfo VALUES(2,'2026-01-01','2.x');CREATE TABLE DeviceIdentity(Id INTEGER PRIMARY KEY CHECK(Id=1),OrganizationId TEXT,DeviceId TEXT,LocationId TEXT,DeviceName TEXT,EnrollmentState TEXT NOT NULL,CredentialVersion INTEGER,LastAuthenticatedAtUtc TEXT,LastHeartbeatAtUtc TEXT,UpdatedAtUtc TEXT NOT NULL);CREATE TABLE SecureArtifacts(ArtifactType TEXT PRIMARY KEY,ProtectedValue BLOB NOT NULL,CreatedAtUtc TEXT NOT NULL,ExpiresAtUtc TEXT,UpdatedAtUtc TEXT NOT NULL);INSERT INTO DeviceIdentity VALUES(1,'org_1','dev_1','loc_1','Booth 1','Enrolled',1,'2026-01-01',NULL,'2026-01-01');INSERT INTO SecureArtifacts VALUES('DeviceCredential',X'010203','2026-01-01',NULL,'2026-01-01');";command.ExecuteNonQuery();}}
                var database=new SqliteDatabase(path);database.Initialize();
                using(var connection=database.OpenConnection())
                {Assert.Equal(3,Convert.ToInt32(Scalar(connection,"SELECT Version FROM SchemaInfo")));Assert.Equal("dev_1",Convert.ToString(Scalar(connection,"SELECT DeviceId FROM DeviceProfiles")));Assert.Equal(3,((byte[])Scalar(connection,"SELECT ProtectedValue FROM ProfileSecureArtifacts WHERE ArtifactType='DeviceCredential'")).Length);Assert.Equal("ok",Convert.ToString(Scalar(connection,"PRAGMA integrity_check")));Assert.Equal(0,Convert.ToInt32(Scalar(connection,"SELECT COUNT(*) FROM pragma_foreign_key_check")));}
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public void Version_one_database_is_rejected_instead_of_partially_upgraded()
        {
            var root=NewRoot();var path=Path.Combine(root,"legacy.db");
            try
            {
                using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
                {connection.Open();using(var command=connection.CreateCommand()){command.CommandText="CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY); INSERT INTO SchemaMigrations VALUES(15);";command.ExecuteNonQuery();}}
                var error=Assert.Throws<InvalidOperationException>(()=>new SqliteDatabase(path).Initialize());
                Assert.Contains("incompatible",error.Message);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Event_labels_are_not_identity_keys_and_events_do_not_own_captures()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);
                await sessions.SaveAsync(Event(root,"Sự kiện trùng tên"),CancellationToken.None);
                await sessions.SaveAsync(Event(root,"Sự kiện trùng tên"),CancellationToken.None);
                var values=await sessions.GetAllAsync(CancellationToken.None);
                Assert.Equal(2,values.Count(x=>x.SessionName=="Sự kiện trùng tên"));
                Assert.All(values,x=>Assert.Empty(x.CapturedShots));
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Accepted_capture_and_deliverable_round_trip_through_v2_tables()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);
                var photoEvent=Event(root,"Festival");await sessions.SaveAsync(photoEvent,CancellationToken.None);
                var booth=Booth(root,photoEvent.Id);await sessions.SaveAsync(booth,CancellationToken.None);
                var attemptId=Guid.NewGuid().ToString("N");var pictureId=Guid.NewGuid().ToString("N");var compositeId=Guid.NewGuid().ToString("N");
                var picture=Write(root,"Captures/one.jpg","picture");var composite=Write(root,"Captures/final.png","composite");
                var attempts=new SqliteCaptureAttemptRepository(database);
                await attempts.BeginAsync(new CaptureAttemptRecord{Id=attemptId,SessionId=booth.Id,Sequence=1,AttemptNumber=1,IntentAtUtc=DateTime.UtcNow},CancellationToken.None);
                await attempts.MarkAcceptedAsync(attemptId,CancellationToken.None);
                await sessions.AddCapturedShotAsync(booth.Id,new CapturedShot{Id=attemptId,Sequence=1,PictureAssetId=pictureId,PicturePath=picture,CapturedAtUtc=DateTime.UtcNow},CancellationToken.None);
                var deliverableId=Guid.NewGuid().ToString("N");var repository=new SqliteCaptureRepository(database);
                await repository.SaveAsync(new PhotoCapture
                {
                    Id=deliverableId,SessionId=booth.Id,CompositeImageId=compositeId,CompositePath=composite,Status="Ready",CreatedAtUtc=DateTime.UtcNow,
                    Photos=new[]
                    {
                        new CapturePhoto{Id=pictureId,CaptureId=deliverableId,CapturedImageId=attemptId,LocalPath=picture,PhotoType=CaptureAssetTypes.Picture,Position=1,MimeType="image/jpeg",FileLength=7,CreatedAtUtc=DateTime.UtcNow,AssetStatus=MediaAssetStates.Ready,SourceAssetIds=new string[0]},
                        new CapturePhoto{Id=compositeId,CaptureId=deliverableId,LocalPath=composite,PhotoType=CaptureAssetTypes.Composite,Position=0,MimeType="image/png",FileLength=9,CreatedAtUtc=DateTime.UtcNow,AssetStatus=MediaAssetStates.Ready,SourceAssetIds=new[]{pictureId}}
                    }
                },CancellationToken.None);

                var loaded=await repository.GetAsync(deliverableId,CancellationToken.None);
                Assert.Equal(booth.Id,loaded.SessionId);Assert.Equal(2,loaded.Photos.Count);
                Assert.Equal(pictureId,loaded.Photos.Single(x=>x.Id==compositeId).SourceAssetIds.Single());
                Assert.Equal(attemptId,Assert.Single((await sessions.GetAsync(booth.Id,CancellationToken.None)).CapturedShots).Id);
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="PRAGMA foreign_key_check";using(var reader=command.ExecuteReader())Assert.False(reader.Read());}
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Asset_lineage_rejects_relationships_between_booth_sessions()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);var assets=new SqliteMediaAssetRepository(database);
                var photoEvent=Event(root,"Event");await sessions.SaveAsync(photoEvent,CancellationToken.None);var first=Booth(root,photoEvent.Id);var second=Booth(root,photoEvent.Id);await sessions.SaveAsync(first,CancellationToken.None);await sessions.SaveAsync(second,CancellationToken.None);
                var firstId=Guid.NewGuid().ToString("N");var secondId=Guid.NewGuid().ToString("N");var now=DateTime.UtcNow;
                await assets.SaveAsync(Asset(first.Id,firstId,"Captures/first.png",now),CancellationToken.None);await assets.SaveAsync(Asset(second.Id,secondId,"Captures/second.png",now),CancellationToken.None);
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {command.CommandText="INSERT INTO AssetLineage(DerivedAssetId,SourceAssetId,Purpose) VALUES($derived,$source,'Composite')";command.Parameters.AddWithValue("$derived",firstId);command.Parameters.AddWithValue("$source",secondId);Assert.Throws<SqliteException>(()=>command.ExecuteNonQuery());}
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Retake_selects_only_the_accepted_replacement_attempt()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);var attempts=new SqliteCaptureAttemptRepository(database);var assets=new SqliteMediaAssetRepository(database);
                var photoEvent=Event(root,"Event");await sessions.SaveAsync(photoEvent,CancellationToken.None);var booth=Booth(root,photoEvent.Id);await sessions.SaveAsync(booth,CancellationToken.None);
                var oldShot=await RecordShot(root,booth.Id,1,1,"old",attempts,assets);await sessions.AddCapturedShotAsync(booth.Id,oldShot,CancellationToken.None);
                var replacement=await RecordShot(root,booth.Id,1,2,"new",attempts,assets);await sessions.ReplaceCapturedShotAsync(booth.Id,oldShot.Id,replacement,CancellationToken.None);
                var loaded=await sessions.GetAsync(booth.Id,CancellationToken.None);
                Assert.Equal(replacement.Id,Assert.Single(loaded.CapturedShots).Id);Assert.EndsWith("new.jpg",loaded.CapturedFiles.Single());
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Statistics_and_library_are_derived_from_deliverables_and_assets()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);var photoEvent=Event(root,"Summer Festival");await sessions.SaveAsync(photoEvent,CancellationToken.None);var booth=Booth(root,photoEvent.Id);booth.DisplayCode="S-001";await sessions.SaveAsync(booth,CancellationToken.None);
                var now=DateTime.UtcNow;var pictureId=Guid.NewGuid().ToString("N");var finalId=Guid.NewGuid().ToString("N");var deliverableId=Guid.NewGuid().ToString("N");var picture=Write(root,"Captures/library.jpg","picture");var final=Write(root,"Captures/library-final.png","final");
                await new SqliteCaptureRepository(database).SaveAsync(new PhotoCapture{Id=deliverableId,SessionId=booth.Id,CompositeImageId=finalId,CompositePath=final,Status="Ready",CreatedAtUtc=now,Photos=new[]{new CapturePhoto{Id=pictureId,CaptureId=deliverableId,LocalPath=picture,PhotoType=CaptureAssetTypes.Picture,Position=1,MimeType="image/jpeg",FileLength=7,CreatedAtUtc=now,AssetStatus=MediaAssetStates.Ready,SourceAssetIds=new string[0]},new CapturePhoto{Id=finalId,CaptureId=deliverableId,LocalPath=final,PhotoType=CaptureAssetTypes.Composite,Position=0,MimeType="image/png",FileLength=5,CreatedAtUtc=now,AssetStatus=MediaAssetStates.Ready,SourceAssetIds=new[]{pictureId}}}},CancellationToken.None);
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){var outputId=Guid.NewGuid().ToString("N");command.CommandText=@"UPDATE BoothSessions SET BasePriceMinor=100000,AdditionalPrintPriceMinor=20000,Currency='VND' WHERE Id=$session;
INSERT INTO OutputJobs(Id,BoothSessionId,DeliverableId,MediaAssetId,JobType,IdempotencyKey,State,CreatedAtUtc,UpdatedAtUtc) VALUES($output,$session,$deliverable,$asset,'Print',$key,'Completed',$now,$now);
INSERT INTO PrintJobs(Id,OutputJobId,BoothSessionId,DeliverableId,PrinterNameSnapshot,Copies,SettingsJson,Outcome,RequestedAtUtc,CompletedAtUtc) VALUES($id,$output,$session,$deliverable,'Printer',3,'{}','Success',$now,$now)";command.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));command.Parameters.AddWithValue("$output",outputId);command.Parameters.AddWithValue("$key","print:"+outputId);command.Parameters.AddWithValue("$session",booth.Id.ToString());command.Parameters.AddWithValue("$deliverable",deliverableId);command.Parameters.AddWithValue("$asset",finalId);command.Parameters.AddWithValue("$now",now.ToString("O"));command.ExecuteNonQuery();}
                booth.Status=BoothSessionStates.Finalizing;booth.UpdatedAtUtc=DateTime.UtcNow;booth.StateVersion++;await sessions.SaveAsync(booth,CancellationToken.None);
                booth.Status=BoothSessionStates.Completed;booth.CompletedAtUtc=DateTime.UtcNow;booth.UpdatedAtUtc=booth.CompletedAtUtc.Value;booth.StateVersion++;await sessions.SaveAsync(booth,CancellationToken.None);
                var repository=new SqliteStatsRepository(database);var stats=await repository.GetDataStatisticsAsync(CancellationToken.None);Assert.Equal(1,stats.SessionCount);Assert.Equal(1,stats.CaptureCount);Assert.Equal(1,stats.PictureCount);Assert.Equal(1,stats.SuccessfulPrintCount);Assert.Equal(12,stats.TotalAssetBytes);
                var library=await repository.SearchCaptureLibraryAsync(new CaptureLibraryFilter{Mode=CaptureLibraryFilterModes.Event,Query="festival"},CancellationToken.None);Assert.Equal(1,library.CaptureCount);Assert.Equal(3,library.PrintedPhotoCount);Assert.Equal(2,library.ExtraPrintCount);Assert.True(library.HasRevenueData);Assert.Equal(140000m,library.RevenueAmount);var capture=Assert.Single(library.Captures);Assert.Equal(deliverableId,capture.CaptureId);Assert.Equal(140000m,capture.RevenueAmount);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Capture_library_and_upload_queue_are_isolated_to_the_active_device()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();
                var sessions=new SqliteSessionRepository(database);var captures=new SqliteCaptureRepository(database);var jobs=new SqliteDurableOutputJobRepository(database);
                var photoEvent=Event(root,"Device isolation");await sessions.SaveAsync(photoEvent,CancellationToken.None);

                SetActiveDevice(database,"profile-a","dev-a");
                var boothA=Booth(root,photoEvent.Id);await sessions.SaveAsync(boothA,CancellationToken.None);
                var captureA=await SaveCapture(root,captures,boothA.Id,"a");
                await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=boothA.Id,DeliverableId=captureA,JobType=DurableOutputJobTypes.CommitDeliverable,IdempotencyKey="commit:"+captureA,CreatedAtUtc=DateTime.UtcNow},CancellationToken.None);

                SetActiveDevice(database,"profile-b","dev-b");
                var boothB=Booth(root,photoEvent.Id);await sessions.SaveAsync(boothB,CancellationToken.None);
                var captureB=await SaveCapture(root,captures,boothB.Id,"b");
                await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=boothB.Id,DeliverableId=captureB,JobType=DurableOutputJobTypes.CommitDeliverable,IdempotencyKey="commit:"+captureB,CreatedAtUtc=DateTime.UtcNow.AddSeconds(1)},CancellationToken.None);

                var statistics=new SqliteStatsRepository(database);
                Assert.Equal(1,await statistics.CountSessionsAsync(CancellationToken.None));
                var library=await statistics.SearchCaptureLibraryAsync(new CaptureLibraryFilter(),CancellationToken.None);
                Assert.Equal(1,library.CaptureCount);Assert.Equal(captureB,Assert.Single(library.Captures).CaptureId);
                Assert.Empty(await statistics.GetCaptureMediaAsync(captureA,CancellationToken.None));
                Assert.NotEmpty(await statistics.GetCaptureMediaAsync(captureB,CancellationToken.None));

                var leased=await jobs.LeaseNextDueAsync("lease-b",DateTime.UtcNow.AddMinutes(1),CancellationToken.None);
                Assert.NotNull(leased);Assert.Equal(boothB.Id,leased.SessionId);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        static void SetActiveDevice(SqliteDatabase database,string profileId,string deviceId)
        {
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                var now=DateTime.UtcNow.ToString("O");
                command.CommandText=@"INSERT INTO DeviceProfiles(Id,InstallationId,OrganizationId,LocationId,DeviceId,DeviceName,EnrollmentState,CredentialVersion,LastSelectedAtUtc,CreatedAtUtc,UpdatedAtUtc)
VALUES($profile,'local-installation','org-test',$device,$device,$device,'Enrolled',1,$now,$now,$now)
ON CONFLICT(Id) DO UPDATE SET LastSelectedAtUtc=$now,UpdatedAtUtc=$now;
UPDATE AppState SET ActiveProfileId=$profile,UpdatedAtUtc=$now WHERE Id=1";
                command.Parameters.AddWithValue("$profile",profileId);command.Parameters.AddWithValue("$device",deviceId);command.Parameters.AddWithValue("$now",now);command.ExecuteNonQuery();
            }
        }

        static async Task<string> SaveCapture(string root,SqliteCaptureRepository captures,Guid sessionId,string name)
        {
            var now=DateTime.UtcNow;var pictureId=Guid.NewGuid().ToString("N");var deliverableId=Guid.NewGuid().ToString("N");var picture=Write(root,"Captures/"+name+".jpg",name);
            await captures.SaveAsync(new PhotoCapture{Id=deliverableId,SessionId=sessionId,CompositePath=picture,Status="Ready",CreatedAtUtc=now,Photos=new[]{new CapturePhoto{Id=pictureId,CaptureId=deliverableId,LocalPath=picture,PhotoType=CaptureAssetTypes.Picture,Position=1,MimeType="image/jpeg",FileLength=1,CreatedAtUtc=now,AssetStatus=MediaAssetStates.Ready,SourceAssetIds=new string[0]}}},CancellationToken.None);
            return deliverableId;
        }

        static async Task<CapturedShot> RecordShot(string root,Guid boothId,int sequence,int attemptNumber,string name,SqliteCaptureAttemptRepository attempts,SqliteMediaAssetRepository assets)
        {
            var now=DateTime.UtcNow;var attemptId=Guid.NewGuid().ToString("N");var assetId=Guid.NewGuid().ToString("N");var path=Write(root,"Captures/"+name+".jpg",name);
            await attempts.BeginAsync(new CaptureAttemptRecord{Id=attemptId,SessionId=boothId,Sequence=sequence,AttemptNumber=attemptNumber,IntentAtUtc=now},CancellationToken.None);
            await assets.SaveAsync(new MediaAssetRecord{Id=assetId,SessionId=boothId,CaptureAttemptId=attemptId,Kind=MediaAssetKinds.OriginalPicture,RelativePath="Captures/"+name+".jpg",MimeType="image/jpeg",FileLength=name.Length,Status=MediaAssetStates.Ready,RetentionClass=MediaRetentionClasses.Original,CreatedAtUtc=now,UpdatedAtUtc=now},CancellationToken.None);
            await attempts.MarkAcceptedAsync(attemptId,CancellationToken.None);
            return new CapturedShot{Id=attemptId,Sequence=sequence,PictureAssetId=assetId,PicturePath=path,CapturedAtUtc=now};
        }

        static Session Event(string root,string name)=>new Session{Id=Guid.NewGuid(),Kind=SessionKinds.Event,SessionName=name,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=new CapturedShot[0]};
        static Session Booth(string root,Guid eventId){var id=Guid.NewGuid();var path=Path.Combine(root,"Sessions",id.ToString("N"));Directory.CreateDirectory(path);return new Session{Id=id,Kind=SessionKinds.Booth,EventId=eventId,Status=BoothSessionStates.Active,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=path,CapturedShots=new CapturedShot[0]};}
        static MediaAssetRecord Asset(Guid sessionId,string id,string path,DateTime now)=>new MediaAssetRecord{Id=id,SessionId=sessionId,Kind=MediaAssetKinds.FinalComposite,RelativePath=path,MimeType="image/png",FileLength=1,Status=MediaAssetStates.Ready,RetentionClass=MediaRetentionClasses.Deliverable,CreatedAtUtc=now,UpdatedAtUtc=now};
        static string Write(string root,string relative,string content){var path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,content);return path;}
        static object Scalar(SqliteConnection connection,string sql){using(var command=connection.CreateCommand()){command.CommandText=sql;return command.ExecuteScalar();}}
        static string NewRoot(){var root=Path.Combine(Path.GetTempPath(),"PhotoBoothV2Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
        static void WithDatabase(Action<string,SqliteDatabase> action){var root=NewRoot();try{action(root,new SqliteDatabase(Path.Combine(root,"test.db")));}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}}
    }
}
