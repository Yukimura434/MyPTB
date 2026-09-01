using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Services
{
    public sealed class CaptureFocusService : ICaptureFocusService
    {
        static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(2);
        readonly ILiveViewService liveView;
        readonly ILogger<CaptureFocusService> log;

        public CaptureFocusService(ILiveViewService liveView, ILogger<CaptureFocusService> log)
        {
            this.liveView = liveView;
            this.log = log;
        }

        public async Task<bool> TryFocusAsync(string cameraId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(cameraId)) return false;

            using (var focusCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task focusTask;
                try { focusTask = liveView.FocusAsync(cameraId, 0, 0, focusCancellation.Token); }
                catch (Exception error)
                {
                    log?.LogWarning(error, "Camera focus failed before capture; capture will continue for {CameraId}", cameraId);
                    return false;
                }
                if (focusTask == null)
                {
                    log?.LogWarning("Camera focus returned no operation; capture will continue for {CameraId}", cameraId);
                    return false;
                }

                var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
                var completed = await Task.WhenAny(focusTask, Task.Delay(FocusTimeout), cancelled).ConfigureAwait(false);
                if (completed == cancelled) cancellationToken.ThrowIfCancellationRequested();
                if (completed == focusTask)
                {
                    try
                    {
                        await focusTask.ConfigureAwait(false);
                        return true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        log?.LogWarning(error, "Camera focus failed before capture; capture will continue for {CameraId}", cameraId);
                        return false;
                    }
                }

                focusCancellation.Cancel();
                log?.LogWarning("Camera focus exceeded {TimeoutMilliseconds}ms; capture will continue for {CameraId}", FocusTimeout.TotalMilliseconds, cameraId);
                _ = focusTask.ContinueWith(
                    task => log?.LogWarning(task.Exception, "Timed-out camera focus later failed for {CameraId}", cameraId),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return false;
            }
        }
    }
}
