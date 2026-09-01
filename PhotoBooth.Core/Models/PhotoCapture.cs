using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public class PhotoCapture
    {
        public string Id { get; set; }
        public Guid SessionId { get; set; }
        public Guid? FrameId { get; set; }
        public string CompositeImageId { get; set; }
        public string CompositePath { get; set; }
        public string SharePath { get; set; }
        public string Status { get; set; }
        public string MediaMode { get; set; }
        public int UploadAttempts { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UploadedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public string LastError { get; set; }
        public IReadOnlyList<CapturePhoto> Photos { get; set; }
        public Guid BoothSessionId { get => SessionId; set => SessionId=value; }
        public IReadOnlyList<CapturePhoto> Assets { get => Photos; set => Photos=value; }
    }

    /// <summary>The finalized set of media offered, printed or delivered for one booth session.</summary>
    public sealed class Deliverable : PhotoCapture
    {
        public new IReadOnlyList<DeliverableAsset> Assets
        {
            get
            {
                var assets = new List<DeliverableAsset>();
                foreach (var photo in Photos ?? new CapturePhoto[0])
                {
                    var asset = photo as DeliverableAsset;
                    assets.Add(asset ?? new DeliverableAsset
                    {
                        Id = photo.Id,
                        DeliverableId = photo.CaptureId,
                        CapturedShotId = photo.CapturedImageId,
                        LocalPath = photo.LocalPath,
                        Role = photo.PhotoType,
                        Position = photo.Position,
                        MimeType = photo.MimeType,
                        FileLength = photo.FileLength,
                        ContentHashSha256 = photo.ContentHashSha256,
                        CreatedAtUtc = photo.CreatedAtUtc,
                        AssetStatus = photo.AssetStatus,
                        SourceAssetIds = photo.SourceAssetIds,
                        CloudinaryPublicId = photo.CloudinaryPublicId,
                        IsUploaded = photo.IsUploaded,
                        UploadAttempts = photo.UploadAttempts,
                        UploadedAtUtc = photo.UploadedAtUtc,
                        LastError = photo.LastError
                    });
                }
                return assets;
            }
            set
            {
                var photos = new List<CapturePhoto>();
                foreach (var asset in value ?? new DeliverableAsset[0]) photos.Add(asset);
                Photos = photos;
            }
        }
    }

    public class CapturePhoto
    {
        public string Id { get; set; }
        public string CaptureId { get; set; }
        public string CapturedImageId { get; set; }
        public string LocalPath { get; set; }
        public string PhotoType { get; set; }
        public int Position { get; set; }
        public string MimeType { get; set; }
        public long FileLength { get; set; }
        public string ContentHashSha256 { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string AssetStatus { get; set; }
        public IReadOnlyList<string> SourceAssetIds { get; set; }
        public string CloudinaryPublicId { get; set; }
        public bool IsUploaded { get; set; }
        public int UploadAttempts { get; set; }
        public DateTime? UploadedAtUtc { get; set; }
        public string LastError { get; set; }
        public string DeliverableId { get => CaptureId; set => CaptureId=value; }
        public string CapturedShotId { get => CapturedImageId; set => CapturedImageId=value; }
        public string Role { get => PhotoType; set => PhotoType=value; }
    }

    public sealed class DeliverableAsset : CapturePhoto { }

    public static class CaptureAssetTypes
    {
        public const string Picture = "Picture";
        public const string Video = "Video";
        public const string CompositeVideo = "CompositeVideo";
        public const string Composite = "Composite";
        public const string Gif = "Gif";
        public const string ShareArchive = "ShareArchive";
    }

    public static class DeliverableAssetRoles
    {
        public const string OriginalPicture = CaptureAssetTypes.Picture;
        public const string OriginalVideo = CaptureAssetTypes.Video;
        public const string CompositeVideo = CaptureAssetTypes.CompositeVideo;
        public const string FinalComposite = CaptureAssetTypes.Composite;
        public const string Gif = CaptureAssetTypes.Gif;
        public const string ShareArchive = CaptureAssetTypes.ShareArchive;
    }
}
