using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CaptureFocusServiceTests
    {
        [Fact]
        public async Task Focus_failure_is_fail_open()
        {
            var service=new CaptureFocusService(new FailingLiveView(),NullLogger<CaptureFocusService>.Instance);

            var focused=await service.TryFocusAsync("camera",CancellationToken.None);

            Assert.False(focused);
        }

        [Fact]
        public async Task Unresponsive_focus_returns_after_bounded_timeout()
        {
            var service=new CaptureFocusService(new HangingLiveView(),NullLogger<CaptureFocusService>.Instance);
            var elapsed=Stopwatch.StartNew();

            var focused=await service.TryFocusAsync("camera",CancellationToken.None);

            Assert.False(focused);
            Assert.InRange(elapsed.Elapsed,TimeSpan.FromSeconds(1.5),TimeSpan.FromSeconds(3));
        }

        sealed class FailingLiveView:LiveViewStub
        {
            public override Task FocusAsync(string cameraId,int x,int y,CancellationToken token)=>Task.FromException(new InvalidOperationException("expected focus failure"));
        }

        sealed class HangingLiveView:LiveViewStub
        {
            readonly TaskCompletionSource<bool> never=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public override Task FocusAsync(string cameraId,int x,int y,CancellationToken token)=>never.Task;
        }

        abstract class LiveViewStub:ILiveViewService
        {
            public Task StartAsync(string cameraId,CancellationToken token)=>Task.CompletedTask;
            public Task StopAsync(string cameraId,CancellationToken token)=>Task.CompletedTask;
            public Task<LiveViewFrame> GetFrameAsync(string cameraId,CancellationToken token)=>Task.FromResult<LiveViewFrame>(null);
            public abstract Task FocusAsync(string cameraId,int x,int y,CancellationToken token);
        }
    }
}
