# THREAD_FIX.md

**Source of truth** cho kế hoạch sửa luồng **Camera Connect / Disconnect / Recovery** hiện tại trong PhotoBooth.

---

## Bối cảnh quan trọng

Feature **Local Share đã bị gỡ bỏ khỏi project**.

Local Share **KHÔNG liên quan** đến công việc hiện tại. Do đó:

- Không tìm cách restore Local Share.
- Không implement Local Share.
- Không sửa Local Share.
- Không tham chiếu `LOCAL_SHARE_PLAN.md`.
- Không đưa Local Share vào architecture.
- Không tạo lại bất kỳ service/UI/web server/QR nào liên quan đến Local Share.

Mục tiêu hiện tại: **ổn định ownership và lifecycle của camera**, tránh tình trạng nhiều asynchronous workflow gọi chồng chéo.

---

## MỤC TIÊU TỔNG THỂ

Ổn định ownership và lifecycle của camera. Đặc biệt tránh tình trạng nhiều path cùng lúc cố:

`Connect / Disconnect / Recover / Check / StartLiveView`

dẫn tới:

- connect chồng connect;
- recovery chồng manual connect;
- semaphore contention;
- UI command treo;
- camera state bị thay đổi đồng thời;
- `context.Camera` bị race;
- Live View không ổn định;
- NullReferenceException.

**Không tạo thêm một camera connection mechanism mới.**

Ưu tiên đưa các luồng hiện có về sử dụng đúng `ICameraService` và synchronization hiện tại.

---

## Kiến trúc liên quan (tham chiếu)

- Admin (`PhotoBooth.Admin.UI`, WinExe) host customer trong cùng process qua `AddCustomerMode()`; Customer.UI là WPF class library.
- Admin và Customer **dùng chung** `ICameraService` / `CameraDeviceManager` singleton (`DependencyInjection.cs`).
- Chuyển màn hình admin ↔ customer: `CustomerModeController.StartAsync()` + `ModeHandoffCoordinator` (in-process).
- `CustomerCameraPriority` (EventWaitHandle) chỉ liên quan mode customer-standalone, không phải focus của plan này.
- Connect path điều phối chính: `HomeViewModel.Connect()` (manual + startup) và `CustomerShellViewModel.PrinterCompleted` (customer, đã gate theo availability).

---

## FIX #1 — Camera Control không load sau startup auto-connect

> **STATUS: COMPLETED**

### Files modified

- `PhotoBooth.Admin.UI/App.xaml.cs`
- `PhotoBooth.Admin.UI/ViewModels/HomeViewModel.cs`

### Actual implementation

1. `App.xaml.cs`: removed the startup recovery call (`_ = provider.GetRequiredService<IRecoveryService>().StartAsync(...)`). `IRecoveryService` is still referenced by `ShutdownCamera()` (`StopAsync`), so no import change needed.
2. `HomeViewModel.Initialize()`: restored `if (!priority.IsCustomerActive) await Connect();` — startup auto-connect now runs from `HomeViewModel` **after** `CamerasChanged` is subscribed in the constructor, so `Connect()` → `TryLoadProperties()` + `EnsureLiveViewAsync()` populates the Camera Control panel reliably.

### Result

- Startup now has **one** connect path (`Initialize() → Connect()`), replacing the previous race-prone recovery start at `App.xaml.cs`.
- Manual Connect (button) / `ResumeFromCustomerAsync` unchanged; each uses the existing `Connect()`.
- Recovery service is no longer started by Admin at startup; it remains available to the Customer flow (`PrinterCompleted`, already gated on camera availability).

### Build

`dotnet build PhotoBooth.sln --configuration Debug` → **succeeded** (0 errors; pre-existing warnings only).

### Test

`dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj --configuration Debug` → **30/30 passed**.

### Deviation

None. Matches the approved plan for Fix #1.

---

## FIX #2 — Connect timeout / UI command bị treo

> **STATUS: COMPLETED**

### Files modified

- `PhotoBooth.Infrastructure/Cameras/CameraOperationGate.cs`
- `PhotoBooth.Infrastructure/Services/CameraService.cs`

### Actual implementation

1. **`CameraOperationGate`** — added a bounded timeout (20s) to `InvokeAsync`:
   - A per-invocation `CancellationTokenSource` with `CancelAfter(OperationTimeout)` guards the `completion.Task` via `Task.WhenAny`.
   - Uses `Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)` (cancelled/disposed when the op finishes) so the hot live-view path does not accumulate timers.
   - On timeout the caller gets `OperationCanceledException("Camera SDK operation timed out.")` and the `RunOnAsync` `finally` still releases the STA/MTP semaphore.
   - This bounds every gate operation (discovery, disconnect, transfer, live view, focus), so `DisconnectAsync` is no longer blocked indefinitely behind a hung SDK call.
   - The fire-and-forget `BeginInvoke` return is discarded (`_ =`) to keep the build warning-free.

2. **`CameraService.ConnectAsync`** — total discovery timeout:
   - `DiscoverCameras` is wrapped by `DiscoverCamerasWithTimeout` using `Task.WhenAny(discovery, Task.Delay(20s))`.
   - On timeout throws `TimeoutException("Camera discovery timed out...")`; the `lifecycle` semaphore is still released by the existing `finally`, so the UI cannot remain stuck in `Connecting`.
   - Manual connect / startup connect both benefit; no new connect path, no new background loop.

### Result

- A hung synchronous EDSDK/WPD discovery now fails gracefully within ~20s and releases the camera `lifecycle` semaphore.
- `Disconnect` is no longer held by an in-progress connect, and its own camera operations are bounded by the gate timeout → UI Connect/Disconnect do not freeze.
- Manual Connect still uses the same `Connect()` path; no overlap with Recovery (Recovery is not started at Admin startup after Fix #1).

### Build

`dotnet build PhotoBooth.sln --configuration Debug` → **succeeded** (0 errors; pre-existing warnings only).

### Test

`dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj --configuration Debug` → **30/30 passed**.

### Deviation

None. Both plan sub-steps implemented. Timeout value 20s is within the approved 15–20s range.

---

## FIX #3 — CustomerWorkflowContext.Camera race / NullReferenceException

> **STATUS: COMPLETED**

### Files modified

- `PhotoBooth.Customer.UI/ViewModels/CaptureViewModel.cs`

### Actual implementation

1. **Serialize `RecoverCamera` & `CheckCamera`** (the two mutators of `context.Camera`):
   - Added `readonly SemaphoreSlim cameraGate = new SemaphoreSlim(1, 1);`.
   - Both methods now `await cameraGate.WaitAsync()` / `finally { cameraGate.Release(); }`, so a `CamerasChanged`-triggered `RecoverCamera` and a `RetryCommand`/`ActivateAsync`-triggered `CheckCamera` can no longer run concurrently and race on `context.Camera`. No re-entrant `WaitAsync` → no deadlock.

2. **Null-guard dereference points:**
   - `Start`: captures `cameraId = context.Camera?.Id` up front; if null/empty → `ErrorMessage = "Camera disconnected"`, return graceful (no crash). Uses the captured `cameraId` in `ExecuteAsync`, so a mid-capture `context.Camera` change cannot NRE.
   - `StartLive`: `var camera = context.Camera; if (camera == null) return;` then starts live view for the captured camera and passes its id to `LiveLoop`.
   - `LiveLoop`: now takes the captured `cameraId` parameter instead of re-reading `context.Camera.Id` each iteration → immune to a concurrent `context.Camera` change.

3. **No self-connect**: these paths only read `GetCamerasAsync` and adopt an existing connected camera; no new `ConnectAsync` call added.

### Build

`dotnet build PhotoBooth.sln --configuration Debug` → **succeeded** (0 errors; pre-existing warnings only).

### Test

`dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj --configuration Debug` → **30/30 passed**.

### Deviation

None. `SemaphoreSlim` used as the plan proposed; null-guards added at the three named dereference sites.

---

## FIX #4 — Admin/Customer camera state + interrupted capture persistence

> **STATUS: COMPLETED**

### Files modified

- `PhotoBooth.Customer.UI/ViewModels/CaptureViewModel.cs`

### Actual implementation

**Part 1 — Camera state (no code change needed, verified conformant):**
- Admin & Customer share the same `ICameraService`; no IPC exists.
- Customer reads camera state only via `GetCamerasAsync` (`CheckCamera`/`RecoverCamera`); no second camera cache.
- Customer has no competing Connect path — it only calls `GetCamerasAsync`, `RecoverCamera`/`CheckCamera` (adopt existing connected camera), `DisconnectAsync` (on shutdown), and `RecoveryService.StartAsync` (only when a camera is already available). No new connect call introduced.

**Part 2 — Interrupted capture persistence:**
- New `Task PreserveCapturedImages()`: clears only in-memory UI staging (`CurrentCaptureFiles`, `CapturedImages`, `Session`) — it does **not** delete image files and does **not** strip DB `CapturedImage` records.
- The two interrupt catch blocks in `Start` now call `PreserveCapturedImages()` instead of `CleanupTemporary()`:
  - `OperationCanceledException` (camera lost → `workflowCts.Cancel()`, or unexpected cancellation);
  - generic `Exception` (capture failure).
- Result: already-persisted photos (via `CapturePipeline.ExecuteAsync` → `AddCapturedImageAsync`) are preserved on disk + DB after an interrupt. Only `.staging` temp files are cleaned by the pipeline's own `finally`.

**Retake semantic preserved:**
- `Retake` still calls `CleanupTemporary()` (delete discarded preview photos + update DB), so the discard/retake behavior is unchanged.

### Build

`dotnet build PhotoBooth.sln --configuration Debug` → **succeeded** (0 errors; pre-existing warnings only).

### Test

`dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj --configuration Debug` → **30/30 passed**.

### Deviation

None. The plan's call-site inspection requirement was honored; the two interrupt paths and the Retake path were separated into preserve vs. delete semantics.

---

## IMPLEMENTATION ORDER

Thứ tự đã chốt:

1. **Fix #1**
   - `HomeViewModel.Initialize()`
   - Admin `App.xaml.cs`

2. **Fix #2**
   - `CameraOperationGate.InvokeAsync`
   - `CameraService.ConnectAsync`

3. **Fix #3**
   - `CaptureViewModel`
   - camera null guards
   - serialize `RecoverCamera` / `CheckCamera`

4. **Fix #4**
   - camera state behavior
   - `CleanupTemporary`
   - interrupt vs retake

5. **Build + tests**

Không tự thay đổi thứ tự nếu không phát hiện dependency kỹ thuật thực sự bắt buộc.

---

## QUY TẮC QUAN TRỌNG VỀ CAMERA OWNERSHIP

Trước khi implementation mỗi fix, **inspect call graph** liên quan. Tìm tất cả call sites của:

- `ConnectAsync`
- `DisconnectAsync`
- `Connect()`
- `Disconnect()`
- `RecoverCamera`
- `CheckCamera`
- `StartAsync` của recovery service
- `StopAsync` của recovery service
- `EnsureLiveViewAsync`
- camera discovery
- camera state mutation

Mục tiêu: sau khi sửa không vô tình tồn tại nhiều owner cùng điều khiển camera lifecycle.

- **Không được** giải quyết một race bằng cách tạo thêm một background recovery/connect loop.
- **Không được** gọi `ConnectAsync` từ một path mới chỉ để xử lý camera null.

---

## SOURCE OF TRUTH

Thứ tự ưu tiên:

1. **Source code hiện tại** = source of truth về implementation.
2. **`THREAD_FIX.md`** = source of truth về kế hoạch sửa lỗi đã duyệt.
3. **`PhotoBooth.ARCHITECTURE.md`** = kiến trúc tổng thể.
4. **`AGENTS.md`** = repository rules.

Nếu code thực tế khác với assumption trong plan:

- không tự ý rewrite architecture;
- ghi nhận discrepancy;
- chọn thay đổi nhỏ nhất;
- nếu discrepancy làm thay đổi bản chất kế hoạch, **DỪNG và báo lại trước khi implementation**.

---

## VERIFICATION

Sau implementation cuối cùng:

```powershell
dotnet build PhotoBooth.sln --configuration Debug
dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj --configuration Debug
```

Ngoài compile/test, cần verify logic:

- `Startup → một Connect path`
- `Manual Connect → không overlap Recovery`
- `Disconnect → không bị Connect giữ vô hạn`
- `Camera unavailable → Customer không tự tạo competing Connect`
- `Camera null → không NRE`
- `Interrupt capture → giữ ảnh thành công`
- `Retake → vẫn xóa ảnh bị discard`
