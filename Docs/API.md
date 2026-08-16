# Extension API

- `IPresetProcessor`: executes the registered image-effect chain.
- `IImageEffectProcessor`: one independently testable image effect.
- `ICompositionProcessor`: ordered future composition stage.
- `IUploadService`: uploads a final image and returns a download URI.
- `IQrCodeService`: renders a URI as PNG QR data.
- `IPhotoDeliveryService`: registers the Windows device, creates the remote session/capture, uploads originals and composite, completes the capture and returns the tokenized frontend URI.

Device API authentication uses a per-device bearer credential issued from a one-time enrollment code. Sessions and captures are owned by the registered device. `ADMIN_API_KEY` is accepted only by enrollment administration endpoints and is never shipped with the desktop application.
- `IPrintQueueService`: enqueue, cancel, retry and observe print jobs.
- `ICameraService.ScanAsync`: enumerates supported cameras without opening a camera session. `ConnectAsync(cameraId)` is the only user-initiated session-open path; Customer Mode reuses the session established by Admin.
- `IStorageManager`: resolves controlled storage areas and applies retention.
- `IBackupService`: exports/imports application data archives.
- `IRecoveryService`: monitors and reconnects camera/live view.
- `IHealthStatusService`: returns diagnostics without exposing SDK objects.
- `ISettingsTransferService`: JSON settings export/import.
- `IUpdateService`: future updater boundary.
- `IPhotoBoothPlugin` specializations: camera, printer, image, upload and QR extensions.
