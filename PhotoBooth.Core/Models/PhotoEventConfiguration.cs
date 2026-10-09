using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public enum EventPrintPolicyMode { Disabled, Manual, Automatic }

    public sealed class PhotoEventConfiguration
    {
        public Guid EventId { get; set; }
        public long BasePriceMinor { get; set; }
        public long AdditionalPrintPriceMinor { get; set; }
        public string Currency { get; set; } = "VND";
        public int PhotoCount { get; set; } = 8;
        public int CountdownSeconds { get; set; } = 5;
        public int GifFrameDurationMilliseconds { get; set; } = 800;
        public int WaitingTimeoutSeconds { get; set; } = 60;
        public CustomerLayoutMode CustomerLayoutMode { get; set; } = CustomerLayoutMode.Landscape;
        public int ImageRotationDegrees { get; set; }
        public BeautySettings Beauty { get; set; } = new BeautySettings();
        public EventPrintPolicyMode PrintPolicyMode { get; set; } = EventPrintPolicyMode.Manual;
        public int IncludedPrintCopies { get; set; } = 1;
        public int MaximumPrintCopies { get; set; } = 10;
        public int ColorPresetStrengthPercent { get; set; } = 50;
        public IReadOnlyList<Guid> FrameIds { get; set; } = new Guid[0];
        public IReadOnlyList<Guid> PresetIds { get; set; } = new Guid[0];
        public DateTime ModifiedAtUtc { get; set; }
        public long RowVersion { get; set; }
    }
}
