using System;

namespace PhotoBooth.Core.Models
{
    public sealed class LocalShareTicket
    {
        public Uri DownloadUrl { get; set; }
        public string ZipPath { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }
}
