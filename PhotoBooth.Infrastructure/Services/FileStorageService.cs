using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class FileStorageService : IFileStorageService
    {
        private readonly string _root;
        private readonly string _rootPrefix;
        public FileStorageService(ApplicationOptions options)
        {
            _root = Path.GetFullPath(options.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _rootPrefix = _root + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_root);
        }
        public async Task<string> SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken)
        {
            var path = Resolve(relativePath); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var output = File.Create(path)) await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            return path;
        }
        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult<Stream>(File.OpenRead(Resolve(relativePath)));
        private string Resolve(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidOperationException("Storage path must be relative to the application data root.");
            var path = Path.GetFullPath(Path.Combine(_root, relativePath));
            if (!path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path escapes storage root.");
            return path;
        }
    }
}
