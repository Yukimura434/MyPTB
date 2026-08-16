using System;

namespace PhotoBooth.Core.Cameras
{
    public sealed class CaptureResult
    {
        public bool Succeeded { get; set; }
        public string CameraId { get; set; }
        public string FileName { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public string Error { get; set; }
        public string ImageId { get; set; }
    }
}
