using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public sealed class Frame
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string SourcePath { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public IReadOnlyList<FrameSlot> Slots { get; set; }
        public string ThumbnailPath { get; set; }
        public bool IsPinned { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
