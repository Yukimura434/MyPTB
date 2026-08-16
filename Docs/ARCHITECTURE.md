# PhotoBooth production architecture

PhotoBooth keeps dependency flow `UI -> Core contracts <- Business/Infrastructure/Database`. UI projects never reference camera SDK types or repositories.

The customer state machine is `Idle -> Countdown -> Smile -> Capturing -> Preview -> FrameSelection -> Printing -> QR -> Idle`. Captures are persisted after every camera operation, so a crash leaves a recoverable session record.

The final-image path is composition, ordered `IImageEffectProcessor` instances, optional watermark, resize, durable print output, then `IPrintQueueService`. Adding GIF, AI enhancement or another composition stage means registering another processor rather than changing UI workflow.

Replaceable boundaries include `IUploadService`, `IQrCodeService`, `IPrinterService`, `ICameraService`, and all plugin capability interfaces. Feature flags are stored in production settings.

Runtime data lives below `%LOCALAPPDATA%\PhotoBooth\Data`; SQLite and files are accessed only through services/repositories.

Camera ownership is exclusive and in-process. Admin scans and opens the selected camera session. Handoff stops the Admin Live View loop without disconnecting, Customer reuses that session, and returning to Admin reverses the Live View ownership. Device-arrival events never open a session and Customer does not auto-reconnect.
