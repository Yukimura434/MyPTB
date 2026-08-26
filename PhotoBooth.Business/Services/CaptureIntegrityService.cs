using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Services
{
    public sealed class CaptureIntegrityService : ICaptureIntegrityService
    {
        public Task ValidateAsync(PhotoCapture capture, CancellationToken token)
        {
            if (capture == null || string.IsNullOrWhiteSpace(capture.Id)) throw new InvalidDataException("Capture identity is unavailable.");
            var assets = (capture.Photos ?? new CapturePhoto[0]).Where(x => x != null && !string.Equals(x.PhotoType, CaptureAssetTypes.ShareArchive, StringComparison.OrdinalIgnoreCase)).ToList();
            if (assets.Count == 0 || assets.Any(x => string.IsNullOrWhiteSpace(x.Id) || !string.Equals(x.CaptureId, capture.Id, StringComparison.Ordinal))) throw new InvalidDataException("Capture asset ownership is incomplete.");
            foreach (var asset in assets) ValidateFile(asset, token);

            var pictures = OfType(assets, CaptureAssetTypes.Picture); var videos = OfType(assets, CaptureAssetTypes.Video);
            var composites = OfType(assets, CaptureAssetTypes.Composite); var compositeVideos = OfType(assets, CaptureAssetTypes.CompositeVideo);
            if (pictures.Count == 0 || composites.Count != 1) throw new InvalidDataException("Capture must contain original Pictures and one static Composite.");
            ValidateSources(composites[0], pictures);

            var mode = string.IsNullOrWhiteSpace(capture.MediaMode) ? CaptureMediaModes.PictureOnly : capture.MediaMode;
            if (string.Equals(mode, CaptureMediaModes.PictureAndVideo, StringComparison.Ordinal))
            {
                if (pictures.Count != videos.Count || compositeVideos.Count != 1) throw new InvalidDataException("PictureAndVideo capture has an incomplete asset pair.");
                var pictureIds = new HashSet<string>(pictures.Select(x => x.CapturedImageId), StringComparer.Ordinal);
                var videoIds = new HashSet<string>(videos.Select(x => x.CapturedImageId), StringComparer.Ordinal);
                if (pictureIds.Contains(null) || videoIds.Contains(null) || !pictureIds.SetEquals(videoIds)) throw new InvalidDataException("Picture and video identities do not match.");
                ValidateSourceSubset(compositeVideos[0], videos);
                foreach (var video in videos.Concat(compositeVideos)) ValidateVideo(video.LocalPath, token);
            }
            else if (videos.Count != 0 || compositeVideos.Count != 0) throw new InvalidDataException("PictureOnly capture contains unexpected video assets.");
            return Task.CompletedTask;
        }

        static List<CapturePhoto> OfType(IEnumerable<CapturePhoto> assets, string type) => assets.Where(x => string.Equals(x.PhotoType, type, StringComparison.OrdinalIgnoreCase)).ToList();
        static void ValidateSources(CapturePhoto derived, IReadOnlyList<CapturePhoto> expected)
        {
            var actual = new HashSet<string>(derived.SourceAssetIds ?? new string[0], StringComparer.Ordinal);
            var required = new HashSet<string>(expected.Select(x => x.Id), StringComparer.Ordinal);
            if (!actual.SetEquals(required)) throw new InvalidDataException(derived.PhotoType + " lineage is incomplete or points to the wrong asset type.");
        }
        static void ValidateSourceSubset(CapturePhoto derived, IReadOnlyList<CapturePhoto> allowed)
        {
            var actual = new HashSet<string>(derived.SourceAssetIds ?? new string[0], StringComparer.Ordinal);
            var valid = new HashSet<string>(allowed.Select(x => x.Id), StringComparer.Ordinal);
            if (actual.Count == 0 || !actual.IsSubsetOf(valid)) throw new InvalidDataException(derived.PhotoType + " lineage is empty or points to the wrong asset type.");
        }
        static void ValidateFile(CapturePhoto asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(asset.LocalPath) || !File.Exists(asset.LocalPath)) throw new FileNotFoundException("Capture asset file is missing.", asset.LocalPath);
            var info = new FileInfo(asset.LocalPath); if (info.Length <= 0 || info.Length != asset.FileLength) throw new InvalidDataException("Capture asset length has changed: " + asset.Id);
            if (!string.Equals(Hash(asset.LocalPath), asset.ContentHashSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Capture asset hash has changed: " + asset.Id);
        }
        static void ValidateVideo(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var bytes = File.ReadAllBytes(path);
            if (string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                if (bytes.Length < 12 || bytes[4] != (byte)'f' || bytes[5] != (byte)'t' || bytes[6] != (byte)'y' || bytes[7] != (byte)'p') throw new InvalidDataException("Video is not a valid MP4 file: " + path);
                return;
            }
            throw new InvalidDataException("Video must use the MP4 format: " + path);
        }
        static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();}
    }
}
