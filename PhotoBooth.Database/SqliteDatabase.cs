using System;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace PhotoBooth.Database
{
    public sealed class SqliteDatabase
    {
        private readonly string _connectionString;

        public SqliteDatabase(string databasePath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA foreign_keys=ON"; command.ExecuteNonQuery(); }
            return connection;
        }

        public void Initialize()
        {
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Presets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, SettingsJson TEXT);
CREATE TABLE IF NOT EXISTS AdminPresets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, SettingsJson TEXT, FrameId TEXT, PrinterProfileId TEXT, Countdown INTEGER, CreatedAtUtc TEXT, ModifiedAtUtc TEXT, IsDefault INTEGER);
CREATE TABLE IF NOT EXISTS PresetProcessingSettings (PresetId TEXT PRIMARY KEY, Brightness REAL NOT NULL, Contrast REAL NOT NULL, Saturation REAL NOT NULL, Gamma REAL NOT NULL, Exposure REAL NOT NULL, Temperature REAL NOT NULL, Tint REAL NOT NULL, Sharpen REAL NOT NULL, Blur REAL NOT NULL, Vignette REAL NOT NULL, BlackAndWhite INTEGER NOT NULL, Sepia INTEGER NOT NULL, WatermarkPath TEXT, WatermarkOpacity REAL NOT NULL, OutputWidth INTEGER NOT NULL, OutputHeight INTEGER NOT NULL, Dpi INTEGER NOT NULL, FOREIGN KEY(PresetId) REFERENCES AdminPresets(Id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS Sessions (Id TEXT PRIMARY KEY, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT, OutputDirectory TEXT);
CREATE TABLE IF NOT EXISTS CustomerSessions (Id TEXT PRIMARY KEY, PresetId TEXT, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT, OutputDirectory TEXT, CapturedFiles TEXT, FinalImagePath TEXT);
CREATE TABLE IF NOT EXISTS CapturedImages (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, Sequence INTEGER NOT NULL, FilePath TEXT NOT NULL, CapturedAtUtc TEXT NOT NULL, FOREIGN KEY(SessionId) REFERENCES CustomerSessions(Id) ON DELETE CASCADE, UNIQUE(SessionId,Sequence), UNIQUE(SessionId,FilePath));
CREATE TABLE IF NOT EXISTS Captures (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, FrameId TEXT, CompositeImageId TEXT, CompositePath TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'Pending', UploadAttempts INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TEXT NOT NULL, UploadedAtUtc TEXT, ExpiresAtUtc TEXT, LastError TEXT, FOREIGN KEY(SessionId) REFERENCES CustomerSessions(Id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS CapturePhotos (Id TEXT PRIMARY KEY, CaptureId TEXT NOT NULL, CapturedImageId TEXT, LocalPath TEXT NOT NULL, PhotoType TEXT NOT NULL, Position INTEGER NOT NULL, CloudinaryPublicId TEXT, IsUploaded INTEGER NOT NULL DEFAULT 0 CHECK(IsUploaded IN (0,1)), UploadAttempts INTEGER NOT NULL DEFAULT 0, UploadedAtUtc TEXT, LastError TEXT, FOREIGN KEY(CaptureId) REFERENCES Captures(Id) ON DELETE CASCADE, FOREIGN KEY(CapturedImageId) REFERENCES CapturedImages(Id) ON DELETE SET NULL, UNIQUE(CaptureId,PhotoType,Position), UNIQUE(CaptureId,LocalPath));
CREATE TABLE IF NOT EXISTS Frames (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, SourcePath TEXT, PixelWidth INTEGER, PixelHeight INTEGER, Slots TEXT, ThumbnailPath TEXT, IsPinned INTEGER, CreatedAtUtc TEXT);
CREATE TABLE IF NOT EXISTS FrameEvents (Id TEXT PRIMARY KEY, Name TEXT NOT NULL COLLATE NOCASE UNIQUE, CreatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS InterfaceAssets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, FilePath TEXT NOT NULL UNIQUE, IsAnimated INTEGER NOT NULL DEFAULT 0, IsSelected INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS FrameSlots (Id TEXT PRIMARY KEY, FrameId TEXT NOT NULL, SlotIndex INTEGER NOT NULL, X INTEGER NOT NULL, Y INTEGER NOT NULL, Width INTEGER NOT NULL, Height INTEGER NOT NULL, FOREIGN KEY(FrameId) REFERENCES Frames(Id) ON DELETE CASCADE, UNIQUE(FrameId,SlotIndex));
CREATE TABLE IF NOT EXISTS PrinterProfiles (Id TEXT PRIMARY KEY, Name TEXT, PrinterName TEXT, PaperSize TEXT, PaperType TEXT, Quality TEXT, UseDefaultBorder INTEGER, DefaultCopies INTEGER, Landscape INTEGER);
CREATE TABLE IF NOT EXISTS AppSettings (Id INTEGER PRIMARY KEY CHECK(Id=1), Culture TEXT, CaptureDirectory TEXT, AlphaThreshold INTEGER, MinimumSlotArea INTEGER, MinimumSlotWidth INTEGER, MinimumSlotHeight INTEGER, IgnoreBorder INTEGER, MaximumSlots INTEGER, PhotoCount INTEGER, CountdownSeconds INTEGER);
CREATE TABLE IF NOT EXISTS WorkflowSettings (Id INTEGER PRIMARY KEY CHECK(Id=1), Culture TEXT, CaptureDirectory TEXT, AlphaThreshold INTEGER, MinimumSlotArea INTEGER, MinimumSlotWidth INTEGER, MinimumSlotHeight INTEGER, IgnoreBorder INTEGER, MaximumSlots INTEGER, PhotoCount INTEGER, CountdownSeconds INTEGER, DefaultFrameId TEXT, DefaultPresetId TEXT, DefaultPrinterProfileId TEXT, KioskMode INTEGER, KeepFinal INTEGER, DelayBetweenShots INTEGER NOT NULL DEFAULT 1, AutoFlip INTEGER NOT NULL DEFAULT 0, SaveLocation INTEGER NOT NULL DEFAULT 0);";
                command.CommandText += @"
CREATE TABLE IF NOT EXISTS ProductionSettings (Id INTEGER PRIMARY KEY CHECK(Id=1), SessionRetentionDays INTEGER, TempRetentionHours INTEGER, PrintRetryCount INTEGER, CameraReconnectSeconds INTEGER, EnableQr INTEGER, EnablePlugins INTEGER, EnableDiagnostics INTEGER, EnableTelemetry INTEGER, AutoStart INTEGER, Theme TEXT, LogLevel TEXT, AdminPasswordHash TEXT);
CREATE TABLE IF NOT EXISTS LocalIdentity (Id INTEGER PRIMARY KEY CHECK(Id=1), AccountId TEXT, DeviceId TEXT, Email TEXT, LastOnlineAuthenticationAtUtc TEXT, UpdatedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS LocalSecureArtifacts (ArtifactType TEXT PRIMARY KEY NOT NULL, ProtectedValue BLOB NOT NULL, CreatedAtUtc TEXT NOT NULL, ExpiresAtUtc TEXT, UpdatedAtUtc TEXT NOT NULL, CHECK(ArtifactType IN ('RefreshToken','DeviceCredential','LicenseCache','AdminPin')));
CREATE TABLE IF NOT EXISTS UploadQueue (Id TEXT PRIMARY KEY NOT NULL, AccountId TEXT NOT NULL, DeviceId TEXT NOT NULL, CaptureId TEXT NOT NULL, PhotoId TEXT NOT NULL, LocalPath TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'Pending' CHECK(Status IN ('Pending','Uploading','Uploaded','RetryWaiting','PermanentFailure')), AttemptCount INTEGER NOT NULL DEFAULT 0, NextRetryAtUtc TEXT, LeaseId TEXT, LeaseExpiresAtUtc TEXT, LastError TEXT, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, UNIQUE(CaptureId,PhotoId));
CREATE TABLE IF NOT EXISTS PrintJobs (Id TEXT PRIMARY KEY, SessionId TEXT, CaptureId TEXT, PrinterProfileId TEXT, PrinterName TEXT NOT NULL, Copies INTEGER NOT NULL DEFAULT 1 CHECK(Copies>=1), PaperSize TEXT, PaperType TEXT, Quality TEXT, Landscape INTEGER NOT NULL DEFAULT 0 CHECK(Landscape IN (0,1)), PrintInColor INTEGER NOT NULL DEFAULT 1 CHECK(PrintInColor IN (0,1)), UseDefaultBorder INTEGER NOT NULL DEFAULT 0 CHECK(UseDefaultBorder IN (0,1)), Status TEXT NOT NULL DEFAULT 'Success' CHECK(Status IN ('Success','Failed')), PrintedAtUtc TEXT NOT NULL, FOREIGN KEY(SessionId) REFERENCES CustomerSessions(Id) ON DELETE SET NULL, FOREIGN KEY(CaptureId) REFERENCES Captures(Id) ON DELETE SET NULL, FOREIGN KEY(PrinterProfileId) REFERENCES PrinterProfiles(Id) ON DELETE SET NULL);";
                command.CommandText += @"
CREATE TABLE IF NOT EXISTS ColorLutAssets (
 Id TEXT PRIMARY KEY NOT NULL,
 DisplayName TEXT NOT NULL CHECK(length(trim(DisplayName))>0),
 RelativePath TEXT NOT NULL,
 ContentHashSha256 TEXT NOT NULL CHECK(length(ContentHashSha256)=64),
 FileLength INTEGER NOT NULL CHECK(FileLength>0),
 LutKind TEXT NOT NULL DEFAULT 'Cube3D' CHECK(LutKind='Cube3D'),
 CubeSize INTEGER NOT NULL CHECK(CubeSize BETWEEN 2 AND 128),
 DomainMinR REAL NOT NULL,DomainMinG REAL NOT NULL,DomainMinB REAL NOT NULL,
 DomainMaxR REAL NOT NULL,DomainMaxG REAL NOT NULL,DomainMaxB REAL NOT NULL,
 LiveInterpolation TEXT NOT NULL DEFAULT 'Trilinear' CHECK(LiveInterpolation='Trilinear'),
 CaptureInterpolation TEXT NOT NULL DEFAULT 'Tetrahedral' CHECK(CaptureInterpolation='Tetrahedral'),
 Status TEXT NOT NULL DEFAULT 'Ready' CHECK(Status IN ('Staging','Ready','Missing','Corrupt','PendingDelete')),
 ValidationVersion INTEGER NOT NULL DEFAULT 1 CHECK(ValidationVersion>=1),
 LastValidatedAtUtc TEXT NOT NULL,CreatedAtUtc TEXT NOT NULL,ModifiedAtUtc TEXT NOT NULL,
 RowVersion INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion>=1),
 CHECK(DomainMinR<DomainMaxR),CHECK(DomainMinG<DomainMaxG),CHECK(DomainMinB<DomainMaxB)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ColorLutAssets_RelativePath ON ColorLutAssets(RelativePath);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ColorLutAssets_ContentHash ON ColorLutAssets(ContentHashSha256);
CREATE INDEX IF NOT EXISTS IX_ColorLutAssets_Status ON ColorLutAssets(Status);
CREATE TABLE IF NOT EXISTS PresetColorSettings (
 PresetId TEXT PRIMARY KEY NOT NULL,
 LutAssetId TEXT,
 Strength REAL NOT NULL DEFAULT 1.0 CHECK(Strength BETWEEN 0.0 AND 1.0),
 Enabled INTEGER NOT NULL DEFAULT 1 CHECK(Enabled IN (0,1)),
 ModifiedAtUtc TEXT NOT NULL,
 RowVersion INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion>=1),
 FOREIGN KEY(PresetId) REFERENCES AdminPresets(Id) ON DELETE CASCADE,
 FOREIGN KEY(LutAssetId) REFERENCES ColorLutAssets(Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_PresetColorSettings_LutAssetId ON PresetColorSettings(LutAssetId);";
                command.ExecuteNonQuery();
            }
            RecordMigration(1, "legacy_schema_baseline");
            RecordMigration(2, "local_identity_license_pin_upload_queue");
            RecordMigration(3, "color_lut_assets_and_preset_color_settings");
            DropColumnIfExists("ProductionSettings", "EnableUpload");
            EnsureColumn("WorkflowSettings", "DelayBetweenShots", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn("WorkflowSettings", "GifFrameDurationMs", "INTEGER NOT NULL DEFAULT 1000");
            EnsureColumn("WorkflowSettings", "WaitingTimeoutSeconds", "INTEGER NOT NULL DEFAULT 30");
            EnsureColumn("WorkflowSettings", "AutoFlip", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("WorkflowSettings", "ShowWaitingLiveView", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn("WorkflowSettings", "WaitingLiveViewX", "REAL NOT NULL DEFAULT 10");
            EnsureColumn("WorkflowSettings", "WaitingLiveViewY", "REAL NOT NULL DEFAULT 10");
            EnsureColumn("WorkflowSettings", "SaveLocation", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("Frames", "EventId", "TEXT");
            Execute("CREATE INDEX IF NOT EXISTS IX_Frames_EventId ON Frames(EventId);");
            EnsureColumn("CustomerSessions", "SessionName", "TEXT");
            EnsureColumn("CustomerSessions", "SessionNumber", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "CapturedImageIds", "TEXT");
            EnsureColumn("CustomerSessions", "IsDefault", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "CaptureIndex", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "FrameIndex", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "FinalImageId", "TEXT");
            EnsureColumn("PrinterProfiles", "IsDefault", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("PrinterProfiles", "PrinterId", "TEXT");
            EnsureColumn("PrinterProfiles", "PrintInColor", "INTEGER NOT NULL DEFAULT 1");
            Execute("UPDATE CustomerSessions SET CaptureIndex=MAX(CaptureIndex,(SELECT COALESCE(MAX(Sequence),0) FROM CapturedImages WHERE SessionId=CustomerSessions.Id));");
            // Remove only duplicate named sessions. Prefer the row that owns images,
            // otherwise retain the oldest row. Legacy unnamed rows are left intact.
            Execute(@"DELETE FROM CustomerSessions
WHERE SessionName IS NOT NULL AND SessionName <> ''
AND Id NOT IN (
 SELECT s.Id FROM CustomerSessions s
 WHERE s.SessionName IS NOT NULL AND s.SessionName <> ''
 AND s.Id = (
  SELECT winner.Id FROM CustomerSessions winner
  WHERE winner.SessionName=s.SessionName
  ORDER BY (SELECT COUNT(*) FROM CapturedImages i WHERE i.SessionId=winner.Id) DESC,
           winner.StartedAtUtc ASC, winner.Id ASC LIMIT 1
 )
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_CustomerSessions_SessionName ON CustomerSessions(SessionName) WHERE SessionName IS NOT NULL AND SessionName <> '';");
            Execute(@"UPDATE CustomerSessions SET IsDefault=0 WHERE IsDefault=1 AND Id<>(SELECT Id FROM CustomerSessions WHERE IsDefault=1 ORDER BY StartedAtUtc LIMIT 1);
UPDATE CustomerSessions SET IsDefault=1 WHERE Id=(SELECT Id FROM CustomerSessions ORDER BY CASE WHEN SessionName='Base_session' THEN 0 ELSE 1 END,StartedAtUtc LIMIT 1) AND NOT EXISTS(SELECT 1 FROM CustomerSessions WHERE IsDefault=1);
CREATE UNIQUE INDEX IF NOT EXISTS UX_CustomerSessions_OneDefault ON CustomerSessions(IsDefault) WHERE IsDefault=1;
UPDATE PrinterProfiles SET IsDefault=1 WHERE Id=(SELECT Id FROM PrinterProfiles ORDER BY rowid DESC LIMIT 1) AND NOT EXISTS(SELECT 1 FROM PrinterProfiles WHERE IsDefault=1);
CREATE UNIQUE INDEX IF NOT EXISTS UX_PrinterProfiles_OneDefault ON PrinterProfiles(IsDefault) WHERE IsDefault=1;");
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS UX_PrinterProfiles_PrinterId ON PrinterProfiles(PrinterId) WHERE PrinterId IS NOT NULL AND PrinterId <> ''; ");
            EnsureColumn("CustomerSessions", "AccountId", "TEXT");
            EnsureColumn("CustomerSessions", "DeviceId", "TEXT");
            EnsureColumn("Captures", "AccountId", "TEXT");
            EnsureColumn("CapturePhotos", "IsUploaded", "INTEGER NOT NULL DEFAULT 0 CHECK(IsUploaded IN (0,1))");
            EnsureColumn("CapturePhotos", "MimeType", "TEXT");
            EnsureColumn("CapturePhotos", "FileLength", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CapturePhotos", "ContentHashSha256", "TEXT");
            EnsureColumn("CapturePhotos", "CreatedAtUtc", "TEXT");
            EnsureColumn("CapturePhotos", "AssetStatus", "TEXT NOT NULL DEFAULT 'Ready'");
            EnsureColumn("Captures", "DeviceId", "TEXT");
            EnsureColumn("Captures", "LocalSharePath", "TEXT");
            EnsureColumn("ProductionSettings", "LocalShareEnabled", "INTEGER NOT NULL DEFAULT 1");
            Execute("CREATE TABLE IF NOT EXISTS InterfaceAssets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, FilePath TEXT NOT NULL UNIQUE, IsAnimated INTEGER NOT NULL DEFAULT 0, IsSelected INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS UX_InterfaceAssets_OneSelected ON InterfaceAssets(IsSelected) WHERE IsSelected=1;");
            Execute(@"CREATE TABLE IF NOT EXISTS CaptureAssetSources (
 AssetId TEXT NOT NULL,
 SourceAssetId TEXT NOT NULL,
 PRIMARY KEY(AssetId,SourceAssetId),
 FOREIGN KEY(AssetId) REFERENCES CapturePhotos(Id) ON DELETE CASCADE,
 FOREIGN KEY(SourceAssetId) REFERENCES CapturePhotos(Id) ON DELETE CASCADE,
 CHECK(AssetId<>SourceAssetId)
);
UPDATE CapturePhotos SET PhotoType='MotionPhoto' WHERE PhotoType='Original' AND lower(LocalPath) LIKE '%_mp.jpg';
UPDATE CapturePhotos SET PhotoType='Picture' WHERE PhotoType='Original';
UPDATE CapturePhotos SET MimeType=CASE PhotoType WHEN 'MotionPhoto' THEN 'image/jpeg' WHEN 'Composite' THEN 'image/png' WHEN 'Gif' THEN 'image/gif' WHEN 'ShareArchive' THEN 'application/zip' ELSE MimeType END WHERE MimeType IS NULL OR MimeType='';
UPDATE CapturePhotos SET CreatedAtUtc=(SELECT CreatedAtUtc FROM Captures WHERE Captures.Id=CapturePhotos.CaptureId) WHERE CreatedAtUtc IS NULL OR CreatedAtUtc='';
CREATE UNIQUE INDEX IF NOT EXISTS UX_Captures_CompositeImageId ON Captures(CompositeImageId) WHERE CompositeImageId IS NOT NULL AND CompositeImageId<>'';
CREATE UNIQUE INDEX IF NOT EXISTS UX_CustomerSessions_FinalImageId ON CustomerSessions(FinalImageId) WHERE FinalImageId IS NOT NULL AND FinalImageId<>'';
CREATE INDEX IF NOT EXISTS IX_CaptureAssetSources_Source ON CaptureAssetSources(SourceAssetId);
DROP TRIGGER IF EXISTS TR_CapturePhotos_Validate_Insert;
DROP TRIGGER IF EXISTS TR_CapturePhotos_Validate_Update;
DROP TRIGGER IF EXISTS TR_CaptureAssetSources_SameCapture;
CREATE TRIGGER TR_CapturePhotos_Validate_Insert BEFORE INSERT ON CapturePhotos BEGIN
 SELECT CASE WHEN NEW.Id IS NULL OR trim(NEW.Id)='' THEN RAISE(ABORT,'Capture asset ID is required') END;
 SELECT CASE WHEN NEW.PhotoType NOT IN ('Picture','MotionPhoto','Composite','Gif','ShareArchive') THEN RAISE(ABORT,'Invalid capture asset type') END;
 SELECT CASE WHEN NEW.PhotoType='MotionPhoto' AND (NEW.CapturedImageId IS NULL OR trim(NEW.CapturedImageId)='') THEN RAISE(ABORT,'Motion Photo requires a captured image ID') END;
 SELECT CASE WHEN NEW.FileLength<0 THEN RAISE(ABORT,'Invalid capture asset length') END;
 SELECT CASE WHEN NEW.CapturedImageId IS NOT NULL AND (SELECT SessionId FROM CapturedImages WHERE Id=NEW.CapturedImageId)<>(SELECT SessionId FROM Captures WHERE Id=NEW.CaptureId) THEN RAISE(ABORT,'Captured image and asset must belong to the same session') END;
END;
CREATE TRIGGER TR_CapturePhotos_Validate_Update BEFORE UPDATE ON CapturePhotos BEGIN
 SELECT CASE WHEN NEW.CaptureId<>OLD.CaptureId THEN RAISE(ABORT,'Capture asset ownership cannot change') END;
 SELECT CASE WHEN NEW.PhotoType NOT IN ('Picture','MotionPhoto','Composite','Gif','ShareArchive') THEN RAISE(ABORT,'Invalid capture asset type') END;
 SELECT CASE WHEN NEW.PhotoType='MotionPhoto' AND (NEW.CapturedImageId IS NULL OR trim(NEW.CapturedImageId)='') THEN RAISE(ABORT,'Motion Photo requires a captured image ID') END;
 SELECT CASE WHEN NEW.CapturedImageId IS NOT NULL AND (SELECT SessionId FROM CapturedImages WHERE Id=NEW.CapturedImageId)<>(SELECT SessionId FROM Captures WHERE Id=NEW.CaptureId) THEN RAISE(ABORT,'Captured image and asset must belong to the same session') END;
END;
CREATE TRIGGER TR_CaptureAssetSources_SameCapture BEFORE INSERT ON CaptureAssetSources BEGIN
 SELECT CASE WHEN (SELECT CaptureId FROM CapturePhotos WHERE Id=NEW.AssetId)<>(SELECT CaptureId FROM CapturePhotos WHERE Id=NEW.SourceAssetId) THEN RAISE(ABORT,'Asset source must belong to the same capture') END;
END;");
            Execute(@"DROP TRIGGER IF EXISTS TR_UploadQueue_Validate_Insert;
DROP TRIGGER IF EXISTS TR_UploadQueue_Validate_Update;
CREATE TRIGGER TR_UploadQueue_Validate_Insert BEFORE INSERT ON UploadQueue BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM Captures WHERE Id=NEW.CaptureId) THEN RAISE(ABORT,'Upload capture does not exist') END;
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM CapturePhotos WHERE Id=NEW.PhotoId AND CaptureId=NEW.CaptureId) THEN RAISE(ABORT,'Upload asset does not belong to capture') END;
END;
CREATE TRIGGER TR_UploadQueue_Validate_Update BEFORE UPDATE ON UploadQueue BEGIN
 SELECT CASE WHEN NEW.CaptureId<>OLD.CaptureId OR NEW.PhotoId<>OLD.PhotoId THEN RAISE(ABORT,'Upload ownership cannot change') END;
END;");
            Execute(@"INSERT OR IGNORE INTO CaptureAssetSources(AssetId,SourceAssetId)
SELECT derived.Id,source.Id FROM CapturePhotos derived JOIN CapturePhotos source ON source.CaptureId=derived.CaptureId
WHERE derived.PhotoType IN ('Composite','Gif') AND source.PhotoType IN ('Picture','MotionPhoto');
INSERT OR IGNORE INTO CaptureAssetSources(AssetId,SourceAssetId)
SELECT archive.Id,source.Id FROM CapturePhotos archive JOIN CapturePhotos source ON source.CaptureId=archive.CaptureId AND source.Id<>archive.Id
WHERE archive.PhotoType='ShareArchive';");
            BackfillCaptureAssetMetadata();
            RecordMigration(4, "capture_asset_identity_metadata_and_lineage");
            RecordMigration(5, "frame_event_collections");
            Execute("CREATE INDEX IF NOT EXISTS IX_CapturedImages_SessionId ON CapturedImages(SessionId); CREATE INDEX IF NOT EXISTS IX_FrameSlots_FrameId ON FrameSlots(FrameId); CREATE INDEX IF NOT EXISTS IX_CustomerSessions_StartedAtUtc ON CustomerSessions(StartedAtUtc); CREATE INDEX IF NOT EXISTS IX_CustomerSessions_Account_Device ON CustomerSessions(AccountId,DeviceId,StartedAtUtc); CREATE INDEX IF NOT EXISTS IX_Captures_SessionId ON Captures(SessionId); CREATE INDEX IF NOT EXISTS IX_Captures_Account_Device ON Captures(AccountId,DeviceId,CreatedAtUtc); CREATE INDEX IF NOT EXISTS IX_Captures_Status_ExpiresAtUtc ON Captures(Status,ExpiresAtUtc); CREATE INDEX IF NOT EXISTS IX_CapturePhotos_CaptureId ON CapturePhotos(CaptureId); CREATE INDEX IF NOT EXISTS IX_CapturePhotos_IsUploaded ON CapturePhotos(IsUploaded); CREATE INDEX IF NOT EXISTS IX_UploadQueue_Due ON UploadQueue(Status,NextRetryAtUtc); CREATE INDEX IF NOT EXISTS IX_PrintJobs_PrintedAtUtc ON PrintJobs(PrintedAtUtc); CREATE INDEX IF NOT EXISTS IX_PrintJobs_Status ON PrintJobs(Status); CREATE INDEX IF NOT EXISTS IX_PrintJobs_SessionId ON PrintJobs(SessionId);");
        }

        private void RecordMigration(int version, string name)
        {
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT OR IGNORE INTO SchemaMigrations(Version,Name,AppliedAtUtc) VALUES($version,$name,$applied)";
                command.Parameters.AddWithValue("$version", version);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$applied", DateTime.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
        }

        private void Execute(string sql) { using(var connection=OpenConnection()) using(var command=connection.CreateCommand()){command.CommandText=sql;command.ExecuteNonQuery();} }

        private void EnsureColumn(string table, string column, string definition)
        {
            using (var connection = OpenConnection())
            {
                var exists = false;
                using (var query = connection.CreateCommand())
                {
                    query.CommandText = "PRAGMA table_info(" + table + ")";
                    using (var reader = query.ExecuteReader())
                        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (exists) return;
                using (var alter = connection.CreateCommand()) { alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition; alter.ExecuteNonQuery(); }
            }
        }

        private void DropColumnIfExists(string table, string column)
        {
            using (var connection = OpenConnection())
            {
                var exists = false;
                using (var query = connection.CreateCommand())
                {
                    query.CommandText = "PRAGMA table_info(" + table + ")";
                    using (var reader = query.ExecuteReader())
                        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists) return;
                using (var alter = connection.CreateCommand()) { alter.CommandText = "ALTER TABLE " + table + " DROP COLUMN " + column; alter.ExecuteNonQuery(); }
            }
        }

        private void BackfillCaptureAssetMetadata()
        {
            var assets=new List<Tuple<string,string,string>>();
            using(var connection=OpenConnection())using(var query=connection.CreateCommand())
            {
                query.CommandText="SELECT Id,LocalPath,PhotoType FROM CapturePhotos WHERE FileLength<=0 OR ContentHashSha256 IS NULL OR ContentHashSha256='' OR MimeType IS NULL OR MimeType=''";
                using(var reader=query.ExecuteReader())while(reader.Read())assets.Add(Tuple.Create(reader.GetString(0),reader.GetString(1),reader.GetString(2)));
            }
            foreach(var asset in assets)
            {
                if(string.IsNullOrWhiteSpace(asset.Item2)||!File.Exists(asset.Item2)){using(var missingConnection=OpenConnection())using(var missing=missingConnection.CreateCommand()){missing.CommandText="UPDATE CapturePhotos SET AssetStatus='Missing' WHERE Id=$id";missing.Parameters.AddWithValue("$id",asset.Item1);missing.ExecuteNonQuery();}continue;}
                string hash;using(var stream=File.OpenRead(asset.Item2))using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();
                var mime=asset.Item3=="MotionPhoto"||asset.Item3=="Picture"?"image/jpeg":asset.Item3=="Composite"?"image/png":asset.Item3=="Gif"?"image/gif":asset.Item3=="ShareArchive"?"application/zip":"application/octet-stream";
                using(var connection=OpenConnection())using(var update=connection.CreateCommand()){update.CommandText="UPDATE CapturePhotos SET FileLength=$length,ContentHashSha256=$hash,MimeType=$mime,AssetStatus='Ready' WHERE Id=$id";update.Parameters.AddWithValue("$length",new FileInfo(asset.Item2).Length);update.Parameters.AddWithValue("$hash",hash);update.Parameters.AddWithValue("$mime",mime);update.Parameters.AddWithValue("$id",asset.Item1);update.ExecuteNonQuery();}
            }
        }
    }
}
