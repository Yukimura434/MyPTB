using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class MediaThumbnailService : IMediaThumbnailService
    {
        public Task<byte[]> CreateAsync(string filePath, int maximumPixels, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Media path is required.", nameof(filePath));
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(filePath)) throw new FileNotFoundException("Media file was not found.", filePath);
                var mime = MimeType(filePath);
                if (mime == null) throw new NotSupportedException("This media format cannot be previewed.");
                var bytes = LocalShareService.CreateThumbnailBytes(filePath, mime, maximumPixels);
                token.ThrowIfCancellationRequested();
                return bytes;
            }, token);
        }

        static string MimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".bmp": return "image/bmp";
                case ".gif": return "image/gif";
                case ".mp4": return "video/mp4";
                default: return null;
            }
        }
    }
}
