using System;
using System.Threading;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public sealed class LiveBeautyPreviewFrameEventArgs : EventArgs
    {
        public LiveBeautyPreviewFrameEventArgs(LiveViewFrame frame, bool beautyApplied, double processingMilliseconds)
        {
            Frame = frame;
            BeautyApplied = beautyApplied;
            ProcessingMilliseconds = processingMilliseconds;
        }

        public LiveViewFrame Frame { get; }
        public bool BeautyApplied { get; }
        public double ProcessingMilliseconds { get; }
    }

    public sealed class LiveBeautyPreviewErrorEventArgs : EventArgs
    {
        public LiveBeautyPreviewErrorEventArgs(Exception error) { Error = error; }
        public Exception Error { get; }
    }

    /// <summary>
    /// Keeps camera acquisition independent from preview retouching. When Beauty
    /// cannot keep up, only the newest pending frame is processed.
    /// </summary>
    public interface ILiveBeautyPreviewPipeline
    {
        event EventHandler<LiveBeautyPreviewFrameEventArgs> FrameReady;
        event EventHandler<LiveBeautyPreviewErrorEventArgs> Failed;
        void UpdateSettings(BeautySettings settings);
        void Submit(LiveViewFrame frame, CancellationToken token);
        void Reset();
    }
}
