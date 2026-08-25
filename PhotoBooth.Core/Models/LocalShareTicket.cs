using System;

namespace PhotoBooth.Core.Models
{
    public sealed class LocalShareTicket
    {
        public string Id { get; set; }
        public Guid SessionId { get; set; }
        public string CaptureId { get; set; }
        public string ArchiveAssetId { get; set; }
        public Uri DownloadUrl { get; set; }
        public string ZipPath { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }
}
