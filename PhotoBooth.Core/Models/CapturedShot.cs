using System;

namespace PhotoBooth.Core.Models
{
    public sealed class CapturedShot
    {
        public string Id { get; set; }
        public int Sequence { get; set; }
        public string PicturePath { get; set; }
        public string VideoPath { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public bool HasVideo => !string.IsNullOrWhiteSpace(VideoPath);
    }

    public static class CaptureMediaModes
    {
        public const string PictureOnly = "PictureOnly";
        public const string PictureAndVideo = "PictureAndVideo";
    }
}
