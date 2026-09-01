using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public sealed class DataStatisticsSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public long BoothSessionCount { get; set; }
        public long DeliverableCount { get; set; }
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
        public long TodayDeliverableCount { get; set; }
        public long TodayBoothSessionCount { get; set; }
        public long TodayPictureCount { get; set; }
        public long TodayPrintCount { get; set; }
        public long TotalAssetBytes { get; set; }
        public IReadOnlyList<RecentDeliverableStatistics> RecentDeliverables { get; set; }
        public long SessionCount { get => BoothSessionCount; set => BoothSessionCount=value; }
        public long CaptureCount { get => DeliverableCount; set => DeliverableCount=value; }
        public long TodayCaptureCount { get => TodayDeliverableCount; set => TodayDeliverableCount=value; }
        public IReadOnlyList<RecentDeliverableStatistics> RecentCaptures { get => RecentDeliverables; set => RecentDeliverables=value; }
    }

    public class RecentDeliverableStatistics
    {
        public string DeliverableId { get; set; }
        public Guid BoothSessionId { get; set; }
        public string EventName { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime CreatedAtLocal => CreatedAtUtc.ToLocalTime();
        public string Status { get; set; }
        public int AssetCount { get; set; }
        public int VideoCount { get; set; }
        public int GifCount { get; set; }
        public int PrintCount { get; set; }
        public int MissingAssetCount { get; set; }
        public bool HasShareArchive { get; set; }
        public string CaptureId { get => DeliverableId; set => DeliverableId=value; }
        public Guid SessionId { get => BoothSessionId; set => BoothSessionId=value; }
        public string SessionName { get => EventName; set => EventName=value; }
    }

    public sealed class RecentCaptureStatistics : RecentDeliverableStatistics { }

    public static class CaptureLibraryFilterModes
    {
        public const string All = "All";
        public const string Date = "Date";
        public const string Event = "Event";
        public const string Session = "Session";
    }

    public sealed class CaptureLibraryFilter
    {
        public string Mode { get; set; } = CaptureLibraryFilterModes.All;
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        public string Query { get; set; }
        public int MaximumItems { get; set; } = 250;
    }

    public sealed class CaptureLibrarySnapshot
    {
        public long CaptureCount { get; set; }
        public long PrintedPhotoCount { get; set; }
        public long ExtraPrintCount { get; set; }
        public decimal RevenueAmount { get; set; }
        public bool HasRevenueData { get; set; }
        public IReadOnlyList<CaptureLibraryItem> Captures { get; set; }
    }

    public sealed class CaptureLibraryItem
    {
        public string CaptureId { get; set; }
        public Guid SessionId { get; set; }
        public string SessionDisplayCode { get; set; }
        public string EventName { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime CreatedAtLocal => CreatedAtUtc.ToLocalTime();
        public string Status { get; set; }
        public int AssetCount { get; set; }
        public int PictureCount { get; set; }
        public int VideoCount { get; set; }
        public int GifCount { get; set; }
        public int PrintedPhotoCount { get; set; }
        public int ExtraPrintCount { get; set; }
        public string ThumbnailPath { get; set; }
        public string ThumbnailManagedRelativePath { get; set; }
    }

    public sealed class CaptureLibraryMedia
    {
        public string AssetId { get; set; }
        public string CaptureId { get; set; }
        public string Role { get; set; }
        public int Position { get; set; }
        public string LocalPath { get; set; }
        public string ManagedRelativePath { get; set; }
        public string MimeType { get; set; }
        public string AssetStatus { get; set; }
    }
}
