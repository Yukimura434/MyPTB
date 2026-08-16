using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Cameras;

namespace PhotoBooth.Core.Services
{
    public interface ILiveViewService
    {
        Task StartAsync(string cameraId, CancellationToken cancellationToken);
        Task<LiveViewFrame> GetFrameAsync(string cameraId, CancellationToken cancellationToken);
        Task FocusAsync(string cameraId, int x, int y, CancellationToken cancellationToken);
        Task StopAsync(string cameraId, CancellationToken cancellationToken);
    }
}
