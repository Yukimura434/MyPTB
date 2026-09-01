using System;

namespace PhotoBooth.Core.Models
{
    public sealed class CapturedShot
    {
        public string Id { get; set; }
        public int Sequence { get; set; }
        public string PicturePath { get; set; }
        public string VideoPath { get; set; }
        public string PictureAssetId { get; set; }
        public string VideoAssetId { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public bool HasVideo => !string.IsNullOrWhiteSpace(VideoPath);
    }

    public sealed class PendingCapture
    {
        public string Id { get; set; }
        public int Sequence { get; set; }
        public string RawPicturePath { get; set; }
        public string PicturePath { get; set; }
        public string VideoPath { get; set; }
        public string VideoSnapshotPath { get; set; }
        public string PictureAssetId { get; set; }
        public string VideoAssetId { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public bool FlipHorizontally { get; set; }
        public int RotationDegrees { get; set; }
        public int VideoDurationSeconds { get; set; }
        public Guid? ColorPresetId { get; set; }
        public BeautySettings BeautySettings { get; set; }
        public bool IsTracked { get; set; }
        public bool IsProcessed { get; set; }
        public bool IsFinalized { get; set; }
    }

    public static class CaptureMediaModes
    {
        public const string PictureOnly = "PictureOnly";
        public const string PictureAndVideo = "PictureAndVideo";
    }
}
