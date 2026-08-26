using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public sealed class PhotoCapture
    {
        public string Id { get; set; }
        public Guid SessionId { get; set; }
        public Guid? FrameId { get; set; }
        public string CompositeImageId { get; set; }
        public string CompositePath { get; set; }
        public string SharePath { get; set; }
        public string Status { get; set; }
        public string MediaMode { get; set; }
        public int UploadAttempts { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UploadedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public string LastError { get; set; }
        public IReadOnlyList<CapturePhoto> Photos { get; set; }
    }

    public sealed class CapturePhoto
    {
        public string Id { get; set; }
        public string CaptureId { get; set; }
        public string CapturedImageId { get; set; }
        public string LocalPath { get; set; }
        public string PhotoType { get; set; }
        public int Position { get; set; }
        public string MimeType { get; set; }
        public long FileLength { get; set; }
        public string ContentHashSha256 { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string AssetStatus { get; set; }
        public IReadOnlyList<string> SourceAssetIds { get; set; }
        public string CloudinaryPublicId { get; set; }
        public bool IsUploaded { get; set; }
        public int UploadAttempts { get; set; }
        public DateTime? UploadedAtUtc { get; set; }
        public string LastError { get; set; }
    }

    public static class CaptureAssetTypes
    {
        public const string Picture = "Picture";
        public const string MotionPhoto = "MotionPhoto";
        public const string MotionPhotoComposite = "MotionPhotoComposite";
        public const string Composite = "Composite";
        public const string Gif = "Gif";
        public const string ShareArchive = "ShareArchive";
    }
}
