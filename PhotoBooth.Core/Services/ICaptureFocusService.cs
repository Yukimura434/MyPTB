using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Services
{
    public interface ICaptureFocusService
    {
        Task<bool> TryFocusAsync(string cameraId, CancellationToken cancellationToken);
    }
}
