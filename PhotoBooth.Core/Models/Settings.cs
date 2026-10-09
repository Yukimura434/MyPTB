using System;

namespace PhotoBooth.Core.Models
{
    public enum CustomerLayoutMode { Landscape = 0, Portrait = 1 }
    public static class ShareDeliveryModes
    {
        public const string CloudOnly = "CloudOnly";
        public const string LanOnly = "LanOnly";
        public const string CloudWithLanFallback = "CloudWithLanFallback";
        public static string Normalize(string value) => value == LanOnly || value == CloudWithLanFallback ? value : CloudOnly;
    }

    public sealed class Settings
    {
        public Guid[] SelectedFrameCollectionIds { get; set; } = new Guid[0];
        public Guid[] SelectedPresetCollectionIds { get; set; } = new Guid[0];
        public bool BoothSettingsConfigured { get; set; }
        public string Culture { get; set; }
        public string CaptureDirectory { get; set; }
        public byte TransparentAlphaThreshold { get; set; } = 8;
        public int MinimumSlotArea { get; set; } = 10000;
        public int MinimumSlotWidth { get; set; } = 40;
        public int MinimumSlotHeight { get; set; } = 40;
        public bool IgnoreBorderTransparency { get; set; } = true;
        public int MaximumFrameSlots { get; set; } = 8;
        public int PhotoCount { get; set; } = 8;
        public int CountdownSeconds { get; set; } = 5;
        public int DelayBetweenShotsSeconds { get; set; } = 1;
        public bool CreateGif { get; set; } = true;
        public bool CreateVideo { get; set; } = true;
        public int GifFrameDurationMilliseconds { get; set; } = 800;
        public int WaitingTimeoutSeconds { get; set; } = 60;
        public bool AutoFlip { get; set; }
        public string PreferredCameraId { get; set; }
        public int ImageRotationDegrees { get; set; }
        public CustomerLayoutMode CustomerLayoutMode { get; set; } = CustomerLayoutMode.Landscape;
        public WaitingScreenProfile LandscapeWaitingScreen { get; set; }
        public WaitingScreenProfile PortraitWaitingScreen { get; set; }
        public WaitingScreenProfile GetWaitingScreen(bool portrait, Guid? legacyBackgroundId)
        {
            var profile = portrait ? PortraitWaitingScreen : LandscapeWaitingScreen;
            if (profile != null) return profile.Copy();
            return new WaitingScreenProfile
            {
                BackgroundAssetId = legacyBackgroundId, ShowLiveView = ShowWaitingLiveView,
                LiveViewX = WaitingLiveViewX, LiveViewY = WaitingLiveViewY,
                LiveViewAreaPercent = WaitingLiveViewAreaPercent, BackgroundZoom = WaitingBackgroundZoom,
                BackgroundPanX = WaitingBackgroundPanX, BackgroundPanY = WaitingBackgroundPanY
            };
        }
        public bool ShowWaitingLiveView { get; set; } = true;
        public double WaitingLiveViewX { get; set; } = 10;
        public double WaitingLiveViewY { get; set; } = 10;
        public double WaitingLiveViewAreaPercent { get; set; } = 5;
        public double WaitingBackgroundZoom { get; set; } = 100;
        public double WaitingBackgroundPanX { get; set; }
        public double WaitingBackgroundPanY { get; set; }
        public PhotoBooth.Core.Cameras.CameraSaveMode SaveLocation { get; set; } = PhotoBooth.Core.Cameras.CameraSaveMode.PcOnly;
        public Guid? DefaultFrameId { get; set; }
        public Guid? DefaultPresetId { get; set; }
        public Guid? LiveViewPresetId { get; set; }
        public int LiveViewPresetStrengthPercent { get; set; } = 50;
        public Guid? DefaultPrinterProfileId { get; set; }
        public bool PrintingEnabled { get; set; } = true;
        public bool KioskMode { get; set; } = true;
        public bool KeepFinalPrintedImage { get; set; } = true;
        public int SessionRetentionDays { get; set; } = 30;
        public int BoothSessionRetentionDays { get => SessionRetentionDays; set => SessionRetentionDays=value; }
        public int TemporaryFileRetentionHours { get; set; } = 24;
        public int PrintRetryCount { get; set; } = 3;
        public int CameraReconnectSeconds { get; set; } = 5;
        public bool EnableQr { get; set; } = true;
        public bool LocalShareEnabled { get; set; } = true;
        public string ShareDeliveryMode { get; set; } = ShareDeliveryModes.CloudWithLanFallback;
        public bool EnablePlugins { get; set; } = true;
        public bool EnableDiagnostics { get; set; } = true;
        public bool EnableTelemetry { get; set; }
        public bool AutoStart { get; set; }
        public string Theme { get; set; } = "System";
        public string LogLevel { get; set; } = "Information";
        public string AdminPasswordHash { get; set; }
    }
}
