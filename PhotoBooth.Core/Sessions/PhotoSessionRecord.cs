using System;

namespace PhotoBooth.Core.Sessions
{
    public sealed class PhotoSessionRecord
    {
        public Guid Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string OutputDirectory { get; set; }
    }
}
