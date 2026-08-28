using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CameraControl.Devices;
using CameraControl.Devices.Canon;
using CameraControl.Devices.Classes;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Services;
using PhotoBooth.Infrastructure.Cameras;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
  internal sealed class CameraService : ICameraService
  {
    // Discovery timeouts (milliseconds)
    private const int CanonDiscoveryTimeout = 8000;
    private const int NikonDiscoveryTimeout = 5000;
    private const int SonyDiscoveryTimeout = 2000;
    private const int CaptureTimeout = 15; // seconds
    private const int TransferCleanupWaitIterations = 20;
    private const int TransferCleanupDelayMs = 100;

    // Dependencies
    private readonly CameraDeviceManager manager;
    private readonly CameraDeviceResolver resolver;
    private readonly CameraOperationGate operations;
    private readonly CameraAdapterRegistry adapters;
    private readonly ILogger<CameraService> logger;
    private readonly string captureRoot;

    // Lifecycle management
    private readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);

    // Concurrent state tracking
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CaptureResult>> pending =
      new ConcurrentDictionary<string, TaskCompletionSource<CaptureResult>>();
    private readonly ConcurrentDictionary<string, string> destinations =
      new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, byte> transfers =
      new ConcurrentDictionary<string, byte>();

    public event EventHandler CamerasChanged;
    public CameraService(
      CameraDeviceManager manager,
      CameraDeviceResolver resolver,
      CameraOperationGate operations,
      CameraAdapterRegistry adapters,
      ILogger<CameraService> logger,
      ApplicationOptions options)
    {
      this.manager = manager;
      this.resolver = resolver;
      this.operations = operations;
      this.adapters = adapters;
      this.logger = logger;
      captureRoot = Path.Combine(options.DataDirectory, "Captures");
      Directory.CreateDirectory(captureRoot);

      // Register for camera lifecycle events
      manager.CameraConnected += OnCameraChanged;
      manager.CameraInitialized += OnCameraChanged;
      manager.CameraDisconnected += OnCameraChanged;
      manager.DiscoveryChanged += OnDiscoveryChanged;
      manager.PhotoCaptured += OnPhotoCaptured;

      // Bridge camera SDK logging to application logger
      CameraControl.Devices.Log.LogDebug += e => logger.LogDebug(e.Exception, "Camera SDK: {Message}", e.Message);
      CameraControl.Devices.Log.LogInfo += e => logger.LogInformation(e.Exception, "Camera SDK: {Message}", e.Message);
      CameraControl.Devices.Log.LogError += e => logger.LogError(e.Exception, "Camera SDK: {Message}", e.Message);
    }
    public Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken token) =>
      Task.FromResult<IReadOnlyList<CameraInfo>>(
        manager.ConnectedDevices
          .Select(x => new CameraInfo(
            CameraDeviceResolver.CameraId(x),
            x.DisplayName ?? x.DeviceName,
            x.IsConnected,
            x.Manufacturer,
            x.Battery,
            x.GetCapability(CapabilityEnum.LiveView)))
          .ToList());
    public async Task<IReadOnlyList<CameraInfo>> ScanAsync(CancellationToken token)
    {
      var connected = await GetCamerasAsync(token).ConfigureAwait(false);
      var discovered = await operations.RunAsync(() => manager.ScanCameras(), token).ConfigureAwait(false);
      return connected.Concat(discovered
          .Where(x => connected.All(c => !String.Equals(c.Id, x.Id, StringComparison.OrdinalIgnoreCase)))
          .Select(x => new CameraInfo(x.Id, x.DisplayName, false, x.Manufacturer)))
        .ToList();
    }
    public async Task ConnectAsync(string cameraId, CancellationToken token)
    {
      if (String.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("Camera id is required.", nameof(cameraId));
      await lifecycle.WaitAsync(token).ConfigureAwait(false);
      try
      {
        token.ThrowIfCancellationRequested();
        if (manager.ConnectedDevices.Any(x => x.IsConnected && String.Equals(CameraDeviceResolver.CameraId(x), cameraId, StringComparison.OrdinalIgnoreCase))) return;
        var existingDevices = manager.ConnectedDevices.ToList();
        await RemoveDisconnectedDevices(token).ConfigureAwait(false);
        await operations.RunAsync(() => manager.ConnectWebCamera(cameraId), token).ConfigureAwait(false);
        if (!manager.HasConnectedCamera())
          await operations.RunAsync(() => manager.ConnectCanonCamera(cameraId), token).ConfigureAwait(false);
        if (!manager.HasConnectedCamera())
          await operations.RunMtpAsync(() => manager.ConnectNativeCamera(cameraId), token).ConfigureAwait(false);
        var selected = manager.ConnectedDevices.FirstOrDefault(x => x.IsConnected &&
          (String.Equals(CameraDeviceResolver.CameraId(x), cameraId, StringComparison.OrdinalIgnoreCase) ||
           String.Equals(x.PortName, cameraId, StringComparison.OrdinalIgnoreCase))) ??
          manager.ConnectedDevices.FirstOrDefault(x => x.IsConnected && !existingDevices.Contains(x));
        if (selected == null) throw new InvalidOperationException("The selected camera could not be connected.");
        manager.SelectedCameraDevice = selected;
      }
      finally { lifecycle.Release(); }
    }
    public async Task ConnectAsync(CancellationToken token)
    {
      await lifecycle.WaitAsync(token).ConfigureAwait(false);
      try
      {
        token.ThrowIfCancellationRequested();

        if (manager.HasConnectedCamera())
        {
          logger.LogInformation(
            "Camera discovery skipped: an active camera session is already connected");
          return;
        }

        await RemoveDisconnectedDevices(token).ConfigureAwait(false);
        // Each SDK operation and discovery barrier owns its timeout. Do not race the
        // complete discovery pipeline against another Task.Delay: the losing discovery
        // task would keep using the camera manager after lifecycle is released, allowing
        // a later Connect/Disconnect to overlap it.
        await DiscoverCameras(token).ConfigureAwait(false);

        var connected = manager.ConnectedDevices.Where(x => x.IsConnected).ToList();
        if (connected.Count == 0)
        {
          throw new InvalidOperationException(
            "No camera was detected. Check Camera.log for the SDK error.");
        }

        logger.LogInformation(
          "Camera sessions opened: {Cameras}. Capability initialization continues independently.",
          string.Join(", ", connected.Select(x => x.DisplayName ?? x.DeviceName)));
      }
      finally
      {
        lifecycle.Release();
      }
    }

    private async Task DiscoverCameras(CancellationToken token)
    {
      logger.LogInformation(
        "Discovering camera devices (Process={Bitness}, EDSDK={Sdk})",
        Environment.Is64BitProcess ? "x64" : "x86",
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EDSDK.dll"));

      // Try Canon discovery first
      await operations.RunAsync(() => manager.ConnectCanonCameras(), token)
        .ConfigureAwait(false);
      await WaitForConnected(CanonDiscoveryTimeout, token).ConfigureAwait(false);

      if (manager.HasConnectedCamera())
        return;

      // Try Nikon discovery via MTP
      await operations.RunMtpAsync(() => manager.ConnectNativeCameras(), token)
        .ConfigureAwait(false);
      await WaitForConnected(NikonDiscoveryTimeout, token).ConfigureAwait(false);

      if (manager.HasConnectedCamera())
        return;

      // Try Sony Wi-Fi as fallback
      logger.LogInformation(
        "No USB camera found after the Canon and Nikon discovery barriers; trying Sony Wi-Fi");
      await operations.RunMtpAsync(() => manager.ConnectWifiCamera("Sony"), token)
        .ConfigureAwait(false);
      await WaitForConnected(SonyDiscoveryTimeout, token).ConfigureAwait(false);
    }
    private async Task WaitForConnected(int milliseconds, CancellationToken token)
    {
      var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
      while (!manager.HasConnectedCamera() && DateTime.UtcNow < deadline)
      {
        await Task.Delay(100, token).ConfigureAwait(false);
      }
    }
    public async Task DisconnectAsync(CancellationToken token)
    {
      await lifecycle.WaitAsync(token).ConfigureAwait(false);
      try
      {
        // Cancel any pending captures
        foreach (var item in pending.ToArray())
        {
          item.Value.TrySetResult(new CaptureResult
          {
            Succeeded = false,
            CameraId = item.Key,
            CapturedAtUtc = DateTime.UtcNow,
            Error = "Camera disconnected."
          });
        }

        // Wait for transfers to complete
        destinations.Clear();
        for (var i = 0; i < TransferCleanupWaitIterations && transfers.Count > 0; i++)
        {
          await Task.Delay(TransferCleanupDelayMs, token).ConfigureAwait(false);
        }

        if (transfers.Count > 0)
        {
          logger.LogWarning(
            "Forcing camera session close while a transfer is unresponsive");
        }

        // Disconnect all cameras
        foreach (var camera in manager.ConnectedDevices.ToList())
        {
          try
          {
            await adapters.Resolve(camera)
              .DisconnectAsync(manager, camera, token)
              .ConfigureAwait(false);
          }
          catch (Exception e)
          {
            logger.LogWarning(
              e,
              "Camera session cleanup failed for {Camera}",
              camera.DisplayName ?? camera.DeviceName);
          }
          finally
          {
            manager.LastCapturedImage.TryRemove(camera, out _);
          }
        }

        // Clear state
        pending.Clear();
        destinations.Clear();
        transfers.Clear();
      }
      finally
      {
        lifecycle.Release();
      }
    }
    public Task<CameraProperties> GetPropertiesAsync(string id, CancellationToken token)
    {
      var camera = resolver.GetRequired(id);
      return operations.RunAsync(camera, () => CameraPropertyMapper.Map(camera), token);
    }
    public Task SetPropertyAsync(string id, CameraPropertyKind property, string value, CancellationToken token)
    {
      var camera = resolver.GetRequired(id);
      return operations.RunAsync(camera, () =>
      {
        var target = CameraPropertyMapper.Resolve(camera, property);

        if (target == null || !target.Available || !target.IsEnabled)
        {
          throw new InvalidOperationException($"{property} is unavailable or read-only.");
        }

        if (!target.Values.Contains(value))
        {
          throw new ArgumentException(
            $"Unsupported value for {property}: {value}",
            nameof(value));
        }

        target.SetValue(value);
      }, token);
    }
    public Task<CaptureResult> CaptureAsync(string id, bool autoFocus, CancellationToken token) =>
      CaptureAsync(id, autoFocus, null, token);

    public Task<CaptureResult> CaptureAsync(
      string id,
      bool autoFocus,
      string destinationBasePath,
      CancellationToken token) =>
      CaptureAsync(id, autoFocus, destinationBasePath, CameraSaveMode.PcOnly, token);
    public async Task<CaptureResult> CaptureAsync(
      string id,
      bool autoFocus,
      string destinationBasePath,
      CameraSaveMode saveMode,
      CancellationToken token)
    {
      var camera = resolver.GetRequired(id);
      var adapter = adapters.Resolve(camera);
      var completion = new TaskCompletionSource<CaptureResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);

      if (!pending.TryAdd(id, completion))
      {
        throw new InvalidOperationException("A capture is already in progress.");
      }

      if (!string.IsNullOrWhiteSpace(destinationBasePath))
      {
        destinations[id] = destinationBasePath;
      }

      token.ThrowIfCancellationRequested();
      try
      {
        await adapter.BeginCaptureAsync(camera, autoFocus, saveMode, token)
          .ConfigureAwait(false);

        // Once the shutter command has been accepted, cancellation must not tear
        // down the session before Canon/Nikon delivers and releases its file
        // handle. A Customer -> Admin handoff therefore waits for this bounded
        // transfer to settle, then the pipeline observes its cancelled token.
        var finished = await Task.WhenAny(
          completion.Task,
          Task.Delay(TimeSpan.FromSeconds(CaptureTimeout), CancellationToken.None))
          .ConfigureAwait(false);

        if (finished != completion.Task)
        {
          await adapter.RecoverCaptureAsync(camera, CancellationToken.None)
            .ConfigureAwait(false);

          return new CaptureResult
          {
            Succeeded = false,
            CameraId = id,
            CapturedAtUtc = DateTime.UtcNow,
            Error = "Capture or file transfer timed out."
          };
        }

        return await completion.Task.ConfigureAwait(false);
      }
      catch (Exception e) when (!(e is OperationCanceledException))
      {
        logger.LogError(e, "Capture failed for {CameraId}", id);

        return new CaptureResult
        {
          Succeeded = false,
          CameraId = id,
          CapturedAtUtc = DateTime.UtcNow,
          Error = e.Message
        };
      }
      finally
      {
        camera.IsBusy = false;
        pending.TryRemove(id, out _);
        destinations.TryRemove(id, out _);
        transfers.TryRemove(id, out _);
      }
    }
    private void OnPhotoCaptured(object sender, PhotoCapturedEventArgs args)
    {
      var camera = args.CameraDevice;
      if (camera == null)
        return;

      var id = CameraDeviceResolver.CameraId(camera);

      if (!transfers.TryAdd(id, 0))
      {
        logger.LogInformation("Cancelling additional transfer callback for {File}", args.FileName);
        _ = adapters.Resolve(camera)
          .CompleteTransferAsync(camera, args.Handle, false, CancellationToken.None);
        return;
      }

      var completion = pending.TryGetValue(id, out var active) ? active : null;
      _ = ProcessCapturedFileOnCameraContext(camera, id, args, completion);
    }
    private async Task ProcessCapturedFileOnCameraContext(
      ICameraDevice camera,
      string id,
      PhotoCapturedEventArgs args,
      TaskCompletionSource<CaptureResult> completion)
    {
      try
      {
        await operations.RunAsync(
          camera,
          () => ProcessCapturedFile(camera, id, args, completion),
          CancellationToken.None)
          .ConfigureAwait(false);
      }
      catch (Exception e)
      {
        logger.LogError(e, "Unable to dispatch transfer on the camera context");

        try
        {
          await adapters.Resolve(camera)
            .CompleteTransferAsync(camera, args.Handle, false, CancellationToken.None)
            .ConfigureAwait(false);
        }
        catch
        {
          // Ignore cleanup errors
        }

        transfers.TryRemove(id, out _);
        completion?.TrySetResult(new CaptureResult
        {
          Succeeded = false,
          CameraId = id,
          CapturedAtUtc = DateTime.UtcNow,
          Error = e.Message
        });
      }
    }
    private void ProcessCapturedFile(
      ICameraDevice camera,
      string id,
      PhotoCapturedEventArgs args,
      TaskCompletionSource<CaptureResult> completion)
    {
      CaptureResult result = null;
      var transferCompleted = false;

      try
      {
        if (completion == null)
        {
          logger.LogInformation(
            "Ignoring unsolicited host transfer event for {File}",
            args.FileName);
          return;
        }

        var destination = DetermineDestination(id, args.FileName);
        camera.IsBusy = true;

        logger.LogInformation(
          "Downloading captured file {File} directly to {Destination}",
          Path.GetFileName(args.FileName),
          destination);

        adapters.Resolve(camera)
          .TransferAsync(camera, args.Handle, destination, CancellationToken.None)
          .GetAwaiter()
          .GetResult();

        ValidateTransferredFile(destination, Path.GetFileName(args.FileName));

        transferCompleted = true;
        manager.LastCapturedImage[camera] = destination;

        result = new CaptureResult
        {
          Succeeded = true,
          CameraId = id,
          FileName = destination,
          CapturedAtUtc = DateTime.UtcNow
        };

        logger.LogInformation(
          "Capture stored on camera card and downloaded once to {File}",
          destination);
      }
      catch (Exception e)
      {
        logger.LogError(e, "Captured image transfer failed");
        result = new CaptureResult
        {
          Succeeded = false,
          CameraId = id,
          CapturedAtUtc = DateTime.UtcNow,
          Error = e.Message
        };
      }
      finally
      {
        camera.IsBusy = false;
        camera.TransferProgress = 0;

        try
        {
          adapters.Resolve(camera)
            .CompleteTransferAsync(camera, args.Handle, transferCompleted, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        }
        catch (Exception e)
        {
          logger.LogWarning(e, "Camera capture handle cleanup failed");
        }

        transfers.TryRemove(id, out _);
      }

      completion?.TrySetResult(result);
    }

    private string DetermineDestination(string id, string fileName)
    {
      var original = string.IsNullOrWhiteSpace(fileName) ? "capture.jpg" : Path.GetFileName(fileName);

      if (destinations.TryGetValue(id, out var requested))
      {
        var extension = Path.GetExtension(original);
        if (string.IsNullOrWhiteSpace(extension))
          extension = ".jpg";

        string destination;
        if (Directory.Exists(requested) || requested.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
          destination = Path.Combine(requested, original);
        }
        else
        {
          destination = Path.HasExtension(requested) ? requested : requested + extension;
        }

        var directoryPath = Path.GetDirectoryName(Path.GetFullPath(destination));
        Directory.CreateDirectory(directoryPath);

        if (File.Exists(destination))
        {
          throw new IOException(
            $"A capture with the camera filename already exists in this session: {original}");
        }

        return destination;
      }

      // Default location
      var defaultDestination = Path.Combine(captureRoot, original);
      if (File.Exists(defaultDestination))
      {
        throw new IOException($"A capture with the camera filename already exists: {original}");
      }

      return defaultDestination;
    }

    private void ValidateTransferredFile(string destination, string originalFileName)
    {
      if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
      {
        throw new IOException("Camera reported a capture but no image was transferred.");
      }
    }
    private void ResumeLiveView(ICameraDevice camera)
    {
      try
      {
        if (camera.IsConnected)
        {
          camera.StartLiveView();
        }

        logger.LogInformation("Live View resumed after capture");
      }
      catch (Exception e)
      {
        logger.LogWarning(e, "Live View resume after capture failed");
      }
    }

    private void OnCameraChanged(ICameraDevice camera)
    {
      CamerasChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDiscoveryChanged(object sender, EventArgs e)
    {
      CamerasChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RemoveDisconnectedDevices(CancellationToken token)
    {
      foreach (var camera in manager.ConnectedDevices.Where(x => !x.IsConnected).ToList())
      {
        try
        {
          await adapters.Resolve(camera)
            .DisconnectAsync(manager, camera, token)
            .ConfigureAwait(false);
        }
        catch (Exception e)
        {
          logger.LogDebug(e, "Discarding stale camera session");
          manager.ConnectedDevices.Remove(camera);
        }
      }
    }

    private Task RunOnCameraContext(Action action, CancellationToken token) =>
      operations.RunAsync(action, token);
  }
}
