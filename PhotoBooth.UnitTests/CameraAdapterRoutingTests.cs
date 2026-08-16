using CameraControl.Devices.Canon;
using CameraControl.Devices.Nikon;
using CameraControl.Devices.Others;
using CameraControl.Devices.Sony;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoBooth.Infrastructure.Cameras;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CameraAdapterRoutingTests
    {
        [Fact]
        public void Registry_routes_each_driver_family_to_its_protocol_adapter()
        {
            using (var gate = new CameraOperationGate())
            {
                var canon = new CanonEdsCameraAdapter(gate, NullLogger<CanonEdsCameraAdapter>.Instance);
                var nikon = new NikonMtpCameraAdapter(gate, NullLogger<NikonMtpCameraAdapter>.Instance);
                var sony = new SonyRemoteCameraAdapter(gate, NullLogger<SonyRemoteCameraAdapter>.Instance);
                var generic = new GenericCameraAdapter(gate, NullLogger<GenericCameraAdapter>.Instance);
                var registry = new CameraAdapterRegistry(new IPhotoBoothCameraAdapter[] { canon, nikon, sony, generic });

                Assert.Same(canon, registry.Resolve(new CanonSDKBase()));
                Assert.Same(nikon, registry.Resolve(new NikonZ7()));
                Assert.Same(sony, registry.Resolve(new SonyWifiCamera()));
                Assert.Same(generic, registry.Resolve(new FakeCameraDevice()));
            }
        }
    }
}
