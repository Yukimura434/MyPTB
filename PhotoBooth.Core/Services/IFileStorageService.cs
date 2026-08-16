using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Services
{
    public interface IFileStorageService
    {
        Task<string> SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken);
        Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
    }
}
