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

    public static class MediaReplicaProviders
    {
        public const string Local = "Local";
        // Transitional intent before the Worker returns the session's snapshotted destination.
        public const string Online = "Online";
        public const string SystemLibrary = "SystemLibrary";
        public const string GoogleDrive = "GoogleDrive";

        public static string FromApi(string value)
        {
            if(string.Equals(value,"google_drive",StringComparison.OrdinalIgnoreCase)||string.Equals(value,"drive",StringComparison.OrdinalIgnoreCase))return GoogleDrive;
            if(string.Equals(value,"system_library",StringComparison.OrdinalIgnoreCase)||string.Equals(value,"r2",StringComparison.OrdinalIgnoreCase))return SystemLibrary;
            return Online;
        }
    }

    public static class MediaReplicaStates
    {
        public const string Present = "Present";
        public const string Queued = "Queued";
        public const string Uploading = "Uploading";
        public const string Verified = "Verified";
        public const string Failed = "Failed";
        public const string Missing = "Missing";
        public const string Deleting = "Deleting";
        public const string Deleted = "Deleted";
    }

    public sealed class MediaAssetReplicaRecord
    {
        public string Id { get; set; }
        public string AssetId { get; set; }
        public string Provider { get; set; }
        public bool IsRequired { get; set; }
        public string PolicyReason { get; set; }
        public string State { get; set; }
        public string RemoteReference { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? NextAttemptAtUtc { get; set; }
        public string LastErrorCategory { get; set; }
        public string LastError { get; set; }
        public string ContentHashSha256 { get; set; }
        public long FileLength { get; set; }
        public DateTime? VerifiedAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
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

    public static class DurableOutputJobTypes
    {
        public const string Print = "Print";
        public const string UploadAsset = "UploadAsset";
        public const string CommitDeliverable = "CommitDeliverable";
        public const string CreateGallery = "CreateGallery";
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
        public string DeliverableId { get; set; }
        public string AssetId { get; set; }
        public string JobType { get; set; }
        public string IdempotencyKey { get; set; }
        public string State { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? NextRetryAtUtc { get; set; }
        public string ExternalReference { get; set; }
        public string LastError { get; set; }
        // DPAPI-protected employee/session credential used only by this delivery chain.
        public string ProtectedAuthorization { get; set; }
        public string AuthorizationScope { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>A recoverable, idempotent export of one finalized booth deliverable into its Event folder.</summary>
    public sealed class EventExportJobRecord
    {
        public string Id { get; set; }
        public Guid EventId { get; set; }
        public Guid SessionId { get; set; }
        public string DeliverableId { get; set; }
        public string DestinationRoot { get; set; }
        public string TurnFolderName { get; set; }
        public string IdempotencyKey { get; set; }
        public string State { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? NextRetryAtUtc { get; set; }
        public string ExportedPath { get; set; }
        public string LastError { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class GalleryLinkCacheRecord
    {
        public string DeliverableId { get; set; }
        public Uri PublicUrl { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
    }
}
