using System;
using System.IO;
using Microsoft.Data.Sqlite;

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
                command.ExecuteNonQuery();
            }
            RecordMigration(1, "legacy_schema_baseline");
            RecordMigration(2, "local_identity_license_pin_upload_queue");
            DropColumnIfExists("ProductionSettings", "EnableUpload");
            EnsureColumn("WorkflowSettings", "DelayBetweenShots", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn("WorkflowSettings", "GifFrameDurationMs", "INTEGER NOT NULL DEFAULT 1000");
            EnsureColumn("WorkflowSettings", "WaitingTimeoutSeconds", "INTEGER NOT NULL DEFAULT 30");
            EnsureColumn("WorkflowSettings", "AutoFlip", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("WorkflowSettings", "ShowWaitingLiveView", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn("WorkflowSettings", "WaitingLiveViewX", "REAL NOT NULL DEFAULT 10");
            EnsureColumn("WorkflowSettings", "WaitingLiveViewY", "REAL NOT NULL DEFAULT 10");
            EnsureColumn("WorkflowSettings", "SaveLocation", "INTEGER NOT NULL DEFAULT 0");
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
            EnsureColumn("Captures", "DeviceId", "TEXT");
            EnsureColumn("Captures", "LocalSharePath", "TEXT");
            EnsureColumn("ProductionSettings", "LocalShareEnabled", "INTEGER NOT NULL DEFAULT 1");
            Execute("CREATE TABLE IF NOT EXISTS InterfaceAssets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, FilePath TEXT NOT NULL UNIQUE, IsAnimated INTEGER NOT NULL DEFAULT 0, IsSelected INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS UX_InterfaceAssets_OneSelected ON InterfaceAssets(IsSelected) WHERE IsSelected=1;");
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
    }
}
