using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace PhotoBooth.Database
{
    /// <summary>Creates and safely migrates the canonical PhotoBooth local database.</summary>
    public sealed class SqliteDatabase
    {
        public const int SchemaVersion = 3;
        readonly string connectionString;
        public string DataRoot { get; }

        public SqliteDatabase(string databasePath)
        {
            var directory=Path.GetDirectoryName(databasePath);
            if(!string.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);
            DataRoot=Path.GetFullPath(string.IsNullOrWhiteSpace(directory)?Directory.GetCurrentDirectory():directory);
            connectionString=new SqliteConnectionStringBuilder{DataSource=databasePath}.ToString();
        }

        public SqliteConnection OpenConnection()
        {
            var connection=new SqliteConnection(connectionString);connection.Open();
            using(var command=connection.CreateCommand()){command.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";command.ExecuteNonQuery();}
            return connection;
        }

        public void Initialize()
        {
            using(var connection=OpenConnection())
            {
                MigrateCompatibleDatabase(connection);
                using(var command=connection.CreateCommand()){command.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";command.ExecuteNonQuery();}
            }
            using(var connection=OpenConnection())using(var transaction=connection.BeginTransaction())using(var command=connection.CreateCommand())
            {command.Transaction=transaction;command.CommandText=Schema;command.ExecuteNonQuery();transaction.Commit();}
            EnsureEventFrameSelectionSchema();
            EnsureEventLifecycleSchema();
            EnsureConfigurationDeploymentSchema();
            EnsureMediaReplicaSchema();
            EnsureEventExportSchema();
            EnsureBeautyColumns();
            EnsureEventConfigurationContractColumns();
            EnsureShareSettings();
            EnsureRevenueSchema();
            EnsureDeviceProfileSchema();
            AdoptUnassignedLocalSessions();
            EnsureWorkflowSettings();
            using(var connection=OpenConnection())EnsureColumn(connection,"PrinterProfiles","MediaType","TEXT");
            using(var connection=OpenConnection())EnsureColumn(connection,"OutputJobs","AuthorizationScope","TEXT");
            using (var connection = OpenConnection()) EnsureColumn(connection, "BoothSessions", "RemoteEventId", "TEXT");
        }
        void EnsureEventFrameSelectionSchema()
        {
            using(var connection=OpenConnection())
            using(var inspect=connection.CreateCommand())
            {
                inspect.CommandText="SELECT sql FROM sqlite_master WHERE type='table' AND name='EventFrames'";
                var definition=inspect.ExecuteScalar() as string;
                if(string.IsNullOrWhiteSpace(definition)||definition.IndexOf("BETWEEN 0 AND 9",StringComparison.OrdinalIgnoreCase)<0)return;
                using(var transaction=connection.BeginTransaction())
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;
                    command.CommandText=@"
DROP TABLE IF EXISTS EventFrames_Unbounded;
CREATE TABLE EventFrames_Unbounded(
 EventId TEXT NOT NULL,
 FrameId TEXT NOT NULL,
 SortOrder INTEGER NOT NULL CHECK(SortOrder>=0),
 PRIMARY KEY(EventId,FrameId),
 UNIQUE(EventId,SortOrder),
 FOREIGN KEY(EventId) REFERENCES EventConfigurations(EventId) ON DELETE CASCADE,
 FOREIGN KEY(FrameId) REFERENCES Frames(Id) ON DELETE CASCADE
);
INSERT INTO EventFrames_Unbounded(EventId,FrameId,SortOrder)
SELECT EventId,FrameId,SortOrder FROM EventFrames;
DROP TABLE EventFrames;
ALTER TABLE EventFrames_Unbounded RENAME TO EventFrames;";
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
        }
        void EnsureConfigurationDeploymentSchema()
        {
            using(var connection=OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"
CREATE TABLE IF NOT EXISTS ConfigurationDeployments(
 Id TEXT PRIMARY KEY,
 RemoteAssignmentId TEXT NOT NULL UNIQUE,
 RemoteEventId TEXT NOT NULL,
 RemoteConfigurationId TEXT,
 ConfigurationVersionId TEXT,
 State TEXT NOT NULL CHECK(State IN ('Pending','Downloading','Ready','Applied','Failed','Superseded')),
 AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount>=0),
 NextRetryAtUtc TEXT,
 ReceivedAtUtc TEXT NOT NULL,
 DownloadStartedAtUtc TEXT,
 ReadyAtUtc TEXT,
 AppliedAtUtc TEXT,
 SupersededAtUtc TEXT,
 LastErrorCategory TEXT,
 LastError TEXT,
 UpdatedAtUtc TEXT NOT NULL,
 FOREIGN KEY(ConfigurationVersionId) REFERENCES ConfigurationVersions(Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_ConfigurationDeployments_Due ON ConfigurationDeployments(State,NextRetryAtUtc,ReceivedAtUtc);
CREATE TABLE IF NOT EXISTS ConfigurationDeploymentAssets(
 DeploymentId TEXT NOT NULL,
 AssetType TEXT NOT NULL CHECK(AssetType IN ('Frame','Preset','Lut')),
 RemoteAssetId TEXT NOT NULL,
 ExpectedHashSha256 TEXT,
 ExpectedBytes INTEGER,
 State TEXT NOT NULL CHECK(State IN ('Missing','Downloading','Verified','Failed')),
 LocalPath TEXT,
 LastErrorCategory TEXT,
 LastError TEXT,
 UpdatedAtUtc TEXT NOT NULL,
 PRIMARY KEY(DeploymentId,AssetType,RemoteAssetId),
 FOREIGN KEY(DeploymentId) REFERENCES ConfigurationDeployments(Id) ON DELETE CASCADE
);
INSERT OR IGNORE INTO ConfigurationDeployments(
 Id,RemoteAssignmentId,RemoteEventId,ConfigurationVersionId,State,AttemptCount,
 ReceivedAtUtc,ReadyAtUtc,AppliedAtUtc,LastError,UpdatedAtUtc)
SELECT a.Id,COALESCE(a.RemoteAssignmentId,a.Id),COALESCE(e.RemoteEventId,e.Id),a.ConfigurationVersionId,
 CASE a.State WHEN 'Applied' THEN 'Ready' WHEN 'Failed' THEN 'Failed' WHEN 'Superseded' THEN 'Superseded' ELSE 'Pending' END,
 CASE a.State WHEN 'Failed' THEN 1 ELSE 0 END,a.ReceivedAtUtc,
 CASE a.State WHEN 'Applied' THEN COALESCE(a.AppliedAtUtc,a.ReceivedAtUtc) END,NULL,a.LastError,
 COALESCE(a.AppliedAtUtc,a.ReceivedAtUtc)
FROM DeviceConfigAssignments a
JOIN ConfigurationVersions c ON c.Id=a.ConfigurationVersionId
JOIN Events e ON e.Id=c.EventId;";
                command.ExecuteNonQuery();
            }
        }

        void EnsureMediaReplicaSchema()
        {
            using(var connection=OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"
CREATE TABLE IF NOT EXISTS MediaAssetReplicas(
 Id TEXT PRIMARY KEY,
 MediaAssetId TEXT NOT NULL,
 Provider TEXT NOT NULL CHECK(Provider IN ('Local','Online','SystemLibrary','GoogleDrive')),
 IsRequired INTEGER NOT NULL DEFAULT 1 CHECK(IsRequired IN (0,1)),
 PolicyReason TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Present','Queued','Uploading','Verified','Failed','Missing','Deleting','Deleted')),
 RemoteReference TEXT,
 AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount>=0),
 NextAttemptAtUtc TEXT,
 LastErrorCategory TEXT,
 LastError TEXT,
 ContentHashSha256 TEXT,
 FileLength INTEGER NOT NULL DEFAULT 0 CHECK(FileLength>=0),
 VerifiedAtUtc TEXT,
 CreatedAtUtc TEXT NOT NULL,
 UpdatedAtUtc TEXT NOT NULL,
 UNIQUE(MediaAssetId,Provider),
 FOREIGN KEY(MediaAssetId) REFERENCES MediaAssets(Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_MediaAssetReplicas_Health ON MediaAssetReplicas(Provider,State,NextAttemptAtUtc);
CREATE INDEX IF NOT EXISTS IX_MediaAssetReplicas_Asset ON MediaAssetReplicas(MediaAssetId,State);
INSERT OR IGNORE INTO MediaAssetReplicas(
 Id,MediaAssetId,Provider,IsRequired,PolicyReason,State,ContentHashSha256,FileLength,VerifiedAtUtc,CreatedAtUtc,UpdatedAtUtc)
SELECT 'rep-local-'||Id,Id,'Local',1,'CaptureOrigin',
 CASE State WHEN 'Ready' THEN CASE WHEN ContentHashSha256 IS NULL THEN 'Present' ELSE 'Verified' END
            WHEN 'Missing' THEN 'Missing' WHEN 'Corrupt' THEN 'Failed'
            WHEN 'PendingDelete' THEN 'Deleting' WHEN 'Deleted' THEN 'Deleted' ELSE 'Present' END,
 ContentHashSha256,FileLength,CASE WHEN State='Ready' AND ContentHashSha256 IS NOT NULL THEN UpdatedAtUtc END,CreatedAtUtc,UpdatedAtUtc
FROM MediaAssets;
CREATE TRIGGER IF NOT EXISTS TR_MediaAssetReplica_LocalInsert
AFTER INSERT ON MediaAssets
BEGIN
 INSERT OR IGNORE INTO MediaAssetReplicas(
  Id,MediaAssetId,Provider,IsRequired,PolicyReason,State,ContentHashSha256,FileLength,VerifiedAtUtc,CreatedAtUtc,UpdatedAtUtc)
 VALUES('rep-local-'||NEW.Id,NEW.Id,'Local',1,'CaptureOrigin',
  CASE NEW.State WHEN 'Ready' THEN CASE WHEN NEW.ContentHashSha256 IS NULL THEN 'Present' ELSE 'Verified' END
                 WHEN 'Missing' THEN 'Missing' WHEN 'Corrupt' THEN 'Failed'
                 WHEN 'PendingDelete' THEN 'Deleting' WHEN 'Deleted' THEN 'Deleted' ELSE 'Present' END,
  NEW.ContentHashSha256,NEW.FileLength,
  CASE WHEN NEW.State='Ready' AND NEW.ContentHashSha256 IS NOT NULL THEN NEW.UpdatedAtUtc END,
  NEW.CreatedAtUtc,NEW.UpdatedAtUtc);
END;
CREATE TRIGGER IF NOT EXISTS TR_MediaAssetReplica_LocalUpdate
AFTER UPDATE OF State,ContentHashSha256,FileLength,UpdatedAtUtc ON MediaAssets
BEGIN
 UPDATE MediaAssetReplicas SET
  State=CASE NEW.State WHEN 'Ready' THEN CASE WHEN NEW.ContentHashSha256 IS NULL THEN 'Present' ELSE 'Verified' END
                       WHEN 'Missing' THEN 'Missing' WHEN 'Corrupt' THEN 'Failed'
                       WHEN 'PendingDelete' THEN 'Deleting' WHEN 'Deleted' THEN 'Deleted' ELSE 'Present' END,
  ContentHashSha256=COALESCE(NEW.ContentHashSha256,ContentHashSha256),FileLength=NEW.FileLength,
  VerifiedAtUtc=CASE WHEN NEW.State='Ready' AND NEW.ContentHashSha256 IS NOT NULL THEN COALESCE(VerifiedAtUtc,NEW.UpdatedAtUtc) ELSE VerifiedAtUtc END,
  UpdatedAtUtc=NEW.UpdatedAtUtc
 WHERE MediaAssetId=NEW.Id AND Provider='Local';
END;
CREATE TRIGGER IF NOT EXISTS TR_MediaAsset_DeleteSafety
BEFORE UPDATE OF State ON MediaAssets
WHEN NEW.State='Deleted' AND OLD.State<>'Deleted' AND OLD.RetentionClass<>'Work'
BEGIN
 SELECT RAISE(ABORT,'media_asset_output_pending') WHERE EXISTS(
  SELECT 1 FROM OutputJobs j WHERE j.MediaAssetId=OLD.Id AND j.State NOT IN ('Completed','Cancelled'));
 SELECT RAISE(ABORT,'media_asset_event_not_delivered') WHERE NOT EXISTS(
  SELECT 1 FROM BoothSessions b JOIN Events e ON e.Id=b.EventId
  WHERE b.Id=OLD.BoothSessionId AND e.LifecycleState IN ('Delivered','Archived'));
 SELECT RAISE(ABORT,'media_asset_required_replica_not_verified') WHERE EXISTS(
  SELECT 1 FROM MediaAssetReplicas r WHERE r.MediaAssetId=OLD.Id AND r.Provider<>'Local'
   AND r.IsRequired=1 AND r.State NOT IN ('Verified','Deleted'));
END;";
                command.ExecuteNonQuery();
            }
        }

        void EnsureEventExportSchema()
        {
            using(var connection=OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"
CREATE TABLE IF NOT EXISTS EventExportJobs(
 Id TEXT PRIMARY KEY,
 EventId TEXT NOT NULL,
 BoothSessionId TEXT NOT NULL,
 DeliverableId TEXT NOT NULL,
 DestinationRoot TEXT NOT NULL,
 TurnFolderName TEXT NOT NULL,
 IdempotencyKey TEXT NOT NULL UNIQUE,
 State TEXT NOT NULL CHECK(State IN ('Pending','Leased','RetryWaiting','Completed','PermanentFailure','Cancelled')),
 AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount>=0),
 NextRetryAtUtc TEXT,
 LeaseId TEXT,
 LeaseExpiresAtUtc TEXT,
 ExportedPath TEXT,
 LastError TEXT,
 CreatedAtUtc TEXT NOT NULL,
 UpdatedAtUtc TEXT NOT NULL,
 FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE RESTRICT,
 FOREIGN KEY(BoothSessionId) REFERENCES BoothSessions(Id) ON DELETE RESTRICT,
 FOREIGN KEY(DeliverableId) REFERENCES Deliverables(Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_EventExportJobs_Due ON EventExportJobs(State,NextRetryAtUtc,LeaseExpiresAtUtc,CreatedAtUtc);
CREATE INDEX IF NOT EXISTS IX_EventExportJobs_Event ON EventExportJobs(EventId,State,CreatedAtUtc);
CREATE TRIGGER IF NOT EXISTS TR_MediaAsset_EventExportDeleteSafety
BEFORE UPDATE OF State ON MediaAssets
WHEN NEW.State='Deleted' AND OLD.State<>'Deleted'
BEGIN
 SELECT RAISE(ABORT,'media_asset_event_export_pending') WHERE EXISTS(
  SELECT 1 FROM DeliverableAssets da
  JOIN EventExportJobs j ON j.DeliverableId=da.DeliverableId
  WHERE da.MediaAssetId=OLD.Id AND j.State NOT IN ('Completed','Cancelled'));
END;";
                command.ExecuteNonQuery();
            }
        }

        void EnsureEventLifecycleSchema()
        {
            using(var connection=OpenConnection())
            {
                EnsureColumn(connection,"Events","LifecycleState","TEXT NOT NULL DEFAULT 'Open' CHECK(LifecycleState IN ('Draft','Scheduled','Open','Closed','Delivered','Archived'))");
                EnsureColumn(connection,"Events","ScheduledStartAtUtc","TEXT");
                EnsureColumn(connection,"Events","ScheduledEndAtUtc","TEXT");
                EnsureColumn(connection,"Events","OpenedAtUtc","TEXT");
                EnsureColumn(connection,"Events","ClosedAtUtc","TEXT");
                EnsureColumn(connection,"Events","DeliveredAtUtc","TEXT");
                EnsureColumn(connection,"Events","ArchivedAtUtc","TEXT");
                using(var command=connection.CreateCommand())
                {
                    command.CommandText=@"
UPDATE Events SET LifecycleState='Archived',ArchivedAtUtc=COALESCE(ArchivedAtUtc,UpdatedAtUtc) WHERE Status='Archived';
CREATE TABLE IF NOT EXISTS DeviceRuntimeState(
 Id INTEGER PRIMARY KEY CHECK(Id=1),
 SelectedEventId TEXT,
 SelectedConfigurationVersionId TEXT,
 SelectedAtUtc TEXT,
 UpdatedAtUtc TEXT NOT NULL,
 FOREIGN KEY(SelectedEventId) REFERENCES Events(Id) ON DELETE SET NULL,
 FOREIGN KEY(SelectedConfigurationVersionId) REFERENCES ConfigurationVersions(Id) ON DELETE SET NULL
);
INSERT OR IGNORE INTO DeviceRuntimeState(Id,SelectedEventId,SelectedConfigurationVersionId,SelectedAtUtc,UpdatedAtUtc)
SELECT 1,e.Id,(SELECT Id FROM ConfigurationVersions c WHERE c.EventId=e.Id AND c.State='Active' ORDER BY c.VersionNumber DESC LIMIT 1),strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now')
FROM Events e WHERE e.IsDefault=1 ORDER BY e.UpdatedAtUtc DESC LIMIT 1;
INSERT OR IGNORE INTO DeviceRuntimeState(Id,SelectedEventId,SelectedConfigurationVersionId,SelectedAtUtc,UpdatedAtUtc)
VALUES(1,NULL,NULL,NULL,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
UPDATE Events SET IsDefault=0 WHERE IsDefault<>0;
CREATE INDEX IF NOT EXISTS IX_Events_Lifecycle_Updated ON Events(LifecycleState,UpdatedAtUtc DESC);";
                    command.ExecuteNonQuery();
                }
            }
        }
        void EnsureWorkflowSettings()
        {
            using(var connection=OpenConnection()){EnsureColumn(connection,"WorkflowSettings","CreateGif","INTEGER NOT NULL DEFAULT 1");EnsureColumn(connection,"WorkflowSettings","CreateVideo","INTEGER NOT NULL DEFAULT 1");}
            using(var connection=OpenConnection())EnsureColumn(connection,"WorkflowSettings","PrintingEnabled","INTEGER NOT NULL DEFAULT 1 CHECK(PrintingEnabled IN (0,1))");
            using(var connection=OpenConnection())EnsureColumn(connection,"WorkflowSettings","PreferredCameraId","TEXT");
            using (var connection = OpenConnection())
            {
                EnsureColumn(connection, "WorkflowSettings", "SelectedFrameCollections", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, "WorkflowSettings", "SelectedPresetCollections", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(connection, "WorkflowSettings", "BoothSettingsConfigured", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, "WorkflowSettings", "LiveViewPresetId", "TEXT NULL");
                EnsureColumn(connection, "WorkflowSettings", "LiveViewPresetStrengthPercent", "INTEGER NOT NULL DEFAULT 50");
                EnsureColumn(connection, "AdminPresets", "StrengthPercent", "INTEGER NOT NULL DEFAULT 50 CHECK(StrengthPercent BETWEEN 0 AND 100)");
            }
            using(var connection=OpenConnection())EnsureColumn(connection,"OutputJobs","ProtectedAuthorization","TEXT");
        }
        void EnsureShareSettings()
        {
            using(var connection=OpenConnection())EnsureColumn(connection,"ProductionSettings","ShareDeliveryMode","TEXT NOT NULL DEFAULT 'CloudWithLanFallback'");
        }

        void EnsureBeautyColumns()
        {
            using(var connection=OpenConnection())
            {
                EnsureColumn(connection,"BeautySettings","BlemishRemoval","INTEGER NOT NULL DEFAULT 0 CHECK(BlemishRemoval BETWEEN 0 AND 100)");
                EnsureColumn(connection,"BeautySettings","DarkCircleReduction","INTEGER NOT NULL DEFAULT 0 CHECK(DarkCircleReduction BETWEEN 0 AND 100)");
                EnsureColumn(connection,"EventConfigurations","BlemishRemoval","INTEGER NOT NULL DEFAULT 0 CHECK(BlemishRemoval BETWEEN 0 AND 100)");
                EnsureColumn(connection,"EventConfigurations","DarkCircleReduction","INTEGER NOT NULL DEFAULT 0 CHECK(DarkCircleReduction BETWEEN 0 AND 100)");
            }
        }
        void EnsureEventConfigurationContractColumns()
        {
            using(var connection=OpenConnection())
            {
                EnsureColumn(connection,"EventConfigurations","PrintPolicyMode","TEXT NOT NULL DEFAULT 'Manual' CHECK(PrintPolicyMode IN ('Disabled','Manual','Automatic'))");
                EnsureColumn(connection,"EventConfigurations","IncludedPrintCopies","INTEGER NOT NULL DEFAULT 1 CHECK(IncludedPrintCopies BETWEEN 0 AND 10)");
                EnsureColumn(connection,"EventConfigurations","MaximumPrintCopies","INTEGER NOT NULL DEFAULT 10 CHECK(MaximumPrintCopies BETWEEN 0 AND 20)");
                EnsureColumn(connection,"EventConfigurations","ColorPresetStrengthPercent","INTEGER NOT NULL DEFAULT 50 CHECK(ColorPresetStrengthPercent BETWEEN 0 AND 100)");
            }
        }
        void EnsureRevenueSchema()
        {
            using(var connection=OpenConnection())
            {
                EnsureColumn(connection,"EventConfigurations","BasePriceMinor","INTEGER NOT NULL DEFAULT 0 CHECK(BasePriceMinor>=0)");
                EnsureColumn(connection,"EventConfigurations","AdditionalPrintPriceMinor","INTEGER NOT NULL DEFAULT 0 CHECK(AdditionalPrintPriceMinor>=0)");
                EnsureColumn(connection,"EventConfigurations","Currency","TEXT NOT NULL DEFAULT 'VND'");
                EnsureColumn(connection,"BoothSessions","BasePriceMinor","INTEGER NOT NULL DEFAULT 0 CHECK(BasePriceMinor>=0)");
                EnsureColumn(connection,"BoothSessions","AdditionalPrintPriceMinor","INTEGER NOT NULL DEFAULT 0 CHECK(AdditionalPrintPriceMinor>=0)");
                EnsureColumn(connection,"BoothSessions","Currency","TEXT NOT NULL DEFAULT 'VND'");
                using(var command=connection.CreateCommand())
                {
                    command.CommandText=RevenueSchema;
                    command.ExecuteNonQuery();
                }
            }
        }
        static void EnsureColumn(SqliteConnection connection,string table,string column,string definition)
        {
            using(var check=connection.CreateCommand()){check.CommandText="SELECT COUNT(*) FROM pragma_table_info('"+table+"') WHERE name=$name";check.Parameters.AddWithValue("$name",column);if(Convert.ToInt32(check.ExecuteScalar())!=0)return;}
            using(var alter=connection.CreateCommand()){alter.CommandText="ALTER TABLE "+table+" ADD COLUMN "+column+" "+definition;alter.ExecuteNonQuery();}
        }

        static void MigrateCompatibleDatabase(SqliteConnection connection)
        {
            using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaInfo'";
                if(Convert.ToInt32(command.ExecuteScalar())==1)
                {
                    command.CommandText="SELECT Version FROM SchemaInfo LIMIT 1";
                    var version=command.ExecuteScalar();
                    if(version!=null&&version!=System.DBNull.Value)
                    {
                        var current=Convert.ToInt32(version);
                        if(current==2){MigrateV2ToV3(connection);return;}
                        if(current!=SchemaVersion)throw new System.InvalidOperationException("Unsupported PhotoBooth database version "+current+". Expected version 2 or 3.");
                    }
                    return;
                }

                command.CommandText=@"SELECT COUNT(*) FROM sqlite_master
WHERE (type='table' AND name IN ('SchemaMigrations','CustomerSessions','CapturedImages','Captures','CapturePhotos','CaptureAssetSources','UploadQueue'))
   OR (type='view' AND name IN ('Events','BoothSessions','Deliverables','DeliverableAssets'))";
                if(Convert.ToInt32(command.ExecuteScalar())>0)
                    throw new System.InvalidOperationException("PhotoBooth v1 databases are intentionally incompatible with version 2. Keep the old file as a backup and start with photobooth-v2.db.");
            }
        }

        static void MigrateV2ToV3(SqliteConnection connection)
        {
            using(var transaction=connection.BeginTransaction())
            using(var command=connection.CreateCommand())
            {
                command.Transaction=transaction;
                command.CommandText=@"
CREATE TABLE DeviceInstallations(
 Id TEXT PRIMARY KEY,
 PublicId TEXT NOT NULL UNIQUE,
 Fingerprint TEXT,
 Platform TEXT NOT NULL DEFAULT 'windows',
 AppVersion TEXT,
 Status TEXT NOT NULL DEFAULT 'Active' CHECK(Status IN ('Active','Blocked')),
 CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL);
CREATE TABLE DeviceProfiles(
 Id TEXT PRIMARY KEY,InstallationId TEXT NOT NULL,OrganizationId TEXT NOT NULL,LocationId TEXT NOT NULL,
 DeviceId TEXT NOT NULL UNIQUE,DeviceName TEXT NOT NULL,OrganizationName TEXT,LocationName TEXT,
 EnrollmentState TEXT NOT NULL DEFAULT 'Enrolled' CHECK(EnrollmentState IN ('Enrolled','Revoked','Suspended')),
 CredentialVersion INTEGER NOT NULL DEFAULT 1,LastAuthenticatedAtUtc TEXT,LastSelectedAtUtc TEXT NOT NULL,
 CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,
 FOREIGN KEY(InstallationId) REFERENCES DeviceInstallations(Id) ON DELETE RESTRICT,
 UNIQUE(InstallationId,OrganizationId,LocationId));
CREATE TABLE ProfileSecureArtifacts(
 ProfileId TEXT NOT NULL,ArtifactType TEXT NOT NULL,ProtectedValue BLOB NOT NULL,
 CreatedAtUtc TEXT NOT NULL,ExpiresAtUtc TEXT,UpdatedAtUtc TEXT NOT NULL,
 PRIMARY KEY(ProfileId,ArtifactType),FOREIGN KEY(ProfileId) REFERENCES DeviceProfiles(Id) ON DELETE CASCADE,
 CHECK(ArtifactType IN ('DeviceCredential','SessionToken')));
CREATE TABLE AppState(Id INTEGER PRIMARY KEY CHECK(Id=1),InstallationId TEXT NOT NULL,ActiveProfileId TEXT,
 UpdatedAtUtc TEXT NOT NULL,FOREIGN KEY(InstallationId) REFERENCES DeviceInstallations(Id) ON DELETE RESTRICT,
 FOREIGN KEY(ActiveProfileId) REFERENCES DeviceProfiles(Id) ON DELETE SET NULL);
INSERT INTO DeviceInstallations(Id,PublicId,Fingerprint,CreatedAtUtc,UpdatedAtUtc)
 VALUES('local-installation',lower(hex(randomblob(16))),NULL,strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'));
INSERT INTO AppState(Id,InstallationId,ActiveProfileId,UpdatedAtUtc)
 VALUES(1,'local-installation',NULL,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
INSERT INTO DeviceProfiles(Id,InstallationId,OrganizationId,LocationId,DeviceId,DeviceName,EnrollmentState,
 CredentialVersion,LastAuthenticatedAtUtc,LastSelectedAtUtc,CreatedAtUtc,UpdatedAtUtc)
 SELECT 'legacy-'||DeviceId,'local-installation',OrganizationId,COALESCE(LocationId,'legacy-location'),DeviceId,
 COALESCE(DeviceName,'Booth'),EnrollmentState,COALESCE(CredentialVersion,1),LastAuthenticatedAtUtc,
 strftime('%Y-%m-%dT%H:%M:%fZ','now'),UpdatedAtUtc,UpdatedAtUtc FROM DeviceIdentity
 WHERE Id=1 AND DeviceId IS NOT NULL AND OrganizationId IS NOT NULL AND EnrollmentState='Enrolled';
INSERT INTO ProfileSecureArtifacts(ProfileId,ArtifactType,ProtectedValue,CreatedAtUtc,ExpiresAtUtc,UpdatedAtUtc)
 SELECT 'legacy-'||d.DeviceId,'DeviceCredential',s.ProtectedValue,s.CreatedAtUtc,s.ExpiresAtUtc,s.UpdatedAtUtc
 FROM DeviceIdentity d JOIN SecureArtifacts s ON s.ArtifactType='DeviceCredential'
 WHERE d.Id=1 AND d.DeviceId IS NOT NULL AND d.OrganizationId IS NOT NULL AND d.EnrollmentState='Enrolled';
UPDATE AppState SET ActiveProfileId=(SELECT Id FROM DeviceProfiles ORDER BY CreatedAtUtc LIMIT 1),UpdatedAtUtc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE Id=1;
ALTER TABLE SchemaInfo RENAME TO SchemaInfoV2;
CREATE TABLE SchemaInfo(Version INTEGER PRIMARY KEY CHECK(Version=3),AppliedAtUtc TEXT NOT NULL,AppBuild TEXT NOT NULL);
INSERT INTO SchemaInfo VALUES(3,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'3.x');
DROP TABLE SchemaInfoV2;
";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        void EnsureDeviceProfileSchema()
        {
            using(var connection=OpenConnection())
            using(var command=connection.CreateCommand())
            {
                command.CommandText=DeviceProfileSchema;
                command.ExecuteNonQuery();
            }
        }

        void AdoptUnassignedLocalSessions()
        {
            using(var connection=OpenConnection())
            using(var command=connection.CreateCommand())
            {
                // Legacy builds stored locally captured sessions as "local-unenrolled"
                // even after the device-profile migration. Adoption is safe only when
                // this installation has exactly one enrolled profile; otherwise the
                // original device ownership is ambiguous and the rows remain isolated.
                command.CommandText=@"UPDATE BoothSessions
SET OriginDeviceId=(
 SELECT p.DeviceId FROM AppState a
 INNER JOIN DeviceProfiles p ON p.Id=a.ActiveProfileId
 WHERE a.Id=1 AND p.EnrollmentState='Enrolled'
)
WHERE OriginDeviceId='local-unenrolled'
AND (SELECT COUNT(*) FROM DeviceProfiles WHERE EnrollmentState='Enrolled')=1
AND EXISTS(
 SELECT 1 FROM AppState a
 INNER JOIN DeviceProfiles p ON p.Id=a.ActiveProfileId
 WHERE a.Id=1 AND p.EnrollmentState='Enrolled'
)";
                command.ExecuteNonQuery();
            }
        }

        const string DeviceProfileSchema=@"
CREATE TABLE IF NOT EXISTS DeviceInstallations(Id TEXT PRIMARY KEY,PublicId TEXT NOT NULL UNIQUE,Fingerprint TEXT,Platform TEXT NOT NULL DEFAULT 'windows',AppVersion TEXT,Status TEXT NOT NULL DEFAULT 'Active' CHECK(Status IN ('Active','Blocked')),CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS DeviceProfiles(Id TEXT PRIMARY KEY,InstallationId TEXT NOT NULL,OrganizationId TEXT NOT NULL,LocationId TEXT NOT NULL,DeviceId TEXT NOT NULL UNIQUE,DeviceName TEXT NOT NULL,OrganizationName TEXT,LocationName TEXT,EnrollmentState TEXT NOT NULL DEFAULT 'Enrolled' CHECK(EnrollmentState IN ('Enrolled','Revoked','Suspended')),CredentialVersion INTEGER NOT NULL DEFAULT 1,LastAuthenticatedAtUtc TEXT,LastSelectedAtUtc TEXT NOT NULL,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,FOREIGN KEY(InstallationId) REFERENCES DeviceInstallations(Id) ON DELETE RESTRICT,UNIQUE(InstallationId,OrganizationId,LocationId));
CREATE TABLE IF NOT EXISTS ProfileSecureArtifacts(ProfileId TEXT NOT NULL,ArtifactType TEXT NOT NULL,ProtectedValue BLOB NOT NULL,CreatedAtUtc TEXT NOT NULL,ExpiresAtUtc TEXT,UpdatedAtUtc TEXT NOT NULL,PRIMARY KEY(ProfileId,ArtifactType),FOREIGN KEY(ProfileId) REFERENCES DeviceProfiles(Id) ON DELETE CASCADE,CHECK(ArtifactType IN ('DeviceCredential','SessionToken')));
CREATE TABLE IF NOT EXISTS AppState(Id INTEGER PRIMARY KEY CHECK(Id=1),InstallationId TEXT NOT NULL,ActiveProfileId TEXT,UpdatedAtUtc TEXT NOT NULL,FOREIGN KEY(InstallationId) REFERENCES DeviceInstallations(Id) ON DELETE RESTRICT,FOREIGN KEY(ActiveProfileId) REFERENCES DeviceProfiles(Id) ON DELETE SET NULL);
INSERT OR IGNORE INTO DeviceInstallations(Id,PublicId,Fingerprint,CreatedAtUtc,UpdatedAtUtc) VALUES('local-installation',lower(hex(randomblob(16))),NULL,strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'));
INSERT OR IGNORE INTO AppState(Id,InstallationId,ActiveProfileId,UpdatedAtUtc) VALUES(1,'local-installation',NULL,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
CREATE INDEX IF NOT EXISTS IX_DeviceProfiles_Installation ON DeviceProfiles(InstallationId,LastSelectedAtUtc DESC);";

        const string RevenueSchema=@"
CREATE TABLE IF NOT EXISTS RevenueReports(
 BoothSessionId TEXT PRIMARY KEY,
 EventId TEXT NOT NULL,
 BasePriceMinor INTEGER NOT NULL CHECK(BasePriceMinor>=0),
 IncludedPrints INTEGER NOT NULL DEFAULT 1 CHECK(IncludedPrints=1),
 PrintedCopies INTEGER NOT NULL CHECK(PrintedCopies>=0),
 AdditionalPrintCopies INTEGER NOT NULL CHECK(AdditionalPrintCopies>=0),
 AdditionalPrintUnitPriceMinor INTEGER NOT NULL CHECK(AdditionalPrintUnitPriceMinor>=0),
 GrossRevenueMinor INTEGER NOT NULL CHECK(GrossRevenueMinor>=0),
 Currency TEXT NOT NULL,
 OpenedAtUtc TEXT NOT NULL,
 CompletedAtUtc TEXT NOT NULL,
 State TEXT NOT NULL DEFAULT 'Pending' CHECK(State IN ('Pending','Synced','RetryWaiting')),
 AttemptCount INTEGER NOT NULL DEFAULT 0,
 NextRetryAtUtc TEXT,
 LastError TEXT,
 UpdatedAtUtc TEXT NOT NULL,
 FOREIGN KEY(BoothSessionId) REFERENCES BoothSessions(Id) ON DELETE RESTRICT,
 FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_RevenueReports_Due ON RevenueReports(State,NextRetryAtUtc);
CREATE TRIGGER IF NOT EXISTS TR_BoothSession_Revenue
AFTER UPDATE OF State ON BoothSessions
WHEN NEW.State='Completed' AND OLD.State<>'Completed'
BEGIN
 INSERT OR IGNORE INTO RevenueReports(
  BoothSessionId,EventId,BasePriceMinor,IncludedPrints,PrintedCopies,AdditionalPrintCopies,
  AdditionalPrintUnitPriceMinor,GrossRevenueMinor,Currency,OpenedAtUtc,CompletedAtUtc,UpdatedAtUtc)
 SELECT NEW.Id,NEW.EventId,NEW.BasePriceMinor,1,
  COALESCE(SUM(CASE WHEN p.Outcome='Success' THEN p.Copies ELSE 0 END),0),
  MAX(0,COALESCE(SUM(CASE WHEN p.Outcome='Success' THEN p.Copies ELSE 0 END),0)-1),
  NEW.AdditionalPrintPriceMinor,
  NEW.BasePriceMinor+MAX(0,COALESCE(SUM(CASE WHEN p.Outcome='Success' THEN p.Copies ELSE 0 END),0)-1)*NEW.AdditionalPrintPriceMinor,
  NEW.Currency,NEW.StartedAtUtc,NEW.CompletedAtUtc,NEW.UpdatedAtUtc
 FROM PrintJobs p WHERE p.BoothSessionId=NEW.Id;
END;";

        const string Schema=@"
CREATE TABLE IF NOT EXISTS SchemaInfo(Version INTEGER PRIMARY KEY CHECK(Version=3),AppliedAtUtc TEXT NOT NULL,AppBuild TEXT NOT NULL);
INSERT OR IGNORE INTO SchemaInfo VALUES(3,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'3.x');

CREATE TABLE IF NOT EXISTS DeviceIdentity(Id INTEGER PRIMARY KEY CHECK(Id=1),OrganizationId TEXT,DeviceId TEXT,LocationId TEXT,DeviceName TEXT,EnrollmentState TEXT NOT NULL DEFAULT 'Unenrolled' CHECK(EnrollmentState IN ('Unenrolled','Enrolled','Revoked','Suspended')),CredentialVersion INTEGER,LastAuthenticatedAtUtc TEXT,LastHeartbeatAtUtc TEXT,UpdatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS SecureArtifacts(ArtifactType TEXT PRIMARY KEY,ProtectedValue BLOB NOT NULL,CreatedAtUtc TEXT NOT NULL,ExpiresAtUtc TEXT,UpdatedAtUtc TEXT NOT NULL,CHECK(ArtifactType IN ('DeviceCredential','LicenseCache','AdminPin')));
CREATE TABLE IF NOT EXISTS PcAccounts(AccountId TEXT NOT NULL,Email TEXT PRIMARY KEY COLLATE NOCASE,DisplayName TEXT NOT NULL,OrganizationId TEXT,Role TEXT NOT NULL CHECK(Role IN ('LocalAdmin','BoothOperator')),PasswordHash TEXT NOT NULL,IsActive INTEGER NOT NULL DEFAULT 1 CHECK(IsActive IN (0,1)),IsDevelopmentAccount INTEGER NOT NULL DEFAULT 0 CHECK(IsDevelopmentAccount IN (0,1)),LastOnlineAuthenticationAtUtc TEXT,UpdatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS PcRememberedLogin(Id INTEGER PRIMARY KEY CHECK(Id=1),AccountId TEXT NOT NULL,OrganizationId TEXT NOT NULL,ProtectedToken BLOB NOT NULL,ExpiresAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS WaitingScreenProfiles(Orientation INTEGER PRIMARY KEY CHECK(Orientation IN (0,1)),BackgroundAssetId TEXT,ShowLiveView INTEGER NOT NULL,LiveViewX REAL NOT NULL,LiveViewY REAL NOT NULL,LiveViewAreaPercent REAL NOT NULL,BackgroundZoom REAL NOT NULL,BackgroundPanX REAL NOT NULL,BackgroundPanY REAL NOT NULL);
CREATE TABLE IF NOT EXISTS WorkflowSettings(Id INTEGER PRIMARY KEY CHECK(Id=1),Culture TEXT,CaptureDirectory TEXT,AlphaThreshold INTEGER,MinimumSlotArea INTEGER,MinimumSlotWidth INTEGER,MinimumSlotHeight INTEGER,IgnoreBorder INTEGER,MaximumSlots INTEGER,PhotoCount INTEGER,CountdownSeconds INTEGER,DefaultFrameId TEXT,DefaultPresetId TEXT,DefaultPrinterProfileId TEXT,KioskMode INTEGER,KeepFinal INTEGER,DelayBetweenShots INTEGER NOT NULL DEFAULT 1,AutoFlip INTEGER NOT NULL DEFAULT 0,SaveLocation INTEGER NOT NULL DEFAULT 0,GifFrameDurationMs INTEGER NOT NULL DEFAULT 800,WaitingTimeoutSeconds INTEGER NOT NULL DEFAULT 60,ShowWaitingLiveView INTEGER NOT NULL DEFAULT 1,WaitingLiveViewX REAL NOT NULL DEFAULT 10,WaitingLiveViewY REAL NOT NULL DEFAULT 10,RotateLiveView180 INTEGER NOT NULL DEFAULT 0,CustomerLayoutMode INTEGER NOT NULL DEFAULT 0,ImageRotationDegrees INTEGER NOT NULL DEFAULT 0,WaitingBackgroundZoom REAL NOT NULL DEFAULT 100,WaitingBackgroundPanX REAL NOT NULL DEFAULT 0,WaitingBackgroundPanY REAL NOT NULL DEFAULT 0,WaitingLiveViewAreaPercent REAL NOT NULL DEFAULT 5,PreferredCameraId TEXT);
CREATE TABLE IF NOT EXISTS ProductionSettings(Id INTEGER PRIMARY KEY CHECK(Id=1),SessionRetentionDays INTEGER,TempRetentionHours INTEGER,PrintRetryCount INTEGER,CameraReconnectSeconds INTEGER,EnableQr INTEGER,EnablePlugins INTEGER,EnableDiagnostics INTEGER,EnableTelemetry INTEGER,AutoStart INTEGER,Theme TEXT,LogLevel TEXT,AdminPasswordHash TEXT,LocalShareEnabled INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS BeautySettings(Id INTEGER PRIMARY KEY CHECK(Id=1),Enabled INTEGER NOT NULL DEFAULT 0 CHECK(Enabled IN (0,1)),SmoothSkin INTEGER NOT NULL DEFAULT 0 CHECK(SmoothSkin BETWEEN 0 AND 100),BrightenSkin INTEGER NOT NULL DEFAULT 0 CHECK(BrightenSkin BETWEEN 0 AND 100),SkinTone INTEGER NOT NULL DEFAULT 0 CHECK(SkinTone BETWEEN 0 AND 100),Sharpen INTEGER NOT NULL DEFAULT 0 CHECK(Sharpen BETWEEN 0 AND 100),EyeSize INTEGER NOT NULL DEFAULT 0 CHECK(EyeSize BETWEEN 0 AND 100),SlimFace INTEGER NOT NULL DEFAULT 0 CHECK(SlimFace BETWEEN 0 AND 100),BlemishRemoval INTEGER NOT NULL DEFAULT 0 CHECK(BlemishRemoval BETWEEN 0 AND 100),DarkCircleReduction INTEGER NOT NULL DEFAULT 0 CHECK(DarkCircleReduction BETWEEN 0 AND 100),ModifiedAtUtc TEXT);
INSERT OR IGNORE INTO BeautySettings(Id) VALUES(1);

CREATE TABLE IF NOT EXISTS FrameEvents(Id TEXT PRIMARY KEY,Name TEXT NOT NULL COLLATE NOCASE UNIQUE,CreatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS PresetEvents(Id TEXT PRIMARY KEY,Name TEXT NOT NULL COLLATE NOCASE UNIQUE,CreatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Frames(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,SourcePath TEXT,PixelWidth INTEGER NOT NULL,PixelHeight INTEGER NOT NULL,Slots TEXT,ThumbnailPath TEXT,IsPinned INTEGER NOT NULL DEFAULT 0 CHECK(IsPinned IN (0,1)),CreatedAtUtc TEXT NOT NULL,EventId TEXT REFERENCES FrameEvents(Id) ON DELETE SET NULL);
CREATE TABLE IF NOT EXISTS FrameSlots(Id TEXT PRIMARY KEY,FrameId TEXT NOT NULL,SlotIndex INTEGER NOT NULL,X INTEGER NOT NULL,Y INTEGER NOT NULL,Width INTEGER NOT NULL,Height INTEGER NOT NULL,FOREIGN KEY(FrameId) REFERENCES Frames(Id) ON DELETE CASCADE,UNIQUE(FrameId,SlotIndex));
CREATE TABLE IF NOT EXISTS InterfaceAssets(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,FilePath TEXT NOT NULL UNIQUE,IsAnimated INTEGER NOT NULL DEFAULT 0 CHECK(IsAnimated IN (0,1)),IsSelected INTEGER NOT NULL DEFAULT 0 CHECK(IsSelected IN (0,1)),CreatedAtUtc TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS UX_InterfaceAssets_OneSelected ON InterfaceAssets(IsSelected) WHERE IsSelected=1;
CREATE TABLE IF NOT EXISTS PrinterProfiles(Id TEXT PRIMARY KEY,Name TEXT,PrinterName TEXT,PrinterId TEXT,PaperSize TEXT,PaperType TEXT,Quality TEXT,UseDefaultBorder INTEGER NOT NULL DEFAULT 0,DefaultCopies INTEGER NOT NULL DEFAULT 1,Landscape INTEGER NOT NULL DEFAULT 0,IsDefault INTEGER NOT NULL DEFAULT 0,PrintInColor INTEGER NOT NULL DEFAULT 1);
CREATE UNIQUE INDEX IF NOT EXISTS UX_PrinterProfiles_PrinterId ON PrinterProfiles(PrinterId) WHERE PrinterId IS NOT NULL AND PrinterId<>'';
CREATE UNIQUE INDEX IF NOT EXISTS UX_PrinterProfiles_OneDefault ON PrinterProfiles(IsDefault) WHERE IsDefault=1;

CREATE TABLE IF NOT EXISTS ColorLutAssets(Id TEXT PRIMARY KEY,DisplayName TEXT NOT NULL CHECK(length(trim(DisplayName))>0),RelativePath TEXT NOT NULL,ContentHashSha256 TEXT NOT NULL CHECK(length(ContentHashSha256)=64),FileLength INTEGER NOT NULL CHECK(FileLength>0),LutKind TEXT NOT NULL DEFAULT 'Cube3D' CHECK(LutKind='Cube3D'),CubeSize INTEGER NOT NULL CHECK(CubeSize BETWEEN 2 AND 128),DomainMinR REAL NOT NULL,DomainMinG REAL NOT NULL,DomainMinB REAL NOT NULL,DomainMaxR REAL NOT NULL,DomainMaxG REAL NOT NULL,DomainMaxB REAL NOT NULL,LiveInterpolation TEXT NOT NULL DEFAULT 'Trilinear' CHECK(LiveInterpolation='Trilinear'),CaptureInterpolation TEXT NOT NULL DEFAULT 'Tetrahedral' CHECK(CaptureInterpolation='Tetrahedral'),Status TEXT NOT NULL DEFAULT 'Ready' CHECK(Status IN ('Staging','Ready','Missing','Corrupt','PendingDelete')),ValidationVersion INTEGER NOT NULL DEFAULT 1 CHECK(ValidationVersion>=1),LastValidatedAtUtc TEXT NOT NULL,CreatedAtUtc TEXT NOT NULL,ModifiedAtUtc TEXT NOT NULL,RowVersion INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion>=1),CHECK(DomainMinR<DomainMaxR),CHECK(DomainMinG<DomainMaxG),CHECK(DomainMinB<DomainMaxB));
CREATE UNIQUE INDEX IF NOT EXISTS UX_ColorLutAssets_RelativePath ON ColorLutAssets(RelativePath);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ColorLutAssets_ContentHash ON ColorLutAssets(ContentHashSha256);
CREATE INDEX IF NOT EXISTS IX_ColorLutAssets_Status ON ColorLutAssets(Status);
CREATE TABLE IF NOT EXISTS AdminPresets(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,SettingsJson TEXT,FrameId TEXT,PrinterProfileId TEXT,Countdown INTEGER,CreatedAtUtc TEXT,ModifiedAtUtc TEXT,IsDefault INTEGER NOT NULL DEFAULT 0,IsPinned INTEGER NOT NULL DEFAULT 0,EventId TEXT REFERENCES PresetEvents(Id) ON DELETE SET NULL);
CREATE TABLE IF NOT EXISTS PresetColorSettings(PresetId TEXT PRIMARY KEY,LutAssetId TEXT NOT NULL,ModifiedAtUtc TEXT NOT NULL,RowVersion INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion>=1),FOREIGN KEY(PresetId) REFERENCES AdminPresets(Id) ON DELETE CASCADE,FOREIGN KEY(LutAssetId) REFERENCES ColorLutAssets(Id) ON DELETE RESTRICT);
CREATE INDEX IF NOT EXISTS IX_PresetColorSettings_LutAssetId ON PresetColorSettings(LutAssetId);

CREATE TABLE IF NOT EXISTS Events(Id TEXT PRIMARY KEY,RemoteEventId TEXT,Name TEXT NOT NULL,PresetId TEXT REFERENCES AdminPresets(Id) ON DELETE SET NULL,OutputDirectory TEXT NOT NULL,Status TEXT NOT NULL DEFAULT 'Active' CHECK(Status IN ('Active','Archived')),IsDefault INTEGER NOT NULL DEFAULT 0 CHECK(IsDefault IN (0,1)),SessionNumber INTEGER NOT NULL DEFAULT 0,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,RowVersion INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion>=1));
CREATE UNIQUE INDEX IF NOT EXISTS UX_Events_OneDefault ON Events(IsDefault) WHERE IsDefault=1;
CREATE INDEX IF NOT EXISTS IX_Events_Name ON Events(Name COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS EventConfigurations(EventId TEXT PRIMARY KEY,BasePriceMinor INTEGER NOT NULL DEFAULT 0 CHECK(BasePriceMinor>=0),AdditionalPrintPriceMinor INTEGER NOT NULL DEFAULT 0 CHECK(AdditionalPrintPriceMinor>=0),Currency TEXT NOT NULL DEFAULT 'VND',PhotoCount INTEGER NOT NULL CHECK(PhotoCount BETWEEN 1 AND 8),CountdownSeconds INTEGER NOT NULL CHECK(CountdownSeconds BETWEEN 1 AND 10),GifFrameDurationMs INTEGER NOT NULL CHECK(GifFrameDurationMs BETWEEN 400 AND 1000),WaitingTimeoutSeconds INTEGER NOT NULL CHECK(WaitingTimeoutSeconds IN (30,60,120,300,600,900)),CustomerLayoutMode INTEGER NOT NULL CHECK(CustomerLayoutMode IN (0,1)),ImageRotationDegrees INTEGER NOT NULL CHECK(ImageRotationDegrees IN (-90,0,90,180)),BeautyEnabled INTEGER NOT NULL CHECK(BeautyEnabled IN (0,1)),SmoothSkin INTEGER NOT NULL CHECK(SmoothSkin BETWEEN 0 AND 100),BrightenSkin INTEGER NOT NULL CHECK(BrightenSkin BETWEEN 0 AND 100),SkinTone INTEGER NOT NULL CHECK(SkinTone BETWEEN 0 AND 100),Sharpen INTEGER NOT NULL CHECK(Sharpen BETWEEN 0 AND 100),EyeSize INTEGER NOT NULL CHECK(EyeSize BETWEEN 0 AND 100),SlimFace INTEGER NOT NULL CHECK(SlimFace BETWEEN 0 AND 100),BlemishRemoval INTEGER NOT NULL DEFAULT 0 CHECK(BlemishRemoval BETWEEN 0 AND 100),DarkCircleReduction INTEGER NOT NULL DEFAULT 0 CHECK(DarkCircleReduction BETWEEN 0 AND 100),ModifiedAtUtc TEXT NOT NULL,RowVersion INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion>=1),FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS EventFrames(EventId TEXT NOT NULL,FrameId TEXT NOT NULL,SortOrder INTEGER NOT NULL CHECK(SortOrder>=0),PRIMARY KEY(EventId,FrameId),UNIQUE(EventId,SortOrder),FOREIGN KEY(EventId) REFERENCES EventConfigurations(EventId) ON DELETE CASCADE,FOREIGN KEY(FrameId) REFERENCES Frames(Id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS EventPresets(EventId TEXT NOT NULL,PresetId TEXT NOT NULL,SortOrder INTEGER NOT NULL CHECK(SortOrder>=0),PRIMARY KEY(EventId,PresetId),UNIQUE(EventId,SortOrder),FOREIGN KEY(EventId) REFERENCES EventConfigurations(EventId) ON DELETE CASCADE,FOREIGN KEY(PresetId) REFERENCES AdminPresets(Id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS ConfigurationVersions(Id TEXT PRIMARY KEY,EventId TEXT NOT NULL,RemoteVersionId TEXT,VersionNumber INTEGER NOT NULL,ConfigurationJson TEXT NOT NULL,ContentHashSha256 TEXT NOT NULL,State TEXT NOT NULL CHECK(State IN ('Draft','Ready','Active','Retired','Invalid')),CreatedAtUtc TEXT NOT NULL,ActivatedAtUtc TEXT,FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE CASCADE,UNIQUE(EventId,VersionNumber),UNIQUE(EventId,ContentHashSha256));
CREATE UNIQUE INDEX IF NOT EXISTS UX_ConfigurationVersions_Active ON ConfigurationVersions(EventId) WHERE State='Active';
CREATE TABLE IF NOT EXISTS DeviceConfigAssignments(Id TEXT PRIMARY KEY,ConfigurationVersionId TEXT NOT NULL,RemoteAssignmentId TEXT,State TEXT NOT NULL CHECK(State IN ('Received','Applied','Failed','Superseded')),ReceivedAtUtc TEXT NOT NULL,AppliedAtUtc TEXT,LastError TEXT,FOREIGN KEY(ConfigurationVersionId) REFERENCES ConfigurationVersions(Id) ON DELETE RESTRICT);

CREATE TABLE IF NOT EXISTS BoothSessions(Id TEXT PRIMARY KEY,EventId TEXT NOT NULL,ConfigurationVersionId TEXT NOT NULL,OriginDeviceId TEXT NOT NULL,PresetId TEXT REFERENCES AdminPresets(Id) ON DELETE SET NULL,DisplayCode TEXT,BasePriceMinor INTEGER NOT NULL DEFAULT 0 CHECK(BasePriceMinor>=0),AdditionalPrintPriceMinor INTEGER NOT NULL DEFAULT 0 CHECK(AdditionalPrintPriceMinor>=0),Currency TEXT NOT NULL DEFAULT 'VND',State TEXT NOT NULL CHECK(State IN ('Created','Active','Finalizing','Completed','Abandoned','Failed')),StateVersion INTEGER NOT NULL DEFAULT 1 CHECK(StateVersion>=1),CaptureIndex INTEGER NOT NULL DEFAULT 0,FrameIndex INTEGER NOT NULL DEFAULT 0,StartedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,CompletedAtUtc TEXT,TerminalReason TEXT,StorageRootRelativePath TEXT NOT NULL,FinalImageAssetId TEXT REFERENCES MediaAssets(Id) ON DELETE SET NULL,FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE RESTRICT,FOREIGN KEY(ConfigurationVersionId) REFERENCES ConfigurationVersions(Id) ON DELETE RESTRICT,CHECK((State IN ('Completed','Abandoned','Failed') AND CompletedAtUtc IS NOT NULL) OR (State NOT IN ('Completed','Abandoned','Failed') AND CompletedAtUtc IS NULL)));
CREATE INDEX IF NOT EXISTS IX_BoothSessions_Event_Started ON BoothSessions(EventId,StartedAtUtc DESC);
CREATE INDEX IF NOT EXISTS IX_BoothSessions_State ON BoothSessions(State,StartedAtUtc);
CREATE TABLE IF NOT EXISTS CaptureAttempts(Id TEXT PRIMARY KEY,BoothSessionId TEXT NOT NULL,Sequence INTEGER NOT NULL,AttemptNumber INTEGER NOT NULL DEFAULT 1 CHECK(AttemptNumber>=1),CameraId TEXT,State TEXT NOT NULL CHECK(State IN ('IntentRecorded','Accepted','Failed','Unknown')),IsSelected INTEGER NOT NULL DEFAULT 0 CHECK(IsSelected IN (0,1)),IntentAtUtc TEXT NOT NULL,CompletedAtUtc TEXT,LastError TEXT,FOREIGN KEY(BoothSessionId) REFERENCES BoothSessions(Id) ON DELETE CASCADE,UNIQUE(BoothSessionId,Sequence,AttemptNumber));
CREATE INDEX IF NOT EXISTS IX_CaptureAttempts_Incomplete ON CaptureAttempts(State,IntentAtUtc);
CREATE UNIQUE INDEX IF NOT EXISTS UX_CaptureAttempts_SelectedSequence ON CaptureAttempts(BoothSessionId,Sequence) WHERE IsSelected=1;
CREATE TABLE IF NOT EXISTS MediaAssets(Id TEXT PRIMARY KEY,BoothSessionId TEXT NOT NULL,CaptureAttemptId TEXT,Kind TEXT NOT NULL CHECK(Kind IN ('OriginalPicture','OriginalVideo','FinalComposite','FinalVideo','Gif','ShareArchive')),RelativePath TEXT NOT NULL,MimeType TEXT NOT NULL,FileLength INTEGER NOT NULL CHECK(FileLength>=0),ContentHashSha256 TEXT,State TEXT NOT NULL CHECK(State IN ('Staging','Ready','Missing','Corrupt','PendingDelete','Deleted')),RetentionClass TEXT NOT NULL CHECK(RetentionClass IN ('Work','Original','Deliverable')),CloudProviderReference TEXT,UploadAttempts INTEGER NOT NULL DEFAULT 0,UploadedAtUtc TEXT,LastError TEXT,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,FOREIGN KEY(BoothSessionId) REFERENCES BoothSessions(Id) ON DELETE RESTRICT,FOREIGN KEY(CaptureAttemptId) REFERENCES CaptureAttempts(Id) ON DELETE SET NULL,UNIQUE(BoothSessionId,RelativePath));
CREATE INDEX IF NOT EXISTS IX_MediaAssets_Session_Kind ON MediaAssets(BoothSessionId,Kind,State);
CREATE INDEX IF NOT EXISTS IX_MediaAssets_Retention ON MediaAssets(RetentionClass,State,UpdatedAtUtc);
CREATE TABLE IF NOT EXISTS AssetLineage(DerivedAssetId TEXT NOT NULL,SourceAssetId TEXT NOT NULL,Purpose TEXT NOT NULL,PRIMARY KEY(DerivedAssetId,SourceAssetId),FOREIGN KEY(DerivedAssetId) REFERENCES MediaAssets(Id) ON DELETE CASCADE,FOREIGN KEY(SourceAssetId) REFERENCES MediaAssets(Id) ON DELETE RESTRICT,CHECK(DerivedAssetId<>SourceAssetId));

CREATE TABLE IF NOT EXISTS Deliverables(Id TEXT PRIMARY KEY,BoothSessionId TEXT NOT NULL,Revision INTEGER NOT NULL DEFAULT 1,FrameId TEXT REFERENCES Frames(Id) ON DELETE SET NULL,FinalCompositeAssetId TEXT,FinalCompositePath TEXT NOT NULL,LocalSharePath TEXT,MediaMode TEXT NOT NULL DEFAULT 'PictureOnly' CHECK(MediaMode IN ('PictureOnly','PictureAndVideo')),State TEXT NOT NULL CHECK(State IN ('Building','Ready','QueuedForUpload','Uploading','RetryWaiting','Uploaded','GalleryReady','Expired','Failed')),UploadAttempts INTEGER NOT NULL DEFAULT 0,CreatedAtUtc TEXT NOT NULL,ReadyAtUtc TEXT,UploadedAtUtc TEXT,GalleryReadyAtUtc TEXT,ExpiresAtUtc TEXT,LastError TEXT,FOREIGN KEY(BoothSessionId) REFERENCES BoothSessions(Id) ON DELETE RESTRICT,FOREIGN KEY(FinalCompositeAssetId) REFERENCES MediaAssets(Id) ON DELETE RESTRICT,UNIQUE(BoothSessionId,Revision));
CREATE TABLE IF NOT EXISTS DeliverableAssets(DeliverableId TEXT NOT NULL,MediaAssetId TEXT NOT NULL,Role TEXT NOT NULL CHECK(Role IN ('Picture','Video','Composite','CompositeVideo','Gif','ShareArchive')),Position INTEGER NOT NULL CHECK(Position>=0),PRIMARY KEY(DeliverableId,MediaAssetId),UNIQUE(DeliverableId,Role,Position),FOREIGN KEY(DeliverableId) REFERENCES Deliverables(Id) ON DELETE CASCADE,FOREIGN KEY(MediaAssetId) REFERENCES MediaAssets(Id) ON DELETE RESTRICT);
CREATE INDEX IF NOT EXISTS IX_Deliverables_Session ON Deliverables(BoothSessionId,CreatedAtUtc DESC);
CREATE TABLE IF NOT EXISTS OutputJobs(Id TEXT PRIMARY KEY,BoothSessionId TEXT NOT NULL,DeliverableId TEXT,MediaAssetId TEXT,JobType TEXT NOT NULL CHECK(JobType IN ('Print','UploadAsset','CommitDeliverable','CreateGallery')),IdempotencyKey TEXT NOT NULL UNIQUE,State TEXT NOT NULL CHECK(State IN ('Pending','Leased','Submitting','Submitted','Completed','RetryWaiting','UnknownOutcome','PermanentFailure','Cancelled')),AttemptCount INTEGER NOT NULL DEFAULT 0,NextRetryAtUtc TEXT,LeaseId TEXT,LeaseExpiresAtUtc TEXT,ExternalReference TEXT,LastError TEXT,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,FOREIGN KEY(BoothSessionId) REFERENCES BoothSessions(Id) ON DELETE RESTRICT,FOREIGN KEY(DeliverableId) REFERENCES Deliverables(Id) ON DELETE RESTRICT,FOREIGN KEY(MediaAssetId) REFERENCES MediaAssets(Id) ON DELETE RESTRICT,CHECK((JobType='Print' AND DeliverableId IS NOT NULL AND MediaAssetId IS NOT NULL) OR JobType<>'Print'));
CREATE INDEX IF NOT EXISTS IX_OutputJobs_Due ON OutputJobs(State,NextRetryAtUtc,LeaseExpiresAtUtc);
CREATE INDEX IF NOT EXISTS IX_OutputJobs_Session ON OutputJobs(BoothSessionId,State);
CREATE TABLE IF NOT EXISTS PrintJobs(Id TEXT PRIMARY KEY,OutputJobId TEXT NOT NULL UNIQUE,BoothSessionId TEXT NOT NULL,DeliverableId TEXT NOT NULL,PrinterProfileId TEXT,PrinterNameSnapshot TEXT NOT NULL,Copies INTEGER NOT NULL DEFAULT 1 CHECK(Copies>=1),SettingsJson TEXT NOT NULL,SpoolerJobId TEXT,Outcome TEXT NOT NULL CHECK(Outcome IN ('Pending','Success','Failed','UnknownOutcome')),RequestedAtUtc TEXT NOT NULL,CompletedAtUtc TEXT,FOREIGN KEY(OutputJobId) REFERENCES OutputJobs(Id) ON DELETE RESTRICT,FOREIGN KEY(BoothSessionId) REFERENCES BoothSessions(Id) ON DELETE RESTRICT,FOREIGN KEY(DeliverableId) REFERENCES Deliverables(Id) ON DELETE RESTRICT,FOREIGN KEY(PrinterProfileId) REFERENCES PrinterProfiles(Id) ON DELETE SET NULL);
CREATE INDEX IF NOT EXISTS IX_PrintJobs_Session ON PrintJobs(BoothSessionId,RequestedAtUtc DESC);
CREATE TABLE IF NOT EXISTS SyncOutbox(Id TEXT PRIMARY KEY,AggregateType TEXT NOT NULL,AggregateId TEXT NOT NULL,EventType TEXT NOT NULL,PayloadJson TEXT NOT NULL,IdempotencyKey TEXT NOT NULL UNIQUE,State TEXT NOT NULL DEFAULT 'Pending' CHECK(State IN ('Pending','Leased','Sent','RetryWaiting','PermanentFailure')),AttemptCount INTEGER NOT NULL DEFAULT 0,NextRetryAtUtc TEXT,LeaseId TEXT,LeaseExpiresAtUtc TEXT,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS IX_SyncOutbox_Due ON SyncOutbox(State,NextRetryAtUtc,LeaseExpiresAtUtc);
CREATE TABLE IF NOT EXISTS RemoteCommandExecutions(CommandId TEXT PRIMARY KEY,CommandType TEXT NOT NULL,PayloadJson TEXT NOT NULL,RequiredIdleState INTEGER NOT NULL CHECK(RequiredIdleState IN (0,1)),ExpiresAtUtc TEXT NOT NULL,State TEXT NOT NULL CHECK(State IN ('Received','Deferred','Running','Succeeded','Failed','Rejected','Expired','UnknownOutcome')),ReceivedAtUtc TEXT NOT NULL,StartedAtUtc TEXT,CompletedAtUtc TEXT,ResultJson TEXT,LastError TEXT);
CREATE TABLE IF NOT EXISTS GalleryLinkCache(DeliverableId TEXT PRIMARY KEY,PublicUrl TEXT NOT NULL,ExpiresAtUtc TEXT NOT NULL,ReceivedAtUtc TEXT NOT NULL,FOREIGN KEY(DeliverableId) REFERENCES Deliverables(Id) ON DELETE CASCADE);

CREATE TRIGGER IF NOT EXISTS TR_AssetLineage_SameSession BEFORE INSERT ON AssetLineage BEGIN SELECT CASE WHEN (SELECT BoothSessionId FROM MediaAssets WHERE Id=NEW.DerivedAssetId)<>(SELECT BoothSessionId FROM MediaAssets WHERE Id=NEW.SourceAssetId) THEN RAISE(ABORT,'Asset lineage must stay inside one booth session') END;END;
CREATE TRIGGER IF NOT EXISTS TR_DeliverableAssets_SameSession BEFORE INSERT ON DeliverableAssets BEGIN SELECT CASE WHEN (SELECT BoothSessionId FROM Deliverables WHERE Id=NEW.DeliverableId)<>(SELECT BoothSessionId FROM MediaAssets WHERE Id=NEW.MediaAssetId) THEN RAISE(ABORT,'Deliverable asset must belong to the same booth session') END;END;
CREATE TRIGGER IF NOT EXISTS TR_DeliverableAssets_SameSession_Update BEFORE UPDATE ON DeliverableAssets BEGIN SELECT CASE WHEN (SELECT BoothSessionId FROM Deliverables WHERE Id=NEW.DeliverableId)<>(SELECT BoothSessionId FROM MediaAssets WHERE Id=NEW.MediaAssetId) THEN RAISE(ABORT,'Deliverable asset must belong to the same booth session') END;END;
";
    }
}
