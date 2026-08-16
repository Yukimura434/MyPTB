using System;

namespace PhotoBooth.Core.Models
{
    public sealed class PhotoBoothSession
    {
        public Guid Id { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string OutputDirectory { get; set; }
    }
}
