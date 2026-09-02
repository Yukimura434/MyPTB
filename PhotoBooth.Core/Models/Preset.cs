using System;

namespace PhotoBooth.Core.Models
{
    public sealed class Preset
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string SettingsJson { get; set; }
        public Guid? FrameId { get; set; }
        public Guid? PrinterProfileId { get; set; }
        public int CaptureCountdownSeconds { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ModifiedAtUtc { get; set; }
        public bool IsDefault { get; set; }
        public bool IsPinned { get; set; }
        public Guid? EventId { get; set; }
        public Guid LutAssetId { get; set; }
        public string LutDisplayName { get; set; }
        public int LutCubeSize { get; set; }
        public string LutStatus { get; set; }
        public long LutRowVersion { get; set; }
    }

    public sealed class PresetEvent
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
