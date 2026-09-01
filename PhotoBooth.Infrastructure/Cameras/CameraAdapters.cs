using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CameraControl.Devices;
using CameraControl.Devices.Canon;
using CameraControl.Devices.Classes;
using CameraControl.Devices.Nikon;
using CameraControl.Devices.Sony;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Cameras;

namespace PhotoBooth.Infrastructure.Cameras
{
    internal enum CameraRuntimeState { Discovered, Initializing, Ready, LiveViewing, Capturing, Transferring, Closing, Disconnected, Faulted }

    internal interface IPhotoBoothCameraAdapter
    {
        string Brand { get; }
        bool CanHandle(ICameraDevice camera);
        CameraRuntimeState GetState(ICameraDevice camera);
        Task WaitUntilReadyAsync(ICameraDevice camera, CancellationToken token);
        Task StartLiveViewAsync(ICameraDevice camera, CancellationToken token);
        Task StopLiveViewAsync(ICameraDevice camera, CancellationToken token);
        Task<LiveViewData> GetLiveViewFrameAsync(ICameraDevice camera, CancellationToken token);
        Task FocusAsync(ICameraDevice camera, int x, int y, CancellationToken token);
        Task BeginCaptureAsync(ICameraDevice camera, bool autoFocus, CameraSaveMode saveMode, CancellationToken token);
        Task TransferAsync(ICameraDevice camera, object handle, string destination, CancellationToken token);
        Task CompleteTransferAsync(ICameraDevice camera, object handle, bool succeeded, CancellationToken token);
        Task RecoverCaptureAsync(ICameraDevice camera, CancellationToken token);
        Task DisconnectAsync(CameraDeviceManager manager, ICameraDevice camera, CancellationToken token);
    }

    internal sealed class CameraAdapterRegistry
    {
        readonly IPhotoBoothCameraAdapter[] adapters;
        public CameraAdapterRegistry(System.Collections.Generic.IEnumerable<IPhotoBoothCameraAdapter> adapters) { this.adapters = System.Linq.Enumerable.ToArray(adapters); }
        public IPhotoBoothCameraAdapter Resolve(ICameraDevice camera)
        {
            foreach (var adapter in adapters) if (adapter.CanHandle(camera)) return adapter;
            throw new NotSupportedException("No PhotoBooth adapter for camera type " + camera.GetType().FullName);
        }
    }

    internal abstract class CameraAdapterBase : IPhotoBoothCameraAdapter
    {
        readonly ConcurrentDictionary<ICameraDevice, CameraRuntimeState> states = new ConcurrentDictionary<ICameraDevice, CameraRuntimeState>();
        protected readonly CameraOperationGate Operations;
        protected readonly ILogger Logger;
        protected CameraAdapterBase(CameraOperationGate operations, ILogger logger) { Operations = operations; Logger = logger; }
        public abstract string Brand { get; }
        public abstract bool CanHandle(ICameraDevice camera);
        protected abstract Task RunAsync(ICameraDevice camera, Action action, CancellationToken token);
        protected abstract Task<T> RunAsync<T>(ICameraDevice camera, Func<T> action, CancellationToken token);
        public CameraRuntimeState GetState(ICameraDevice camera) => states.TryGetValue(camera, out var state) ? state : CameraRuntimeState.Discovered;

        protected async Task Execute(ICameraDevice camera, string operation, CameraRuntimeState state, Action action, CancellationToken token)
        {
            var before = GetState(camera); states[camera] = state; var watch = Stopwatch.StartNew(); int operationThread = 0; ApartmentState apartment = ApartmentState.Unknown;
            try { await RunAsync(camera, () => { operationThread = Thread.CurrentThread.ManagedThreadId; apartment = Thread.CurrentThread.GetApartmentState(); action(); }, token).ConfigureAwait(false); Logger.LogInformation("Camera protocol {Brand} {Camera} {Operation}: {Before}->{After}, {Elapsed}ms, thread {Thread}, apartment {Apartment}", Brand, CameraDeviceResolver.CameraId(camera), operation, before, state, watch.ElapsedMilliseconds, operationThread, apartment); }
            catch (Exception error) { states[camera] = CameraRuntimeState.Faulted; Logger.LogError(error, "Camera protocol {Brand} {Camera} {Operation} failed after {Elapsed}ms", Brand, CameraDeviceResolver.CameraId(camera), operation, watch.ElapsedMilliseconds); throw; }
        }

        public virtual async Task WaitUntilReadyAsync(ICameraDevice camera, CancellationToken token)
        {
            states[camera] = CameraRuntimeState.Initializing; var deadline = DateTime.UtcNow.AddSeconds(12);
            while (camera.IsConnected && camera.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(100, token).ConfigureAwait(false);
            if (!camera.IsConnected) throw new InvalidOperationException(Brand + " camera disconnected during initialization.");
            if (camera.IsBusy) throw new TimeoutException(Brand + " camera property initialization did not complete.");
            states[camera] = CameraRuntimeState.Ready;
        }
        public virtual async Task StartLiveViewAsync(ICameraDevice camera, CancellationToken token) { await WaitUntilReadyAsync(camera, token).ConfigureAwait(false); await Execute(camera, "StartLiveView", CameraRuntimeState.LiveViewing, camera.StartLiveView, token).ConfigureAwait(false); }
        public virtual Task StopLiveViewAsync(ICameraDevice camera, CancellationToken token) => Execute(camera, "StopLiveView", CameraRuntimeState.Ready, camera.StopLiveView, token);
        public virtual Task<LiveViewData> GetLiveViewFrameAsync(ICameraDevice camera, CancellationToken token)
        {
            // EDSDK delivers the captured-file handle on the same STA context used
            // by Live View. Do not let another frame request occupy that context
            // while the capture callback is waiting to download/release its file.
            var state = GetState(camera);
            if (state == CameraRuntimeState.Capturing || state == CameraRuntimeState.Transferring || state == CameraRuntimeState.Closing || state == CameraRuntimeState.Disconnected)
                return Task.FromResult<LiveViewData>(null);
            return RunAsync(camera, camera.GetLiveViewImage, token);
        }
        public virtual Task FocusAsync(ICameraDevice camera, int x, int y, CancellationToken token) => Execute(camera, "Focus", GetState(camera), () => { if (x == 0 && y == 0) camera.AutoFocus(); else camera.Focus(x, y); }, token);
        public virtual Task BeginCaptureAsync(ICameraDevice camera, bool autoFocus, CameraSaveMode saveMode, CancellationToken token) => Execute(camera, "Capture", CameraRuntimeState.Capturing, () => { camera.IsBusy = true; if (autoFocus) camera.CapturePhoto(); else camera.CapturePhotoNoAf(); }, token);
        public virtual Task TransferAsync(ICameraDevice camera, object handle, string destination, CancellationToken token) => Execute(camera, "Transfer", CameraRuntimeState.Transferring, () => camera.TransferFile(handle, destination), token);
        public virtual Task CompleteTransferAsync(ICameraDevice camera, object handle, bool succeeded, CancellationToken token) { camera.IsBusy = false; states[camera] = CameraRuntimeState.Ready; return Task.CompletedTask; }
        public virtual Task RecoverCaptureAsync(ICameraDevice camera, CancellationToken token) { camera.IsBusy = false; states[camera] = CameraRuntimeState.Ready; return Task.CompletedTask; }
        public virtual async Task DisconnectAsync(CameraDeviceManager manager, ICameraDevice camera, CancellationToken token)
        {
            try { await Execute(camera, "Disconnect", CameraRuntimeState.Disconnected, () => manager.DisconnectCamera(camera), token).ConfigureAwait(false); }
            finally { Forget(camera); }
        }
        protected virtual void Forget(ICameraDevice camera) { states.TryRemove(camera, out _); }
    }

    internal sealed class CanonEdsCameraAdapter : CameraAdapterBase
    {
        public CanonEdsCameraAdapter(CameraOperationGate operations, ILogger<CanonEdsCameraAdapter> logger) : base(operations, logger) { }
        public override string Brand => "Canon EDSDK";
        public override bool CanHandle(ICameraDevice camera) => camera is CanonSDKBase;
        protected override Task RunAsync(ICameraDevice camera, Action action, CancellationToken token) => Operations.RunAsync(action, token);
        protected override Task<T> RunAsync<T>(ICameraDevice camera, Func<T> action, CancellationToken token) => Operations.RunAsync(action, token);
        public override Task BeginCaptureAsync(ICameraDevice camera, bool autoFocus, CameraSaveMode saveMode, CancellationToken token)
        {
            return Execute(camera, "Capture", CameraRuntimeState.Capturing, () => { var canon = (CanonSDKBase)camera; if (saveMode == CameraSaveMode.PcAndCard) canon.Camera.SavePicturesToHostAndCamera(System.IO.Path.GetTempPath()); else canon.CaptureInSdRam = true; camera.IsBusy = true; if (autoFocus) camera.CapturePhoto(); else camera.CapturePhotoNoAf(); }, token);
        }
        public override async Task CompleteTransferAsync(ICameraDevice camera, object handle, bool succeeded, CancellationToken token) { await Operations.RunAsync(() => { var canon = (CanonSDKBase)camera; if (succeeded) canon.ReleaseResurce(handle); else canon.CancelTransfer(handle); }, token).ConfigureAwait(false); await base.CompleteTransferAsync(camera, handle, succeeded, token).ConfigureAwait(false); }
    }

    internal sealed class NikonMtpCameraAdapter : CameraAdapterBase
    {
        readonly ConcurrentDictionary<ICameraDevice, DateTime> nextFrame = new ConcurrentDictionary<ICameraDevice, DateTime>();
        public NikonMtpCameraAdapter(CameraOperationGate operations, ILogger<NikonMtpCameraAdapter> logger) : base(operations, logger) { }
        public override string Brand => "Nikon MTP";
        public override bool CanHandle(ICameraDevice camera) => camera is NikonBase;
        protected override Task RunAsync(ICameraDevice camera, Action action, CancellationToken token) => Operations.RunMtpAsync(action, token);
        protected override Task<T> RunAsync<T>(ICameraDevice camera, Func<T> action, CancellationToken token) => Operations.RunMtpAsync(action, token);
        public override async Task<LiveViewData> GetLiveViewFrameAsync(ICameraDevice camera, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            // Do not return the cached LiveViewData while waiting for the next MTP
            // slot. LiveViewService would defensively copy that same JPEG on every
            // 1 ms poll, only for the UI loop to discard it as a duplicate.
            if (nextFrame.TryGetValue(camera, out var due) && now < due) return null;
            nextFrame[camera] = now.AddTicks(TimeSpan.TicksPerSecond / 30); // At most 30 MTP preview commands/sec.
            return await base.GetLiveViewFrameAsync(camera, token).ConfigureAwait(false);
        }
        public override async Task RecoverCaptureAsync(ICameraDevice camera, CancellationToken token) { await Operations.RunMtpAsync(() => ((NikonBase)camera).ResetTimer(), token).ConfigureAwait(false); await base.RecoverCaptureAsync(camera, token).ConfigureAwait(false); }
        protected override void Forget(ICameraDevice camera) { nextFrame.TryRemove(camera, out _); base.Forget(camera); }
    }

    internal sealed class SonyRemoteCameraAdapter : CameraAdapterBase
    {
        public SonyRemoteCameraAdapter(CameraOperationGate operations, ILogger<SonyRemoteCameraAdapter> logger) : base(operations, logger) { }
        public override string Brand => "Sony Remote API";
        public override bool CanHandle(ICameraDevice camera) => camera is SonyWifiCamera;
        protected override Task RunAsync(ICameraDevice camera, Action action, CancellationToken token) => Operations.RunMtpAsync(action, token);
        protected override Task<T> RunAsync<T>(ICameraDevice camera, Func<T> action, CancellationToken token) => Operations.RunMtpAsync(action, token);
    }

    internal sealed class GenericCameraAdapter : CameraAdapterBase
    {
        public GenericCameraAdapter(CameraOperationGate operations, ILogger<GenericCameraAdapter> logger) : base(operations, logger) { }
        public override string Brand => "Generic";
        public override bool CanHandle(ICameraDevice camera) => !(camera is CanonSDKBase) && !(camera is NikonBase) && !(camera is SonyWifiCamera);
        protected override Task RunAsync(ICameraDevice camera, Action action, CancellationToken token) => Operations.RunAsync(action, token);
        protected override Task<T> RunAsync<T>(ICameraDevice camera, Func<T> action, CancellationToken token) => Operations.RunAsync(action, token);
    }
}
