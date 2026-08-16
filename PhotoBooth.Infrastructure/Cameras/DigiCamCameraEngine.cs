using System.Collections.Generic;
using System.Linq;
using CameraControl.Devices;
using PhotoBooth.Core.Cameras;

namespace PhotoBooth.Infrastructure.Cameras
{
    public sealed class DigiCamCameraEngine : ICameraEngine
    {
        private readonly CameraDeviceManager _manager;

        public DigiCamCameraEngine(CameraDeviceManager manager)
        {
            _manager = manager;
        }

        public IReadOnlyList<CameraInfo> GetCameras()
        {
            return _manager.ConnectedDevices
                .Select(camera => new CameraInfo(
                    CameraDeviceResolver.CameraId(camera),
                    camera.DisplayName ?? camera.DeviceName,
                    camera.IsConnected))
                .ToList();
        }
    }
}
