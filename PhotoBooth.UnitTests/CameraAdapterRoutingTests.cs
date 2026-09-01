using CameraControl.Devices.Canon;
using CameraControl.Devices.Classes;
using CameraControl.Devices.Nikon;
using CameraControl.Devices.Others;
using CameraControl.Devices.Sony;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoBooth.Infrastructure.Cameras;
using Xunit;
using System.Threading;
using System.Threading.Tasks;

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

        [Fact]
        public async Task Nikon_adapter_does_not_republish_cached_frame_before_next_mtp_slot()
        {
            using (var gate = new CameraOperationGate())
            {
                var adapter = new NikonMtpCameraAdapter(gate, NullLogger<NikonMtpCameraAdapter>.Instance);
                var camera = new CountingNikonCamera();
                var first = await adapter.GetLiveViewFrameAsync(camera, CancellationToken.None);
                var duplicate = await adapter.GetLiveViewFrameAsync(camera, CancellationToken.None);

                Assert.NotNull(first);
                Assert.Null(duplicate);
                Assert.Equal(1, camera.LiveViewRequests);

                await Task.Delay(45);
                Assert.NotNull(await adapter.GetLiveViewFrameAsync(camera, CancellationToken.None));
                Assert.Equal(2, camera.LiveViewRequests);
            }
        }

        sealed class CountingNikonCamera : NikonBase
        {
            public int LiveViewRequests { get; private set; }
            public override LiveViewData GetLiveViewImage()
            {
                LiveViewRequests++;
                return new LiveViewData { ImageData = new byte[] { 1, 2, 3 }, ImageDataPosition = 0 };
            }
        }
    }
}
