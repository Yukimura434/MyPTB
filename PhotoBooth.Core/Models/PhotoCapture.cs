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
        public string CloudinaryPublicId { get; set; }
        public bool IsUploaded { get; set; }
        public int UploadAttempts { get; set; }
        public DateTime? UploadedAtUtc { get; set; }
        public string LastError { get; set; }
    }
}
