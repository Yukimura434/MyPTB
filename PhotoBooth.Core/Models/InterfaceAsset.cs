using System;

namespace PhotoBooth.Core.Models
{
    public sealed class InterfaceAsset
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string FilePath { get; set; }
        public bool IsAnimated { get; set; }
        public bool IsSelected { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
