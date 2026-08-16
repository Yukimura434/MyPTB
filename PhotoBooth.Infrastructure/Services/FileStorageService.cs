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
        public FileStorageService(ApplicationOptions options) { _root = Path.GetFullPath(options.DataDirectory); Directory.CreateDirectory(_root); }
        public async Task<string> SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken)
        {
            var path = Resolve(relativePath); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var output = File.Create(path)) await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            return path;
        }
        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult<Stream>(File.OpenRead(Resolve(relativePath)));
        private string Resolve(string relativePath)
        {
            var path = Path.GetFullPath(Path.Combine(_root, relativePath));
            if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path escapes storage root.");
            return path;
        }
    }
}
