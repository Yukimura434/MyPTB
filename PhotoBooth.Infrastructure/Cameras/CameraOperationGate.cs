using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CameraControl.Devices;
using CameraControl.Devices.Nikon;
using CameraControl.Devices.Sony;

namespace PhotoBooth.Infrastructure.Cameras
{
    /// <summary>
    /// Runs Canon EDSDK and Nikon WPD on separate dedicated STA dispatchers.
    /// Each retains the Windows message pump required by its native callback/COM
    /// transport without allowing one camera protocol to block the other.
    /// </summary>
    internal sealed class CameraOperationGate : IDisposable
    {
        readonly SemaphoreSlim staSemaphore = new SemaphoreSlim(1, 1), mtpSemaphore = new SemaphoreSlim(1, 1);
        readonly ManualResetEventSlim staReady = new ManualResetEventSlim(false), mtpReady = new ManualResetEventSlim(false);
        readonly Thread staThread, mtpThread;
        Dispatcher staDispatcher, mtpDispatcher;
        bool disposed;
        static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

        public CameraOperationGate()
        {
            staThread = new Thread(() => ThreadMain(true)) { IsBackground = true, Name = "PhotoBooth Canon EDSDK" };
            staThread.SetApartmentState(ApartmentState.STA);
            // Windows Portable Device is a COM API. The reference application
            // creates Nikon MTP on the WPF STA and relies on its message pump.
            // Use a dedicated STA so Nikon preserves that invariant without
            // sharing or blocking Canon's EDSDK dispatcher.
            mtpThread = new Thread(() => ThreadMain(false)) { IsBackground = true, Name = "PhotoBooth Nikon WPD" };
            mtpThread.SetApartmentState(ApartmentState.STA);
            staThread.Start(); mtpThread.Start(); staReady.Wait(); mtpReady.Wait();
        }

        void ThreadMain(bool sta)
        {
            if (sta) { staDispatcher = Dispatcher.CurrentDispatcher; staReady.Set(); }
            else { mtpDispatcher = Dispatcher.CurrentDispatcher; mtpReady.Set(); }
            Dispatcher.Run();
        }

        public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken token)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            ThrowIfDisposed();
            return await RunOnAsync(staDispatcher, staSemaphore, operation, token).ConfigureAwait(false);
        }

        public Task<T> RunMtpAsync<T>(Func<T> operation, CancellationToken token) => RunOnAsync(mtpDispatcher, mtpSemaphore, operation, token);
        public Task RunMtpAsync(Action operation, CancellationToken token) => RunMtpAsync(() => { operation(); return true; }, token);
        public Task RunAsync(ICameraDevice camera, Action operation, CancellationToken token) => IsMtpCamera(camera) ? RunMtpAsync(operation, token) : RunAsync(operation, token);
        public Task<T> RunAsync<T>(ICameraDevice camera, Func<T> operation, CancellationToken token) => IsMtpCamera(camera) ? RunMtpAsync(operation, token) : RunAsync(operation, token);
        static bool IsMtpCamera(ICameraDevice camera) => camera is NikonBase || camera is BaseMTPCamera || camera is SonyWifiCamera;

        public Task RunAsync(Action operation, CancellationToken token)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return RunAsync(() => { operation(); return true; }, token);
        }

        public void Run(Action operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            RunAsync(operation, CancellationToken.None).GetAwaiter().GetResult();
        }

        async Task<T> RunOnAsync<T>(Dispatcher target, SemaphoreSlim semaphore, Func<T> operation, CancellationToken token)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            // Adapter operations may compose transfer and cleanup while already
            // executing on their owning SDK context. Do not reacquire the same
            // non-reentrant semaphore in that case.
            if (target.CheckAccess()) return operation();
            await semaphore.WaitAsync(token).ConfigureAwait(false);
            try { return await InvokeAsync(target, operation, token).ConfigureAwait(false); }
            finally { semaphore.Release(); }
        }

        async Task<T> InvokeAsync<T>(Dispatcher target, Func<T> operation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (target.CheckAccess()) return operation();

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = target.BeginInvoke(new Action(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    completion.TrySetResult(operation());
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception error) { completion.TrySetException(error); }
            }), DispatcherPriority.Normal);

            using (var timeout = new CancellationTokenSource())
            {
                timeout.CancelAfter(OperationTimeout);
                var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
                if (await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false) == timeoutTask)
                {
                    completion.TrySetCanceled();
                    throw new OperationCanceledException("Camera SDK operation timed out.", token);
                }
                timeout.Cancel();
                return await completion.Task.ConfigureAwait(false);
            }
        }

        void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CameraOperationGate));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (staDispatcher != null && !staDispatcher.HasShutdownStarted) staDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            if (mtpDispatcher != null && !mtpDispatcher.HasShutdownStarted) mtpDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            staSemaphore.Dispose(); mtpSemaphore.Dispose(); staReady.Dispose(); mtpReady.Dispose();
        }
    }
}
