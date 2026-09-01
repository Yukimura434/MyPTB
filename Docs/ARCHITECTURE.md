# PhotoBooth production architecture

PhotoBooth keeps dependency flow `UI -> Core contracts <- Business/Infrastructure/Database`. UI projects never reference camera SDK types or repositories.

The customer state machine is `Idle -> Countdown -> Smile -> Capturing -> Preview -> FrameSelection -> Printing -> QR -> Idle`. Entering a customer turn creates an independent Booth session linked to the selected Event. Capture intent is persisted before the camera operation, accepted media is checkpointed by immutable asset ID, and finalization moves the session through an explicit lifecycle.

The final-image path is composition, ordered `IImageEffectProcessor` instances, optional watermark, resize, durable print output, then `IPrintQueueService`. Adding GIF, AI enhancement or another composition stage means registering another processor rather than changing UI workflow.

Replaceable boundaries include `IUploadService`, `IQrCodeService`, `IPrinterService`, `ICameraService`, and all plugin capability interfaces. Feature flags are stored in production settings.

Runtime data lives below `%LOCALAPPDATA%\PhotoBooth\Data`; SQLite and files are accessed only through services/repositories.

SQLite is the local operational source of truth. Folders do not define sessions and filenames do not define business identity. Print/upload/delivery side effects use durable idempotent job records, and retention checks those records before deleting media. The detailed contract is in `Docs/LOCAL_BUSINESS_DATA_MODEL.md`.

Camera ownership is exclusive and in-process. Admin scans and opens the selected camera session. Handoff stops the Admin Live View loop without disconnecting, Customer reuses that session, and returning to Admin reverses the Live View ownership. Device-arrival events never open a session and Customer does not auto-reconnect.

Live View acquisition never waits for Beauty preview processing. A capacity-one Business pipeline keeps only the newest pending frame, applies saved Beauty settings without restarting the camera session, and falls back to raw frames on failure. Admin FPS is counted when the reusable WPF image surface actually presents a frame; acquisition, preview-output and presentation rates remain separate telemetry values.
