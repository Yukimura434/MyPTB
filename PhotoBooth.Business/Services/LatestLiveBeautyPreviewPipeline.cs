using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Services
{
    /// <summary>
    /// A capacity-one Beauty worker. Camera callers only submit frames; slow
    /// OpenCV work never delays the next camera request.
    /// </summary>
    public sealed class LatestLiveBeautyPreviewPipeline : ILiveBeautyPreviewPipeline
    {
        readonly object sync = new object();
        readonly ILiveBeautyPreviewService processor;
        BeautySettings settings = new BeautySettings();
        PendingFrame pending;
        long generation;
        bool running;
        bool failed;
        bool resetProcessor;

        public LatestLiveBeautyPreviewPipeline(ILiveBeautyPreviewService value)
        {
            processor = value ?? throw new ArgumentNullException(nameof(value));
        }

        public event EventHandler<LiveBeautyPreviewFrameEventArgs> FrameReady;
        public event EventHandler<LiveBeautyPreviewErrorEventArgs> Failed;

        public void UpdateSettings(BeautySettings value)
        {
            lock (sync)
            {
                settings = value?.Clone() ?? new BeautySettings();
                generation++;
                failed = false;
                if (!settings.HasEffect) pending = null;
                resetProcessor = running;
            }
            processor.Reset();
        }

        public void Submit(LiveViewFrame frame, CancellationToken token)
        {
            if (frame?.ImageData == null || frame.ImageData.Length == 0 || token.IsCancellationRequested) return;

            var publishRaw = false;
            lock (sync)
            {
                if (!settings.HasEffect || failed) publishRaw = true;
                else
                {
                    pending = new PendingFrame(frame, token, generation);
                    if (running) return;
                    running = true;
                }
            }

            if (publishRaw) Publish(frame, false, 0d);
            else _ = ProcessLoopAsync();
        }

        public void Reset()
        {
            lock (sync)
            {
                generation++;
                pending = null;
                settings = new BeautySettings();
                failed = false;
                resetProcessor = running;
            }
            processor.Reset();
        }

        async Task ProcessLoopAsync()
        {
            while (true)
            {
                PendingFrame item;
                BeautySettings snapshot;
                lock (sync)
                {
                    item = pending;
                    pending = null;
                    if (item == null) { running = false; return; }
                    snapshot = settings.Clone();
                }

                if (item.Token.IsCancellationRequested) continue;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var output = await processor.ProcessAsync(item.Frame.ImageData, snapshot, item.Token).ConfigureAwait(false);
                    var elapsed = ElapsedMilliseconds(started);
                    var current = false;
                    lock (sync) current = item.Generation == generation && settings.HasEffect && !failed;
                    if (!current || item.Token.IsCancellationRequested) continue;
                    Publish(CloneWithImage(item.Frame, output ?? item.Frame.ImageData), true, elapsed);
                }
                catch (OperationCanceledException) when (item.Token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    var current = false;
                    lock (sync)
                    {
                        current = item.Generation == generation;
                        if (current) { failed = true; pending = null; }
                    }
                    if (!current) continue;
                    Failed?.Invoke(this, new LiveBeautyPreviewErrorEventArgs(error));
                    Publish(item.Frame, false, ElapsedMilliseconds(started));
                }

                var resetNow = false;
                lock (sync) if (resetProcessor) { resetProcessor = false; resetNow = true; }
                if (resetNow) processor.Reset();
            }
        }

        void Publish(LiveViewFrame frame, bool beautyApplied, double elapsedMilliseconds)
        {
            FrameReady?.Invoke(this, new LiveBeautyPreviewFrameEventArgs(frame, beautyApplied, elapsedMilliseconds));
        }

        static LiveViewFrame CloneWithImage(LiveViewFrame source, byte[] image)
        {
            return new LiveViewFrame
            {
                ImageData = image,
                Width = source.Width,
                Height = source.Height,
                Rotation = source.Rotation,
                FocusX = source.FocusX,
                FocusY = source.FocusY,
                IsFocused = source.IsFocused,
                TimestampUtc = source.TimestampUtc
            };
        }

        static double ElapsedMilliseconds(long started) =>
            (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

        sealed class PendingFrame
        {
            internal PendingFrame(LiveViewFrame frame, CancellationToken token, long generation)
            {
                Frame = frame; Token = token; Generation = generation;
            }
            internal LiveViewFrame Frame { get; }
            internal CancellationToken Token { get; }
            internal long Generation { get; }
        }
    }
}
