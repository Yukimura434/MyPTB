using System.Collections.Generic;

namespace PhotoBooth.Core.Cameras
{
    public interface ICameraEngine
    {
        IReadOnlyList<CameraInfo> GetCameras();
    }
}
