using System.Collections.Generic;

namespace PhotoBooth.Core.Cameras
{
    public sealed class CameraProperty
    {
        public CameraPropertyKind Kind { get; set; }
        public string Value { get; set; }
        public long NumericValue { get; set; }
        public IReadOnlyList<string> AllowedValues { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsReadOnly { get; set; }
    }
}
