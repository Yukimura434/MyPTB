using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Cameras;

namespace PhotoBooth.Core.Services
{
    public interface ICameraService
    {
        event System.EventHandler CamerasChanged;
        Task<IReadOnlyList<CameraInfo>> ScanAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken cancellationToken);
        Task ConnectAsync(string cameraId, CancellationToken cancellationToken);
        Task ConnectAsync(CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
        Task<CameraProperties> GetPropertiesAsync(string cameraId, CancellationToken cancellationToken);
        Task SetPropertyAsync(string cameraId, CameraPropertyKind property, string value, CancellationToken cancellationToken);
        Task<CaptureResult> CaptureAsync(string cameraId, bool autoFocus, CancellationToken cancellationToken);
        Task<CaptureResult> CaptureAsync(string cameraId, bool autoFocus, string destinationBasePath, CancellationToken cancellationToken);
        Task<CaptureResult> CaptureAsync(string cameraId, bool autoFocus, string destinationBasePath, CameraSaveMode saveMode, CancellationToken cancellationToken);
    }
}
