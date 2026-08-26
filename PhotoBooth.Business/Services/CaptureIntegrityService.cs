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

            var pictures = OfType(assets, CaptureAssetTypes.Picture); var motions = OfType(assets, CaptureAssetTypes.MotionPhoto);
            var composites = OfType(assets, CaptureAssetTypes.Composite); var motionComposites = OfType(assets, CaptureAssetTypes.MotionPhotoComposite);
            if (pictures.Count == 0 || composites.Count != 1) throw new InvalidDataException("Capture must contain original Pictures and one static Composite.");
            ValidateSources(composites[0], pictures);

            var mode = string.IsNullOrWhiteSpace(capture.MediaMode) ? CaptureMediaModes.PictureOnly : capture.MediaMode;
            if (string.Equals(mode, CaptureMediaModes.PictureAndMotion, StringComparison.Ordinal))
            {
                if (pictures.Count != motions.Count || motionComposites.Count != 1) throw new InvalidDataException("PictureAndMotion capture has an incomplete asset pair.");
                var pictureIds = new HashSet<string>(pictures.Select(x => x.CapturedImageId), StringComparer.Ordinal);
                var motionIds = new HashSet<string>(motions.Select(x => x.CapturedImageId), StringComparer.Ordinal);
                if (pictureIds.Contains(null) || motionIds.Contains(null) || !pictureIds.SetEquals(motionIds)) throw new InvalidDataException("Picture and Motion Photo identities do not match.");
                ValidateSources(motionComposites[0], motions);
                foreach (var motion in motions.Concat(motionComposites)) ValidateMotionPhoto(motion.LocalPath, token);
            }
            else if (motions.Count != 0 || motionComposites.Count != 0) throw new InvalidDataException("PictureOnly capture contains unexpected Motion Photo assets.");
            return Task.CompletedTask;
        }

        static List<CapturePhoto> OfType(IEnumerable<CapturePhoto> assets, string type) => assets.Where(x => string.Equals(x.PhotoType, type, StringComparison.OrdinalIgnoreCase)).ToList();
        static void ValidateSources(CapturePhoto derived, IReadOnlyList<CapturePhoto> expected)
        {
            var actual = new HashSet<string>(derived.SourceAssetIds ?? new string[0], StringComparer.Ordinal);
            var required = new HashSet<string>(expected.Select(x => x.Id), StringComparer.Ordinal);
            if (!actual.SetEquals(required)) throw new InvalidDataException(derived.PhotoType + " lineage is incomplete or points to the wrong asset type.");
        }
        static void ValidateFile(CapturePhoto asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(asset.LocalPath) || !File.Exists(asset.LocalPath)) throw new FileNotFoundException("Capture asset file is missing.", asset.LocalPath);
            var info = new FileInfo(asset.LocalPath); if (info.Length <= 0 || info.Length != asset.FileLength) throw new InvalidDataException("Capture asset length has changed: " + asset.Id);
            if (!string.Equals(Hash(asset.LocalPath), asset.ContentHashSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Capture asset hash has changed: " + asset.Id);
        }
        static void ValidateMotionPhoto(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var bytes = File.ReadAllBytes(path); var text = Encoding.ASCII.GetString(bytes);
            if ((text.IndexOf("Camera:MotionPhoto", StringComparison.Ordinal) < 0 && text.IndexOf("GCamera:MicroVideo", StringComparison.Ordinal) < 0) || text.IndexOf("ftyp", StringComparison.Ordinal) < 0) throw new InvalidDataException("Motion Photo is missing XMP or its MP4 payload: " + path);
        }
        static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();}
    }
}
