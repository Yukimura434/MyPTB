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
            using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000"; command.ExecuteNonQuery(); }
            return connection;
        }

        public void Initialize()
        {
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Presets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, SettingsJson TEXT);
CREATE TABLE IF NOT EXISTS AdminPresets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, SettingsJson TEXT, FrameId TEXT, PrinterProfileId TEXT, Countdown INTEGER, CreatedAtUtc TEXT, ModifiedAtUtc TEXT, IsDefault INTEGER);
CREATE TABLE IF NOT EXISTS PresetProcessingSettings (PresetId TEXT PRIMARY KEY, Brightness REAL NOT NULL, Contrast REAL NOT NULL, Saturation REAL NOT NULL, Gamma REAL NOT NULL, Exposure REAL NOT NULL, Temperature REAL NOT NULL, Tint REAL NOT NULL, Sharpen REAL NOT NULL, Blur REAL NOT NULL, Vignette REAL NOT NULL, BlackAndWhite INTEGER NOT NULL, Sepia INTEGER NOT NULL, WatermarkPath TEXT, WatermarkOpacity REAL NOT NULL, OutputWidth INTEGER NOT NULL, OutputHeight INTEGER NOT NULL, Dpi INTEGER NOT NULL, FOREIGN KEY(PresetId) REFERENCES AdminPresets(Id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS Sessions (Id TEXT PRIMARY KEY, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT, OutputDirectory TEXT);
CREATE TABLE IF NOT EXISTS CustomerSessions (Id TEXT PRIMARY KEY, PresetId TEXT, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT, OutputDirectory TEXT, CapturedFiles TEXT, FinalImagePath TEXT, Kind TEXT NOT NULL DEFAULT 'Event', EventId TEXT, Status TEXT NOT NULL DEFAULT 'Active', StateVersion INTEGER NOT NULL DEFAULT 0, TerminalReason TEXT, DisplayCode TEXT, UpdatedAtUtc TEXT);
CREATE TABLE IF NOT EXISTS CapturedImages (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, Sequence INTEGER NOT NULL, FilePath TEXT NOT NULL, CapturedAtUtc TEXT NOT NULL, PictureAssetId TEXT, VideoAssetId TEXT, FOREIGN KEY(SessionId) REFERENCES CustomerSessions(Id) ON DELETE CASCADE, UNIQUE(SessionId,Sequence), UNIQUE(SessionId,FilePath));
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
CREATE TABLE IF NOT EXISTS BeautySettings (Id INTEGER PRIMARY KEY CHECK(Id=1), Enabled INTEGER NOT NULL DEFAULT 0 CHECK(Enabled IN (0,1)), SmoothSkin INTEGER NOT NULL DEFAULT 0 CHECK(SmoothSkin BETWEEN 0 AND 100), BrightenSkin INTEGER NOT NULL DEFAULT 0 CHECK(BrightenSkin BETWEEN 0 AND 100), SkinTone INTEGER NOT NULL DEFAULT 0 CHECK(SkinTone BETWEEN 0 AND 100), Sharpen INTEGER NOT NULL DEFAULT 0 CHECK(Sharpen BETWEEN 0 AND 100), EyeSize INTEGER NOT NULL DEFAULT 0 CHECK(EyeSize BETWEEN 0 AND 100), SlimFace INTEGER NOT NULL DEFAULT 0 CHECK(SlimFace BETWEEN 0 AND 100), ModifiedAtUtc TEXT);
INSERT OR IGNORE INTO BeautySettings(Id,Enabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen) VALUES(1,0,0,0,0,0);
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
            EnsureColumn("WorkflowSettings", "RotateLiveView180", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("WorkflowSettings", "ImageRotationDegrees", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("WorkflowSettings", "CustomerLayoutMode", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("WorkflowSettings", "ShowWaitingLiveView", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn("WorkflowSettings", "WaitingLiveViewX", "REAL NOT NULL DEFAULT 10");
            EnsureColumn("WorkflowSettings", "WaitingLiveViewY", "REAL NOT NULL DEFAULT 10");
            EnsureColumn("WorkflowSettings", "WaitingLiveViewAreaPercent", "REAL NOT NULL DEFAULT 5");
            EnsureColumn("WorkflowSettings", "WaitingBackgroundZoom", "REAL NOT NULL DEFAULT 100");
            EnsureColumn("WorkflowSettings", "WaitingBackgroundPanX", "REAL NOT NULL DEFAULT 0");
            EnsureColumn("WorkflowSettings", "WaitingBackgroundPanY", "REAL NOT NULL DEFAULT 0");
            EnsureColumn("WorkflowSettings", "SaveLocation", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("BeautySettings", "EyeSize", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("BeautySettings", "SlimFace", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("Frames", "EventId", "TEXT");
            Execute("CREATE INDEX IF NOT EXISTS IX_Frames_EventId ON Frames(EventId);");
            EnsureColumn("CustomerSessions", "SessionName", "TEXT");
            EnsureColumn("CustomerSessions", "SessionNumber", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "CapturedImageIds", "TEXT");
            EnsureColumn("CustomerSessions", "IsDefault", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "CaptureIndex", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "FrameIndex", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "FinalImageId", "TEXT");
            EnsureColumn("CustomerSessions", "Kind", "TEXT NOT NULL DEFAULT 'Event'");
            EnsureColumn("CustomerSessions", "EventId", "TEXT");
            EnsureColumn("CustomerSessions", "Status", "TEXT NOT NULL DEFAULT 'Active'");
            EnsureColumn("CustomerSessions", "StateVersion", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("CustomerSessions", "TerminalReason", "TEXT");
            EnsureColumn("CustomerSessions", "DisplayCode", "TEXT");
            EnsureColumn("CustomerSessions", "UpdatedAtUtc", "TEXT");
            EnsureColumn("CapturedImages", "PictureAssetId", "TEXT");
            EnsureColumn("CapturedImages", "VideoAssetId", "TEXT");
            EnsureColumn("PrinterProfiles", "IsDefault", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("PrinterProfiles", "PrinterId", "TEXT");
            EnsureColumn("PrinterProfiles", "PrintInColor", "INTEGER NOT NULL DEFAULT 1");
            Execute("UPDATE CustomerSessions SET CaptureIndex=MAX(CaptureIndex,(SELECT COALESCE(MAX(Sequence),0) FROM CapturedImages WHERE SessionId=CustomerSessions.Id));");
            // Event names are operator-facing labels. Stable GUIDs own identity and
            // relationships, so duplicate names must remain valid and preserved.
            Execute(@"DROP INDEX IF EXISTS UX_CustomerSessions_SessionName;
CREATE INDEX IF NOT EXISTS IX_CustomerSessions_SessionName ON CustomerSessions(SessionName) WHERE SessionName IS NOT NULL AND SessionName <> '';");
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
            EnsureColumn("CapturedImages", "VideoPath", "TEXT");
            EnsureColumn("Captures", "DeviceId", "TEXT");
            EnsureColumn("Captures", "LocalSharePath", "TEXT");
            EnsureColumn("Captures", "MediaMode", "TEXT NOT NULL DEFAULT 'PictureOnly'");
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
DROP TRIGGER IF EXISTS TR_CapturePhotos_Validate_Insert;
DROP TRIGGER IF EXISTS TR_CapturePhotos_Validate_Update;
DROP TRIGGER IF EXISTS TR_CaptureAssetSources_SameCapture;
DROP TRIGGER IF EXISTS TR_CaptureAssetSources_ValidateTypes;
UPDATE CapturePhotos SET PhotoType='Picture' WHERE PhotoType='Original';
UPDATE CapturePhotos SET MimeType=CASE PhotoType WHEN 'Video' THEN 'video/mp4' WHEN 'CompositeVideo' THEN 'video/mp4' WHEN 'Picture' THEN 'image/jpeg' WHEN 'Composite' THEN 'image/png' WHEN 'Gif' THEN 'image/gif' WHEN 'ShareArchive' THEN 'application/zip' ELSE MimeType END WHERE MimeType IS NULL OR MimeType='';
UPDATE CapturePhotos SET CreatedAtUtc=(SELECT CreatedAtUtc FROM Captures WHERE Captures.Id=CapturePhotos.CaptureId) WHERE CreatedAtUtc IS NULL OR CreatedAtUtc='';
CREATE UNIQUE INDEX IF NOT EXISTS UX_Captures_CompositeImageId ON Captures(CompositeImageId) WHERE CompositeImageId IS NOT NULL AND CompositeImageId<>'';
CREATE UNIQUE INDEX IF NOT EXISTS UX_CustomerSessions_FinalImageId ON CustomerSessions(FinalImageId) WHERE FinalImageId IS NOT NULL AND FinalImageId<>'';
CREATE INDEX IF NOT EXISTS IX_CaptureAssetSources_Source ON CaptureAssetSources(SourceAssetId);
UPDATE CapturePhotos SET AssetStatus='Legacy' WHERE CaptureId IN (
 SELECT c.Id FROM Captures c WHERE
  (SELECT COUNT(*) FROM CapturePhotos p WHERE p.CaptureId=c.Id AND p.PhotoType='Picture')<>(SELECT COUNT(*) FROM CapturePhotos p WHERE p.CaptureId=c.Id AND p.PhotoType='Video')
  OR (SELECT COUNT(*) FROM CapturePhotos p WHERE p.CaptureId=c.Id AND p.PhotoType='Composite')<>1
  OR (SELECT COUNT(*) FROM CapturePhotos p WHERE p.CaptureId=c.Id AND p.PhotoType='CompositeVideo')<>1
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_CapturePhotos_CapturedImage_Type_Ready ON CapturePhotos(CapturedImageId,PhotoType) WHERE CapturedImageId IS NOT NULL AND AssetStatus<>'Legacy' AND PhotoType IN ('Picture','Video');
DROP TRIGGER IF EXISTS TR_CapturePhotos_Validate_Insert;
DROP TRIGGER IF EXISTS TR_CapturePhotos_Validate_Update;
DROP TRIGGER IF EXISTS TR_CaptureAssetSources_SameCapture;
DROP TRIGGER IF EXISTS TR_CaptureAssetSources_ValidateTypes;
CREATE TRIGGER TR_CapturePhotos_Validate_Insert BEFORE INSERT ON CapturePhotos BEGIN
 SELECT CASE WHEN NEW.Id IS NULL OR trim(NEW.Id)='' THEN RAISE(ABORT,'Capture asset ID is required') END;
 SELECT CASE WHEN NEW.PhotoType NOT IN ('Picture','Video','CompositeVideo','Composite','Gif','ShareArchive') THEN RAISE(ABORT,'Invalid capture asset type') END;
 SELECT CASE WHEN NEW.PhotoType IN ('Picture','Video') AND NEW.AssetStatus<>'Legacy' AND (NEW.CapturedImageId IS NULL OR trim(NEW.CapturedImageId)='') THEN RAISE(ABORT,'Original Picture and Video require a captured image ID') END;
 SELECT CASE WHEN NEW.FileLength<0 THEN RAISE(ABORT,'Invalid capture asset length') END;
 SELECT CASE WHEN NEW.CapturedImageId IS NOT NULL AND (SELECT SessionId FROM CapturedImages WHERE Id=NEW.CapturedImageId)<>(SELECT SessionId FROM Captures WHERE Id=NEW.CaptureId) THEN RAISE(ABORT,'Captured image and asset must belong to the same session') END;
END;
CREATE TRIGGER TR_CapturePhotos_Validate_Update BEFORE UPDATE ON CapturePhotos BEGIN
 SELECT CASE WHEN NEW.CaptureId<>OLD.CaptureId THEN RAISE(ABORT,'Capture asset ownership cannot change') END;
 SELECT CASE WHEN NEW.PhotoType NOT IN ('Picture','Video','CompositeVideo','Composite','Gif','ShareArchive') THEN RAISE(ABORT,'Invalid capture asset type') END;
 SELECT CASE WHEN NEW.PhotoType IN ('Picture','Video') AND NEW.AssetStatus<>'Legacy' AND (NEW.CapturedImageId IS NULL OR trim(NEW.CapturedImageId)='') THEN RAISE(ABORT,'Original Picture and Video require a captured image ID') END;
 SELECT CASE WHEN NEW.CapturedImageId IS NOT NULL AND (SELECT SessionId FROM CapturedImages WHERE Id=NEW.CapturedImageId)<>(SELECT SessionId FROM Captures WHERE Id=NEW.CaptureId) THEN RAISE(ABORT,'Captured image and asset must belong to the same session') END;
END;
CREATE TRIGGER TR_CaptureAssetSources_SameCapture BEFORE INSERT ON CaptureAssetSources BEGIN
 SELECT CASE WHEN (SELECT CaptureId FROM CapturePhotos WHERE Id=NEW.AssetId)<>(SELECT CaptureId FROM CapturePhotos WHERE Id=NEW.SourceAssetId) THEN RAISE(ABORT,'Asset source must belong to the same capture') END;
END;
CREATE TRIGGER TR_CaptureAssetSources_ValidateTypes BEFORE INSERT ON CaptureAssetSources BEGIN
 SELECT CASE WHEN (SELECT AssetStatus FROM CapturePhotos WHERE Id=NEW.AssetId)<>'Legacy' AND NOT (
  ((SELECT PhotoType FROM CapturePhotos WHERE Id=NEW.AssetId)='Composite' AND (SELECT PhotoType FROM CapturePhotos WHERE Id=NEW.SourceAssetId)='Picture') OR
  ((SELECT PhotoType FROM CapturePhotos WHERE Id=NEW.AssetId)='CompositeVideo' AND (SELECT PhotoType FROM CapturePhotos WHERE Id=NEW.SourceAssetId)='Video') OR
  ((SELECT PhotoType FROM CapturePhotos WHERE Id=NEW.AssetId)='Gif' AND (SELECT PhotoType FROM CapturePhotos WHERE Id=NEW.SourceAssetId) IN ('Picture','Video')) OR
  ((SELECT PhotoType FROM CapturePhotos WHERE Id=NEW.AssetId)='ShareArchive')
 ) THEN RAISE(ABORT,'Invalid capture asset lineage types') END;
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
WHERE ((derived.PhotoType='Composite' AND source.PhotoType='Picture')
   OR (derived.PhotoType='CompositeVideo' AND source.PhotoType='Video')
   OR (derived.PhotoType='Gif' AND source.PhotoType IN ('Picture','Video')))
AND NOT EXISTS(SELECT 1 FROM CaptureAssetSources existing WHERE existing.AssetId=derived.Id AND existing.SourceAssetId=source.Id);
INSERT OR IGNORE INTO CaptureAssetSources(AssetId,SourceAssetId)
SELECT archive.Id,source.Id FROM CapturePhotos archive JOIN CapturePhotos source ON source.CaptureId=archive.CaptureId AND source.Id<>archive.Id
WHERE archive.PhotoType='ShareArchive'
AND NOT EXISTS(SELECT 1 FROM CaptureAssetSources existing WHERE existing.AssetId=archive.Id AND existing.SourceAssetId=source.Id);");
            BackfillCaptureAssetMetadata();
            RecordMigration(4, "capture_asset_identity_metadata_and_lineage");
            RecordMigration(5, "frame_event_collections");
            RecordMigration(6, "composite_video_asset");
            RecordMigration(7, "capture_media_mode");
            ApplyMigration(8, "waiting_live_view_default_area_5_percent", "UPDATE WorkflowSettings SET WaitingLiveViewAreaPercent=5 WHERE WaitingLiveViewAreaPercent=10;");
            ApplyMigration(9, "local_business_sessions_attempts_assets_jobs", @"
CREATE TABLE IF NOT EXISTS CaptureAttempts (
 Id TEXT PRIMARY KEY NOT NULL,
 SessionId TEXT NOT NULL,
 Sequence INTEGER NOT NULL,
 AttemptNumber INTEGER NOT NULL DEFAULT 1 CHECK(AttemptNumber>=1),
 CameraId TEXT,
 PictureAssetId TEXT NOT NULL,
 VideoAssetId TEXT,
 Status TEXT NOT NULL CHECK(Status IN ('IntentRecorded','Accepted','Failed','Unknown')),
 IntentAtUtc TEXT NOT NULL,
 CompletedAtUtc TEXT,
 LastError TEXT,
 FOREIGN KEY(SessionId) REFERENCES CustomerSessions(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_CaptureAttempts_Session_Sequence ON CaptureAttempts(SessionId,Sequence,AttemptNumber);
CREATE INDEX IF NOT EXISTS IX_CaptureAttempts_Incomplete ON CaptureAttempts(Status,IntentAtUtc);
CREATE TABLE IF NOT EXISTS MediaAssets (
 Id TEXT PRIMARY KEY NOT NULL,
 SessionId TEXT NOT NULL,
 CaptureAttemptId TEXT,
 Kind TEXT NOT NULL CHECK(Kind IN ('OriginalPicture','OriginalVideo','FinalComposite','FinalVideo','Gif','ShareArchive')),
 RelativePath TEXT NOT NULL,
 MimeType TEXT NOT NULL,
 FileLength INTEGER NOT NULL CHECK(FileLength>=0),
 ContentHashSha256 TEXT,
 Status TEXT NOT NULL CHECK(Status IN ('Staging','Ready','Missing','PendingDelete','Deleted')),
 RetentionClass TEXT NOT NULL CHECK(RetentionClass IN ('Work','Original','Deliverable')),
 CreatedAtUtc TEXT NOT NULL,
 UpdatedAtUtc TEXT NOT NULL,
 FOREIGN KEY(SessionId) REFERENCES CustomerSessions(Id) ON DELETE CASCADE,
 FOREIGN KEY(CaptureAttemptId) REFERENCES CaptureAttempts(Id) ON DELETE SET NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_MediaAssets_Session_Path ON MediaAssets(SessionId,RelativePath);
CREATE INDEX IF NOT EXISTS IX_MediaAssets_Session_Kind ON MediaAssets(SessionId,Kind,Status);
CREATE INDEX IF NOT EXISTS IX_MediaAssets_Retention ON MediaAssets(RetentionClass,Status,UpdatedAtUtc);
CREATE TABLE IF NOT EXISTS OutputJobs (
 Id TEXT PRIMARY KEY NOT NULL,
 SessionId TEXT NOT NULL,
 AssetId TEXT NOT NULL,
 JobType TEXT NOT NULL CHECK(JobType IN ('Print','Upload','Delivery')),
 IdempotencyKey TEXT NOT NULL UNIQUE,
 State TEXT NOT NULL CHECK(State IN ('Pending','Leased','Submitting','Submitted','Completed','RetryWaiting','UnknownOutcome','PermanentFailure','Cancelled')),
 AttemptCount INTEGER NOT NULL DEFAULT 0,
 NextRetryAtUtc TEXT,
 LeaseId TEXT,
 LeaseExpiresAtUtc TEXT,
 LastError TEXT,
 CreatedAtUtc TEXT NOT NULL,
 UpdatedAtUtc TEXT NOT NULL,
 FOREIGN KEY(SessionId) REFERENCES CustomerSessions(Id) ON DELETE RESTRICT,
 FOREIGN KEY(AssetId) REFERENCES MediaAssets(Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_OutputJobs_Due ON OutputJobs(State,NextRetryAtUtc,LeaseExpiresAtUtc);
CREATE INDEX IF NOT EXISTS IX_OutputJobs_Session ON OutputJobs(SessionId,State);
CREATE TABLE IF NOT EXISTS SyncOutbox (
 Id TEXT PRIMARY KEY NOT NULL,
 AggregateType TEXT NOT NULL,
 AggregateId TEXT NOT NULL,
 EventType TEXT NOT NULL,
 PayloadJson TEXT NOT NULL,
 State TEXT NOT NULL DEFAULT 'Pending' CHECK(State IN ('Pending','Leased','Sent','RetryWaiting','PermanentFailure')),
 AttemptCount INTEGER NOT NULL DEFAULT 0,
 NextRetryAtUtc TEXT,
 LeaseId TEXT,
 LeaseExpiresAtUtc TEXT,
 CreatedAtUtc TEXT NOT NULL,
 UpdatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SyncOutbox_Due ON SyncOutbox(State,NextRetryAtUtc,LeaseExpiresAtUtc);
UPDATE CustomerSessions SET Kind='Event' WHERE Kind IS NULL OR Kind='';
UPDATE CustomerSessions SET Status=CASE WHEN CompletedAtUtc IS NULL THEN 'Active' ELSE 'Completed' END WHERE Status IS NULL OR Status='';
");
            ApplyMigration(10, "canonical_business_read_model", @"
UPDATE CustomerSessions SET SessionName='Sự kiện mặc định'
WHERE Kind='Event' AND SessionName='Base_session'
AND NOT EXISTS(SELECT 1 FROM CustomerSessions existing WHERE existing.Kind='Event' AND existing.SessionName='Sự kiện mặc định');
DROP VIEW IF EXISTS Events;
CREATE VIEW Events AS
SELECT Id,PresetId,SessionName AS Name,IsDefault,StartedAtUtc AS CreatedAtUtc,UpdatedAtUtc
FROM CustomerSessions WHERE Kind='Event';
DROP VIEW IF EXISTS BoothSessions;
CREATE VIEW BoothSessions AS
SELECT Id,EventId,PresetId,Status,StateVersion,DisplayCode,StartedAtUtc,UpdatedAtUtc,CompletedAtUtc,TerminalReason,OutputDirectory,FinalImageId,FinalImagePath
FROM CustomerSessions WHERE Kind='Booth';
DROP VIEW IF EXISTS Deliverables;
CREATE VIEW Deliverables AS
SELECT Id,SessionId AS BoothSessionId,FrameId,CompositeImageId AS FinalCompositeAssetId,CompositePath AS FinalCompositePath,LocalSharePath,MediaMode,Status,UploadAttempts,CreatedAtUtc,UploadedAtUtc,ExpiresAtUtc,LastError
FROM Captures;
DROP VIEW IF EXISTS DeliverableAssets;
CREATE VIEW DeliverableAssets AS
SELECT Id,CaptureId AS DeliverableId,CapturedImageId AS CapturedShotId,LocalPath,PhotoType AS Role,Position,MimeType,FileLength,ContentHashSha256,CreatedAtUtc,AssetStatus,CloudinaryPublicId,IsUploaded,UploadAttempts,UploadedAtUtc,LastError
FROM CapturePhotos;
DROP VIEW IF EXISTS DeliverableAssetSources;
CREATE VIEW DeliverableAssetSources AS SELECT AssetId,SourceAssetId FROM CaptureAssetSources;
");
            ApplyMigration(11, "canonical_business_view_scope", @"
DROP VIEW IF EXISTS DeliverableAssetSources;
DROP VIEW IF EXISTS DeliverableAssets;
DROP VIEW IF EXISTS Deliverables;
CREATE VIEW Deliverables AS
SELECT c.Id,c.SessionId AS BoothSessionId,c.FrameId,c.CompositeImageId AS FinalCompositeAssetId,c.CompositePath AS FinalCompositePath,c.LocalSharePath,c.MediaMode,c.Status,c.UploadAttempts,c.CreatedAtUtc,c.UploadedAtUtc,c.ExpiresAtUtc,c.LastError
FROM Captures c
INNER JOIN CustomerSessions b ON b.Id=c.SessionId AND b.Kind='Booth';
CREATE VIEW DeliverableAssets AS
SELECT p.Id,p.CaptureId AS DeliverableId,p.CapturedImageId AS CapturedShotId,p.LocalPath,p.PhotoType AS Role,p.Position,p.MimeType,p.FileLength,p.ContentHashSha256,p.CreatedAtUtc,p.AssetStatus,p.CloudinaryPublicId,p.IsUploaded,p.UploadAttempts,p.UploadedAtUtc,p.LastError
FROM CapturePhotos p
INNER JOIN Deliverables d ON d.Id=p.CaptureId;
CREATE VIEW DeliverableAssetSources AS
SELECT s.AssetId,s.SourceAssetId
FROM CaptureAssetSources s
INNER JOIN DeliverableAssets a ON a.Id=s.AssetId;
");
            ApplyMigration(12, "event_names_are_non_unique_labels", @"
DROP INDEX IF EXISTS UX_CustomerSessions_SessionName;
CREATE INDEX IF NOT EXISTS IX_CustomerSessions_SessionName ON CustomerSessions(SessionName) WHERE SessionName IS NOT NULL AND SessionName <> '';
");
            ApplyMigration(13, "photo_event_configuration_and_frames", @"
CREATE TABLE IF NOT EXISTS EventConfigurations (
 EventId TEXT PRIMARY KEY NOT NULL,
 PhotoCount INTEGER NOT NULL CHECK(PhotoCount BETWEEN 1 AND 8),
 CountdownSeconds INTEGER NOT NULL CHECK(CountdownSeconds BETWEEN 1 AND 10),
 GifFrameDurationMs INTEGER NOT NULL CHECK(GifFrameDurationMs BETWEEN 400 AND 1000),
 WaitingTimeoutSeconds INTEGER NOT NULL CHECK(WaitingTimeoutSeconds IN (30,60,120,300,600,900)),
 CustomerLayoutMode INTEGER NOT NULL CHECK(CustomerLayoutMode IN (0,1)),
 ImageRotationDegrees INTEGER NOT NULL CHECK(ImageRotationDegrees IN (-90,0,90,180)),
 BeautyEnabled INTEGER NOT NULL CHECK(BeautyEnabled IN (0,1)),
 SmoothSkin INTEGER NOT NULL CHECK(SmoothSkin BETWEEN 0 AND 100),
 BrightenSkin INTEGER NOT NULL CHECK(BrightenSkin BETWEEN 0 AND 100),
 SkinTone INTEGER NOT NULL CHECK(SkinTone BETWEEN 0 AND 100),
 Sharpen INTEGER NOT NULL CHECK(Sharpen BETWEEN 0 AND 100),
 EyeSize INTEGER NOT NULL CHECK(EyeSize BETWEEN 0 AND 100),
 SlimFace INTEGER NOT NULL CHECK(SlimFace BETWEEN 0 AND 100),
 ModifiedAtUtc TEXT NOT NULL,
 RowVersion INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion>=1),
 FOREIGN KEY(EventId) REFERENCES CustomerSessions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS EventFrames (
 EventId TEXT NOT NULL,
 FrameId TEXT NOT NULL,
 SortOrder INTEGER NOT NULL CHECK(SortOrder BETWEEN 0 AND 9),
 PRIMARY KEY(EventId,FrameId),
 UNIQUE(EventId,SortOrder),
 FOREIGN KEY(EventId) REFERENCES EventConfigurations(EventId) ON DELETE CASCADE,
 FOREIGN KEY(FrameId) REFERENCES Frames(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_EventFrames_FrameId ON EventFrames(FrameId);
CREATE TRIGGER IF NOT EXISTS TR_EventConfigurations_EventOnly_Insert BEFORE INSERT ON EventConfigurations
WHEN NOT EXISTS(SELECT 1 FROM CustomerSessions WHERE Id=NEW.EventId AND Kind='Event')
BEGIN SELECT RAISE(ABORT,'Event configuration owner must be an Event'); END;
CREATE TRIGGER IF NOT EXISTS TR_EventConfigurations_EventOnly_Update BEFORE UPDATE OF EventId ON EventConfigurations
WHEN NEW.EventId<>OLD.EventId OR NOT EXISTS(SELECT 1 FROM CustomerSessions WHERE Id=NEW.EventId AND Kind='Event')
BEGIN SELECT RAISE(ABORT,'Event configuration owner cannot change'); END;
");
            Execute("CREATE INDEX IF NOT EXISTS IX_CapturedImages_SessionId ON CapturedImages(SessionId); CREATE INDEX IF NOT EXISTS IX_FrameSlots_FrameId ON FrameSlots(FrameId); CREATE INDEX IF NOT EXISTS IX_CustomerSessions_StartedAtUtc ON CustomerSessions(StartedAtUtc); CREATE INDEX IF NOT EXISTS IX_CustomerSessions_Kind_Status ON CustomerSessions(Kind,Status,StartedAtUtc); CREATE INDEX IF NOT EXISTS IX_CustomerSessions_EventId ON CustomerSessions(EventId,StartedAtUtc); CREATE INDEX IF NOT EXISTS IX_CustomerSessions_Account_Device ON CustomerSessions(AccountId,DeviceId,StartedAtUtc); CREATE INDEX IF NOT EXISTS IX_Captures_SessionId ON Captures(SessionId); CREATE INDEX IF NOT EXISTS IX_Captures_Account_Device ON Captures(AccountId,DeviceId,CreatedAtUtc); CREATE INDEX IF NOT EXISTS IX_Captures_Status_ExpiresAtUtc ON Captures(Status,ExpiresAtUtc); CREATE INDEX IF NOT EXISTS IX_CapturePhotos_CaptureId ON CapturePhotos(CaptureId); CREATE INDEX IF NOT EXISTS IX_CapturePhotos_IsUploaded ON CapturePhotos(IsUploaded); CREATE INDEX IF NOT EXISTS IX_UploadQueue_Due ON UploadQueue(Status,NextRetryAtUtc); CREATE INDEX IF NOT EXISTS IX_PrintJobs_PrintedAtUtc ON PrintJobs(PrintedAtUtc); CREATE INDEX IF NOT EXISTS IX_PrintJobs_Status ON PrintJobs(Status); CREATE INDEX IF NOT EXISTS IX_PrintJobs_SessionId ON PrintJobs(SessionId);");
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

        private void ApplyMigration(int version, string name, string sql)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Version=$version";
                    check.Parameters.AddWithValue("$version", version);
                    if (Convert.ToInt32(check.ExecuteScalar()) > 0) { transaction.Commit(); return; }
                }
                using (var update = connection.CreateCommand()) { update.Transaction = transaction; update.CommandText = sql; update.ExecuteNonQuery(); }
                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText = "INSERT INTO SchemaMigrations(Version,Name,AppliedAtUtc) VALUES($version,$name,$applied)";
                    record.Parameters.AddWithValue("$version", version);
                    record.Parameters.AddWithValue("$name", name);
                    record.Parameters.AddWithValue("$applied", DateTime.UtcNow.ToString("O"));
                    record.ExecuteNonQuery();
                }
                transaction.Commit();
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
                query.CommandText="SELECT Id,LocalPath,PhotoType FROM CapturePhotos WHERE PhotoType IN ('Picture','Video','Composite','CompositeVideo','Gif','ShareArchive') AND (FileLength<=0 OR ContentHashSha256 IS NULL OR ContentHashSha256='' OR MimeType IS NULL OR MimeType='')";
                using(var reader=query.ExecuteReader())while(reader.Read())assets.Add(Tuple.Create(reader.GetString(0),reader.GetString(1),reader.GetString(2)));
            }
            foreach(var asset in assets)
            {
                if(string.IsNullOrWhiteSpace(asset.Item2)||!File.Exists(asset.Item2)){using(var missingConnection=OpenConnection())using(var missing=missingConnection.CreateCommand()){missing.CommandText="UPDATE CapturePhotos SET AssetStatus='Missing' WHERE Id=$id";missing.Parameters.AddWithValue("$id",asset.Item1);missing.ExecuteNonQuery();}continue;}
                string hash;using(var stream=File.OpenRead(asset.Item2))using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();
                var mime=asset.Item3=="Video"||asset.Item3=="CompositeVideo"?"video/mp4":asset.Item3=="Picture"?"image/jpeg":asset.Item3=="Composite"?"image/png":asset.Item3=="Gif"?"image/gif":asset.Item3=="ShareArchive"?"application/zip":"application/octet-stream";
                using(var connection=OpenConnection())using(var update=connection.CreateCommand()){update.CommandText="UPDATE CapturePhotos SET FileLength=$length,ContentHashSha256=$hash,MimeType=$mime,AssetStatus='Ready' WHERE Id=$id";update.Parameters.AddWithValue("$length",new FileInfo(asset.Item2).Length);update.Parameters.AddWithValue("$hash",hash);update.Parameters.AddWithValue("$mime",mime);update.Parameters.AddWithValue("$id",asset.Item1);update.ExecuteNonQuery();}
            }
        }
    }
}
