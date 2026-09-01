using System;

namespace PhotoBooth.Core.Models
{
    public static class SessionKinds
    {
        public const string Event = "Event";
        public const string Booth = "Booth";
    }

    public static class BoothSessionStates
    {
        public const string Active = "Active";
        public const string Finalizing = "Finalizing";
        public const string Completed = "Completed";
        public const string Abandoned = "Abandoned";
        public const string Failed = "Failed";
    }

    public static class CaptureAttemptStates
    {
        public const string IntentRecorded = "IntentRecorded";
        public const string Accepted = "Accepted";
        public const string Failed = "Failed";
        public const string Unknown = "Unknown";
    }

    public static class MediaAssetKinds
    {
        public const string OriginalPicture = "OriginalPicture";
        public const string OriginalVideo = "OriginalVideo";
        public const string FinalComposite = "FinalComposite";
        public const string FinalVideo = "FinalVideo";
        public const string Gif = "Gif";
        public const string ShareArchive = "ShareArchive";
    }

    public static class MediaAssetStates
    {
        public const string Staging = "Staging";
        public const string Ready = "Ready";
        public const string Missing = "Missing";
        public const string PendingDelete = "PendingDelete";
        public const string Deleted = "Deleted";
    }

    public static class MediaRetentionClasses
    {
        public const string Work = "Work";
        public const string Original = "Original";
        public const string Deliverable = "Deliverable";
    }

    public static class DurableOutputJobStates
    {
        public const string Pending = "Pending";
        public const string Leased = "Leased";
        public const string Submitting = "Submitting";
        public const string Submitted = "Submitted";
        public const string Completed = "Completed";
        public const string RetryWaiting = "RetryWaiting";
        public const string UnknownOutcome = "UnknownOutcome";
        public const string PermanentFailure = "PermanentFailure";
        public const string Cancelled = "Cancelled";
    }

    public sealed class CaptureAttemptRecord
    {
        public string Id { get; set; }
        public Guid SessionId { get; set; }
        public int Sequence { get; set; }
        public int AttemptNumber { get; set; }
        public string CameraId { get; set; }
        public string PictureAssetId { get; set; }
        public string VideoAssetId { get; set; }
        public string Status { get; set; }
        public DateTime IntentAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string LastError { get; set; }
    }

    public sealed class MediaAssetRecord
    {
        public string Id { get; set; }
        public Guid SessionId { get; set; }
        public string CaptureAttemptId { get; set; }
        public string Kind { get; set; }
        public string RelativePath { get; set; }
        public string MimeType { get; set; }
        public long FileLength { get; set; }
        public string ContentHashSha256 { get; set; }
        public string Status { get; set; }
        public string RetentionClass { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class SessionStoragePaths
    {
        public string Root { get; set; }
        public string Work { get; set; }
        public string Originals { get; set; }
        public string Final { get; set; }
    }

    public sealed class DurableOutputJobRecord
    {
        public string Id { get; set; }
        public Guid SessionId { get; set; }
        public string AssetId { get; set; }
        public string JobType { get; set; }
        public string IdempotencyKey { get; set; }
        public string State { get; set; }
        public int AttemptCount { get; set; }
        public string LastError { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
