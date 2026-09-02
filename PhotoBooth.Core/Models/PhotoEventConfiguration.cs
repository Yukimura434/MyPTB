using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public sealed class PhotoEventConfiguration
    {
        public Guid EventId { get; set; }
        public int PhotoCount { get; set; } = 1;
        public int CountdownSeconds { get; set; } = 3;
        public int GifFrameDurationMilliseconds { get; set; } = 1000;
        public int WaitingTimeoutSeconds { get; set; } = 30;
        public CustomerLayoutMode CustomerLayoutMode { get; set; } = CustomerLayoutMode.Landscape;
        public int ImageRotationDegrees { get; set; }
        public BeautySettings Beauty { get; set; } = new BeautySettings();
        public IReadOnlyList<Guid> FrameIds { get; set; } = new Guid[0];
        public IReadOnlyList<Guid> PresetIds { get; set; } = new Guid[0];
        public DateTime ModifiedAtUtc { get; set; }
        public long RowVersion { get; set; }
    }
}
