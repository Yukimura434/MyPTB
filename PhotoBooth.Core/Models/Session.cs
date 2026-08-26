using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public sealed class Session
    {
        public Guid Id { get; set; }
        public Guid? PresetId { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string OutputDirectory { get; set; }
        public IReadOnlyList<string> CapturedFiles { get; set; }
        public IReadOnlyList<string> CapturedVideoFiles { get; set; }
        public IReadOnlyList<CapturedShot> CapturedShots { get; set; }
        public string FinalImagePath { get; set; }
        public string SessionName { get; set; }
        public int SessionNumber { get; set; }
        public IReadOnlyList<string> CapturedImageIds { get; set; }
        public bool IsDefault { get; set; }
        public int CaptureIndex { get; set; }
        public int FrameIndex { get; set; }
        public string FinalImageId { get; set; }
        public override string ToString() => SessionName ?? string.Empty;
    }
}
