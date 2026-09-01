using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class LatestLiveBeautyPreviewPipelineTests
    {
        [Fact]
        public async Task Disabled_beauty_publishes_raw_frame_immediately()
        {
            var processor = new ControlledProcessor();
            var pipeline = new LatestLiveBeautyPreviewPipeline(processor);
            LiveBeautyPreviewFrameEventArgs ready = null;
            pipeline.FrameReady += (s, e) => ready = e;
            pipeline.UpdateSettings(new BeautySettings { Enabled = false, SmoothSkin = 100 });
            var frame = Frame(1);

            pipeline.Submit(frame, CancellationToken.None);

            Assert.NotNull(ready);
            Assert.Same(frame, ready.Frame);
            Assert.False(ready.BeautyApplied);
            Assert.Equal(0, processor.Calls);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task Enabled_beauty_processes_only_latest_pending_frame()
        {
            var processor = new ControlledProcessor();
            var pipeline = new LatestLiveBeautyPreviewPipeline(processor);
            var outputs = new List<byte>();
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pipeline.FrameReady += (s, e) =>
            {
                lock (outputs) outputs.Add(e.Frame.ImageData[0]);
                if (e.Frame.ImageData[0] == 103) completed.TrySetResult(true);
            };
            pipeline.UpdateSettings(new BeautySettings { Enabled = true, SmoothSkin = 20 });

            pipeline.Submit(Frame(1), CancellationToken.None);
            await processor.FirstCallStarted.Task;
            pipeline.Submit(Frame(2), CancellationToken.None);
            pipeline.Submit(Frame(3), CancellationToken.None);
            processor.ReleaseFirst.TrySetResult(true);
            await completed.Task;

            lock (outputs) Assert.Equal(new byte[] { 101, 103 }, outputs.ToArray());
            Assert.Equal(2, processor.Calls);
        }

        [Fact]
        public async Task Disabling_beauty_discards_in_flight_result_and_publishes_new_raw_frame()
        {
            var processor = new ControlledProcessor();
            var pipeline = new LatestLiveBeautyPreviewPipeline(processor);
            var outputs = new List<byte>();
            pipeline.FrameReady += (s, e) => { lock (outputs) outputs.Add(e.Frame.ImageData[0]); };
            pipeline.UpdateSettings(new BeautySettings { Enabled = true, SmoothSkin = 20 });
            pipeline.Submit(Frame(1), CancellationToken.None);
            await processor.FirstCallStarted.Task;

            pipeline.UpdateSettings(new BeautySettings());
            pipeline.Submit(Frame(2), CancellationToken.None);
            processor.ReleaseFirst.TrySetResult(true);
            await Task.Delay(50);

            lock (outputs) Assert.Equal(new byte[] { 2 }, outputs.ToArray());
        }

        static LiveViewFrame Frame(byte value) => new LiveViewFrame
        {
            ImageData = new[] { value }, Width = 1, Height = 1, TimestampUtc = DateTime.UtcNow
        };

        sealed class ControlledProcessor : ILiveBeautyPreviewService
        {
            int calls;
            public int Calls => Volatile.Read(ref calls);
            public TaskCompletionSource<bool> FirstCallStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> ReleaseFirst { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<byte[]> ProcessAsync(byte[] jpegData, BeautySettings settings, CancellationToken token)
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    FirstCallStarted.TrySetResult(true);
                    await ReleaseFirst.Task;
                }
                token.ThrowIfCancellationRequested();
                return new[] { (byte)(jpegData[0] + 100) };
            }

            public void Reset() { }
        }
    }
}
