using System;
using System.Threading;
using System.Threading.Tasks;
using CameraControl.Devices.Classes;
using CameraControl.Devices.Canon;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Services;
using PhotoBooth.Infrastructure.Cameras;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class LiveViewService : ILiveViewService
    {
        private readonly CameraDeviceResolver _resolver;
        private readonly CameraAdapterRegistry _adapters;
        private readonly ILogger<LiveViewService> _logger;
        public LiveViewService(CameraDeviceResolver resolver, CameraAdapterRegistry adapters, ILogger<LiveViewService> logger) { _resolver = resolver; _adapters = adapters; _logger = logger; }
        public async Task StartAsync(string cameraId, CancellationToken cancellationToken)
        {
            try
            {
                var camera = _resolver.GetRequired(cameraId);
                await _adapters.Resolve(camera).StartLiveViewAsync(camera, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Live View started for camera {CameraId}", cameraId);
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Camera rejected Live View start for camera {CameraId}", cameraId);
                throw;
            }
        }
        public Task StopAsync(string cameraId, CancellationToken cancellationToken) { var camera = _resolver.GetRequired(cameraId); return _adapters.Resolve(camera).StopLiveViewAsync(camera, cancellationToken); }
        public Task FocusAsync(string cameraId, int x, int y, CancellationToken cancellationToken) { var camera = _resolver.GetRequired(cameraId); return _adapters.Resolve(camera).FocusAsync(camera, x, y, cancellationToken); }
        public Task<LiveViewFrame> GetFrameAsync(string cameraId, CancellationToken cancellationToken)
        {
            var camera = _resolver.GetRequired(cameraId);
            return GetFrame(camera, cancellationToken);
        }
        async Task<LiveViewFrame> GetFrame(CameraControl.Devices.ICameraDevice camera, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!camera.IsConnected || camera.IsBusy) return null;
            var data = await _adapters.Resolve(camera).GetLiveViewFrameAsync(camera, cancellationToken).ConfigureAwait(false);
            if (data == null) return null;
            // Canon's EosConverter already allocates and owns a fresh byte[] for
            // every EVF download. Reusing that array avoids a second full-frame
            // allocation/copy; other adapters retain the defensive detach.
            var image = ExtractImage(data, camera is CanonSDKBase);
            if (image == null || image.Length == 0) return null;
            return new LiveViewFrame { ImageData = image, Width = data.ImageWidth > 0 ? data.ImageWidth : data.LiveViewImageWidth, Height = data.ImageHeight > 0 ? data.ImageHeight : data.LiveViewImageHeight, Rotation = data.Rotation, FocusX = data.FocusX, FocusY = data.FocusY, IsFocused = data.Focused, TimestampUtc = DateTime.UtcNow };
        }
        private static byte[] ExtractImage(LiveViewData data, bool ownsCompleteBuffer)
        {
            // Some webcam drivers reuse LiveViewData and replace ImageData while the
            // next frame is arriving. Snapshot the array reference once so length,
            // bounds validation and BlockCopy all describe the same buffer.
            var source = data.ImageData;
            if (source == null) return null;
            var offset = Math.Max(0, data.ImageDataPosition);
            if (offset >= source.Length) return null;
            if (ownsCompleteBuffer && offset == 0) return source;
            // Camera SDKs may reuse their live-view buffer immediately after this call.
            // Always detach the frame before WPF decodes it on another thread.
            var result = new byte[source.Length - offset];
            Buffer.BlockCopy(source, offset, result, 0, result.Length);
            return result;
        }
    }
}
