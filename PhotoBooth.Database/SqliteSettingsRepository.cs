using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteSettingsRepository : ISettingsRepository
    {
        readonly SqliteDatabase db;
        public SqliteSettingsRepository(SqliteDatabase database) { db = database; }

        public Task<Settings> GetAsync(CancellationToken token)
        {
            var settings = new Settings();
            using (var connection = db.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Culture,CaptureDirectory,AlphaThreshold,MinimumSlotArea,MinimumSlotWidth,MinimumSlotHeight,IgnoreBorder,MaximumSlots,PhotoCount,CountdownSeconds,DefaultFrameId,DefaultPresetId,DefaultPrinterProfileId,KioskMode,KeepFinal,DelayBetweenShots,AutoFlip,SaveLocation,GifFrameDurationMs,WaitingTimeoutSeconds,ShowWaitingLiveView,WaitingLiveViewX,WaitingLiveViewY,RotateLiveView180,CustomerLayoutMode,ImageRotationDegrees,WaitingBackgroundZoom,WaitingBackgroundPanX,WaitingBackgroundPanY,WaitingLiveViewAreaPercent,PrintingEnabled,PreferredCameraId,SelectedFrameCollections,SelectedPresetCollections,BoothSettingsConfigured,LiveViewPresetId,LiveViewPresetStrengthPercent,CreateGif,CreateVideo,PrintedQrEnabled,PrintedQrX,PrintedQrY,PrintedQrSize,QrPreviewImagePath FROM WorkflowSettings WHERE Id=1";
                    using (var reader = command.ExecuteReader()) if (reader.Read())
                    {
                        settings.Culture = Text(reader, 0); settings.CaptureDirectory = Text(reader, 1);
                        settings.TransparentAlphaThreshold = (byte)reader.GetInt32(2); settings.MinimumSlotArea = reader.GetInt32(3); settings.MinimumSlotWidth = reader.GetInt32(4); settings.MinimumSlotHeight = reader.GetInt32(5); settings.IgnoreBorderTransparency = reader.GetInt32(6) != 0; settings.MaximumFrameSlots = reader.GetInt32(7); settings.PhotoCount = reader.GetInt32(8); settings.CountdownSeconds = reader.GetInt32(9);
                        settings.DefaultFrameId = GuidValue(reader, 10); settings.DefaultPresetId = GuidValue(reader, 11); settings.DefaultPrinterProfileId = GuidValue(reader, 12); settings.KioskMode = reader.GetInt32(13) != 0; settings.KeepFinalPrintedImage = reader.GetInt32(14) != 0; settings.DelayBetweenShotsSeconds = Clamp(reader.GetInt32(15), 1, 3); settings.AutoFlip = reader.GetInt32(16) != 0; settings.SaveLocation = (PhotoBooth.Core.Cameras.CameraSaveMode)reader.GetInt32(17); settings.GifFrameDurationMilliseconds = Clamp(reader.GetInt32(18), 800, 1500); settings.WaitingTimeoutSeconds = NormalizeWaitingTimeout(reader.GetInt32(19));
                        settings.ShowWaitingLiveView = reader.GetInt32(20) != 0; settings.WaitingLiveViewX = Clamp(reader.GetDouble(21), 0, 100); settings.WaitingLiveViewY = Clamp(reader.GetDouble(22), 0, 100);
                        var rotation = NormalizeRotation(reader.GetInt32(25)); settings.ImageRotationDegrees = rotation == 0 && reader.GetInt32(23) != 0 ? 180 : rotation; settings.CustomerLayoutMode = reader.GetInt32(24) == 1 ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape;
                        settings.WaitingBackgroundZoom = Clamp(reader.GetDouble(26), 100, 300); settings.WaitingBackgroundPanX = Clamp(reader.GetDouble(27), -100, 100); settings.WaitingBackgroundPanY = Clamp(reader.GetDouble(28), -100, 100);
                        settings.WaitingLiveViewAreaPercent = Clamp(reader.GetDouble(29), LiveViewLayoutGeometry.MinimumAreaPercent, LiveViewLayoutGeometry.MaximumAreaPercent);
                        settings.PrintingEnabled = reader.GetInt32(30) != 0;
                        settings.PreferredCameraId = Text(reader, 31);
                        settings.SelectedFrameCollectionIds = ReadIds(Text(reader, 32));
                        settings.SelectedPresetCollectionIds = ReadIds(Text(reader, 33));
                        settings.BoothSettingsConfigured = reader.GetInt32(34) != 0;
                        settings.LiveViewPresetId = GuidValue(reader, 35);
                        settings.LiveViewPresetStrengthPercent = Clamp(reader.GetInt32(36), 0, 100);
                        settings.CreateGif = reader.GetInt32(37) != 0; settings.CreateVideo = reader.GetInt32(38) != 0;
                        settings.PrintedQr = new FrameQrSettings { Enabled=reader.GetInt32(39)!=0, X=reader.GetDouble(40), Y=reader.GetDouble(41), Size=reader.GetDouble(42) };
                        settings.QrPreviewImagePath = Text(reader,43);
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT SessionRetentionDays,TempRetentionHours,PrintRetryCount,CameraReconnectSeconds,EnableQr,EnablePlugins,EnableDiagnostics,EnableTelemetry,AutoStart,Theme,LogLevel,AdminPasswordHash,LocalShareEnabled,ShareDeliveryMode FROM ProductionSettings WHERE Id=1";
                    using (var reader = command.ExecuteReader()) if (reader.Read()) { settings.SessionRetentionDays = reader.GetInt32(0); settings.TemporaryFileRetentionHours = reader.GetInt32(1); settings.PrintRetryCount = reader.GetInt32(2); settings.CameraReconnectSeconds = reader.GetInt32(3); settings.EnableQr = reader.GetInt32(4) != 0; settings.EnablePlugins = reader.GetInt32(5) != 0; settings.EnableDiagnostics = reader.GetInt32(6) != 0; settings.EnableTelemetry = reader.GetInt32(7) != 0; settings.AutoStart = reader.GetInt32(8) != 0; settings.Theme = Text(reader, 9); settings.LogLevel = Text(reader, 10); settings.AdminPasswordHash = Text(reader, 11); settings.LocalShareEnabled = reader.GetInt32(12) != 0; settings.ShareDeliveryMode = ShareDeliveryModes.Normalize(Text(reader,13)); }
                }
            }
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Orientation,BackgroundAssetId,ShowLiveView,LiveViewX,LiveViewY,LiveViewAreaPercent,BackgroundZoom,BackgroundPanX,BackgroundPanY FROM WaitingScreenProfiles";
                using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    var profile = new WaitingScreenProfile {
                        BackgroundAssetId = GuidValue(reader, 1), ShowLiveView = reader.GetInt32(2) != 0,
                        LiveViewX = Clamp(reader.GetDouble(3), 0, 100), LiveViewY = Clamp(reader.GetDouble(4), 0, 100),
                        LiveViewAreaPercent = Clamp(reader.GetDouble(5), 5, 40), BackgroundZoom = Clamp(reader.GetDouble(6), 100, 300),
                        BackgroundPanX = Clamp(reader.GetDouble(7), -100, 100), BackgroundPanY = Clamp(reader.GetDouble(8), -100, 100)
                    };
                    if (reader.GetInt32(0) == 1) settings.PortraitWaitingScreen = profile;
                    else settings.LandscapeWaitingScreen = profile;
                }
            }
            return Task.FromResult(settings);
        }

        public Task SaveAsync(Settings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR REPLACE INTO WorkflowSettings (Id,Culture,CaptureDirectory,AlphaThreshold,MinimumSlotArea,MinimumSlotWidth,MinimumSlotHeight,IgnoreBorder,MaximumSlots,PhotoCount,CountdownSeconds,DefaultFrameId,DefaultPresetId,DefaultPrinterProfileId,KioskMode,KeepFinal,DelayBetweenShots,AutoFlip,SaveLocation,GifFrameDurationMs,WaitingTimeoutSeconds,ShowWaitingLiveView,WaitingLiveViewX,WaitingLiveViewY,RotateLiveView180,CustomerLayoutMode,ImageRotationDegrees,WaitingBackgroundZoom,WaitingBackgroundPanX,WaitingBackgroundPanY,WaitingLiveViewAreaPercent,PrintingEnabled,PreferredCameraId,SelectedFrameCollections,SelectedPresetCollections,BoothSettingsConfigured,LiveViewPresetId,LiveViewPresetStrengthPercent,CreateGif,CreateVideo,PrintedQrEnabled,PrintedQrX,PrintedQrY,PrintedQrSize,QrPreviewImagePath) VALUES(1,$culture,$dir,$alpha,$area,$width,$height,$border,$max,$count,$down,$frame,$preset,$printer,$kiosk,$keep,$delay,$autoFlip,$saveLocation,$gifDuration,$waitingTimeout,$showLive,$liveX,$liveY,$rotate180,$layoutMode,$imageRotation,$backgroundZoom,$backgroundPanX,$backgroundPanY,$liveViewAreaPercent,$printingEnabled,$preferredCameraId,$frameCollections,$presetCollections,$boothConfigured,$livePreset,$liveStrength,$createGif,$createVideo,$qrEnabled,$qrX,$qrY,$qrSize,$qrImage)";
                    command.Parameters.AddWithValue("$culture", Db(settings.Culture)); command.Parameters.AddWithValue("$dir", Db(settings.CaptureDirectory)); command.Parameters.AddWithValue("$alpha", settings.TransparentAlphaThreshold); command.Parameters.AddWithValue("$area", settings.MinimumSlotArea); command.Parameters.AddWithValue("$width", settings.MinimumSlotWidth); command.Parameters.AddWithValue("$height", settings.MinimumSlotHeight); command.Parameters.AddWithValue("$border", settings.IgnoreBorderTransparency ? 1 : 0); command.Parameters.AddWithValue("$max", settings.MaximumFrameSlots); command.Parameters.AddWithValue("$count", settings.PhotoCount); command.Parameters.AddWithValue("$down", settings.CountdownSeconds); command.Parameters.AddWithValue("$frame", Db(settings.DefaultFrameId)); command.Parameters.AddWithValue("$preset", Db(settings.DefaultPresetId)); command.Parameters.AddWithValue("$printer", Db(settings.DefaultPrinterProfileId)); command.Parameters.AddWithValue("$kiosk", settings.KioskMode ? 1 : 0); command.Parameters.AddWithValue("$keep", settings.KeepFinalPrintedImage ? 1 : 0); command.Parameters.AddWithValue("$delay", Clamp(settings.DelayBetweenShotsSeconds, 1, 3)); command.Parameters.AddWithValue("$autoFlip", settings.AutoFlip ? 1 : 0); command.Parameters.AddWithValue("$saveLocation", (int)settings.SaveLocation); command.Parameters.AddWithValue("$gifDuration", Clamp(settings.GifFrameDurationMilliseconds, 800, 1500)); command.Parameters.AddWithValue("$waitingTimeout", NormalizeWaitingTimeout(settings.WaitingTimeoutSeconds)); command.Parameters.AddWithValue("$showLive", settings.ShowWaitingLiveView ? 1 : 0); command.Parameters.AddWithValue("$liveX", Clamp(settings.WaitingLiveViewX, 0, 100)); command.Parameters.AddWithValue("$liveY", Clamp(settings.WaitingLiveViewY, 0, 100)); command.Parameters.AddWithValue("$rotate180", 0); command.Parameters.AddWithValue("$layoutMode", settings.CustomerLayoutMode == CustomerLayoutMode.Portrait ? 1 : 0); command.Parameters.AddWithValue("$imageRotation", NormalizeRotation(settings.ImageRotationDegrees)); command.Parameters.AddWithValue("$backgroundZoom", Clamp(settings.WaitingBackgroundZoom, 100, 300)); command.Parameters.AddWithValue("$backgroundPanX", Clamp(settings.WaitingBackgroundPanX, -100, 100)); command.Parameters.AddWithValue("$backgroundPanY", Clamp(settings.WaitingBackgroundPanY, -100, 100)); command.Parameters.AddWithValue("$liveViewAreaPercent", Clamp(settings.WaitingLiveViewAreaPercent, LiveViewLayoutGeometry.MinimumAreaPercent, LiveViewLayoutGeometry.MaximumAreaPercent)); command.Parameters.AddWithValue("$printingEnabled", settings.PrintingEnabled ? 1 : 0); command.Parameters.AddWithValue("$frameCollections", string.Join(",", (settings.SelectedFrameCollectionIds ?? new Guid[0]).Distinct()));
                    command.Parameters.AddWithValue("$presetCollections", string.Join(",", (settings.SelectedPresetCollectionIds ?? new Guid[0]).Distinct()));
                    command.Parameters.AddWithValue("$boothConfigured", settings.BoothSettingsConfigured ? 1 : 0);
                    command.Parameters.AddWithValue("$livePreset", Db(settings.LiveViewPresetId));
                    var qr = settings.PrintedQr ?? new FrameQrSettings();
                    command.Parameters.AddWithValue("$qrEnabled", qr.Enabled ? 1 : 0);
                    command.Parameters.AddWithValue("$qrX", qr.X); command.Parameters.AddWithValue("$qrY", qr.Y); command.Parameters.AddWithValue("$qrSize", qr.Size);
                    command.Parameters.AddWithValue("$qrImage", (object)settings.QrPreviewImagePath ?? DBNull.Value);
                    command.Parameters.AddWithValue("$createGif", settings.CreateGif ? 1 : 0); command.Parameters.AddWithValue("$createVideo", settings.CreateVideo ? 1 : 0);
                    command.Parameters.AddWithValue("$liveStrength", Clamp(settings.LiveViewPresetStrengthPercent, 0, 100));
                    command.Parameters.AddWithValue("$preferredCameraId", Db(settings.PreferredCameraId)); command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    token.ThrowIfCancellationRequested();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR REPLACE INTO ProductionSettings (Id,SessionRetentionDays,TempRetentionHours,PrintRetryCount,CameraReconnectSeconds,EnableQr,EnablePlugins,EnableDiagnostics,EnableTelemetry,AutoStart,Theme,LogLevel,AdminPasswordHash,LocalShareEnabled,ShareDeliveryMode) VALUES(1,$days,$hours,$retry,$reconnect,$qr,$plugins,$diagnostics,$telemetry,$start,$theme,$log,$password,$localShare,$shareMode)";
                    command.Parameters.AddWithValue("$days", settings.SessionRetentionDays); command.Parameters.AddWithValue("$hours", settings.TemporaryFileRetentionHours); command.Parameters.AddWithValue("$retry", settings.PrintRetryCount); command.Parameters.AddWithValue("$reconnect", settings.CameraReconnectSeconds); command.Parameters.AddWithValue("$qr", settings.EnableQr ? 1 : 0); command.Parameters.AddWithValue("$plugins", settings.EnablePlugins ? 1 : 0); command.Parameters.AddWithValue("$diagnostics", settings.EnableDiagnostics ? 1 : 0); command.Parameters.AddWithValue("$telemetry", settings.EnableTelemetry ? 1 : 0); command.Parameters.AddWithValue("$start", settings.AutoStart ? 1 : 0); command.Parameters.AddWithValue("$theme", Db(settings.Theme)); command.Parameters.AddWithValue("$log", Db(settings.LogLevel)); command.Parameters.AddWithValue("$password", Db(settings.AdminPasswordHash)); command.Parameters.AddWithValue("$localShare", settings.LocalShareEnabled ? 1 : 0); command.Parameters.AddWithValue("$shareMode", ShareDeliveryModes.Normalize(settings.ShareDeliveryMode)); command.ExecuteNonQuery();
                }
                SaveWaitingScreen(connection, transaction, 0, settings.LandscapeWaitingScreen);
                SaveWaitingScreen(connection, transaction, 1, settings.PortraitWaitingScreen);
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        static void SaveWaitingScreen(SqliteConnection connection, SqliteTransaction transaction, int orientation, WaitingScreenProfile profile)
        {
            // Old settings writers without profiles must not erase configured layouts.
            if (profile == null) return;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT OR REPLACE INTO WaitingScreenProfiles (Orientation,BackgroundAssetId,ShowLiveView,LiveViewX,LiveViewY,LiveViewAreaPercent,BackgroundZoom,BackgroundPanX,BackgroundPanY) VALUES($orientation,$asset,$show,$x,$y,$area,$zoom,$panX,$panY)";
                command.Parameters.AddWithValue("$orientation", orientation);
                command.Parameters.AddWithValue("$asset", Db(profile.BackgroundAssetId));
                command.Parameters.AddWithValue("$show", profile.ShowLiveView ? 1 : 0);
                command.Parameters.AddWithValue("$x", Clamp(profile.LiveViewX, 0, 100));
                command.Parameters.AddWithValue("$y", Clamp(profile.LiveViewY, 0, 100));
                command.Parameters.AddWithValue("$area", Clamp(profile.LiveViewAreaPercent, 5, 40));
                command.Parameters.AddWithValue("$zoom", Clamp(profile.BackgroundZoom, 100, 300));
                command.Parameters.AddWithValue("$panX", Clamp(profile.BackgroundPanX, -100, 100));
                command.Parameters.AddWithValue("$panY", Clamp(profile.BackgroundPanY, -100, 100));
                command.ExecuteNonQuery();
            }
        }

        static Guid[] ReadIds(string value) => (value ?? "").Split(',').Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToArray();
        static int NormalizeWaitingTimeout(int seconds) => new[] { 30, 60, 120, 300, 600, 900 }.Contains(seconds) ? seconds : 30;
        static int NormalizeRotation(int value) => value == 90 || value == -90 || value == 180 ? value : 0;
        static int Clamp(int value, int minimum, int maximum)
        {
            // GIF duration used to be 0.8–1.5 seconds. Keep existing SQL compact
            // while applying the new 0.4–1.0 second range on read and save.
            if (minimum == 800 && maximum == 1500) { minimum = 400; maximum = 1000; }
            return Math.Max(minimum, Math.Min(maximum, value));
        }
        static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
        static object Db(object value) => value == null ? DBNull.Value : value.ToString();
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        static Guid? GuidValue(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? (Guid?)null : Guid.Parse(reader.GetString(index));
    }
}
