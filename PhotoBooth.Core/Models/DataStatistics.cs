using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public sealed class DataStatisticsSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public long SessionCount { get; set; }
        public long CaptureCount { get; set; }
        public long PictureCount { get; set; }
        public long VideoCount { get; set; }
        public long CompositeCount { get; set; }
        public long GifCount { get; set; }
        public long ShareArchiveCount { get; set; }
        public long ReadyAssetCount { get; set; }
        public long MissingAssetCount { get; set; }
        public long SuccessfulPrintCount { get; set; }
        public long FailedPrintCount { get; set; }
        public long PendingUploadCount { get; set; }
        public long UploadedCount { get; set; }
        public long FailedUploadCount { get; set; }
        public long TodayCaptureCount { get; set; }
        public long TodayPictureCount { get; set; }
        public long TodayPrintCount { get; set; }
        public long TotalAssetBytes { get; set; }
        public IReadOnlyList<RecentCaptureStatistics> RecentCaptures { get; set; }
    }

    public sealed class RecentCaptureStatistics
    {
        public string CaptureId { get; set; }
        public Guid SessionId { get; set; }
        public string SessionName { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime CreatedAtLocal => CreatedAtUtc.ToLocalTime();
        public string Status { get; set; }
        public int AssetCount { get; set; }
        public int VideoCount { get; set; }
        public int GifCount { get; set; }
        public int PrintCount { get; set; }
        public int MissingAssetCount { get; set; }
        public bool HasShareArchive { get; set; }
    }
}
