using System;
using System.Linq;
using CameraControl.Devices;

namespace PhotoBooth.Infrastructure.Cameras
{
    internal sealed class CameraDeviceResolver
    {
        private readonly CameraDeviceManager _manager;
        public CameraDeviceResolver(CameraDeviceManager manager) { _manager = manager; }
        public ICameraDevice GetRequired(string cameraId)
        {
            var camera = _manager.ConnectedDevices.FirstOrDefault(x => CameraId(x) == cameraId);
            if (camera == null) throw new InvalidOperationException("Camera not found: " + cameraId);
            return camera;
        }
        public static string CameraId(ICameraDevice camera) =>
            camera.SerialNumber ?? camera.PortName ?? camera.DeviceName ?? camera.GetType().FullName;
    }
}
