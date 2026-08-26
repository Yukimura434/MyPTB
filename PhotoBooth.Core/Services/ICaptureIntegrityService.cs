using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface ICaptureIntegrityService
    {
        Task ValidateAsync(PhotoCapture capture, CancellationToken token);
    }
}
