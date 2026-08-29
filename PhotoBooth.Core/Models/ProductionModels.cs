using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public sealed class PresetProcessingOptions
    {
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float Saturation { get; set; }
        public float Gamma { get; set; } = 1f;
        public float Exposure { get; set; }
        public float Temperature { get; set; }
        public float Tint { get; set; }
        public float Sharpen { get; set; }
        public float Blur { get; set; }
        public float Vignette { get; set; }
        public bool BlackAndWhite { get; set; }
        public bool Sepia { get; set; }
        public string WatermarkPath { get; set; }
        public float WatermarkOpacity { get; set; } = .65f;
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
        public int Dpi { get; set; } = 300;
    }

    public enum PrintJobStatus { Queued, Printing, Completed, Failed, Cancelled }
    public sealed class PrintQueueItem
    {
        public PrintJob Job { get; set; }
        public PrintJobStatus Status { get; set; }
        public int Attempts { get; set; }
        public int MaximumAttempts { get; set; } = 3;
        public string Error { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }

    public sealed class UploadResult
    {
        public bool Succeeded { get; set; }
        public Uri DownloadUri { get; set; }
        public string ProviderReference { get; set; }
        public string Error { get; set; }
    }

    public enum ComponentHealth { Healthy, Degraded, Unavailable }
    public sealed class HealthSnapshot
    {
        public DateTime TimestampUtc { get; set; }
        public ComponentHealth Camera { get; set; }
        public ComponentHealth Printer { get; set; }
        public ComponentHealth Storage { get; set; }
        public ComponentHealth Queue { get; set; }
        public long AvailableDiskBytes { get; set; }
        public long ManagedMemoryBytes { get; set; }
        public long WorkingSetBytes { get; set; }
        public long PrivateMemoryBytes { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public bool Is64BitProcess { get; set; }
        public double CpuPercent { get; set; }
        public double LiveViewFps { get; set; }
        public int PendingPrintJobs { get; set; }
        public string CameraSdkVersion { get; set; }
        public IReadOnlyList<string> Messages { get; set; }
    }
}
