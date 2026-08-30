# Báo cáo rà soát độ ổn định, lifecycle và hiệu năng PhotoBooth

Ngày rà soát: 2026-08-30  
Phạm vi: solution desktop `PhotoBooth.sln`  
Hình thức: phân tích tĩnh, build/test/smoke test; **không sửa mã nguồn**

## 1. Kết luận điều hành

Phiên bản hiện tại có nền tảng tương đối tốt và kết quả stress test 30 phút của đội phát triển là tín hiệu tích cực. Các đường chạy thông thường đã có nhiều cơ chế đúng: chuyển quyền camera mềm giữa Admin/Customer, hủy và chờ phần lớn vòng Live View, tách encoder video sang tiến trình con, giới hạn bộ đệm video, giải phóng `Bitmap`/OpenCV `Mat`, staging file trước khi commit capture, kiểm tra hash/lineage, và giới hạn frame tối đa tám slot.

Tuy nhiên, 30 phút chạy ổn định chưa bao phủ những lỗi chỉ xuất hiện ở **ranh giới lifecycle**: đổi trang nhiều lần, trả về Admin đúng lúc compose/in/chia sẻ đang chạy, timeout camera nhưng callback đến muộn, hoặc bắt đầu phiên mới khi tác vụ của phiên cũ chưa kết thúc. Có bốn nhóm cần ưu tiên:

1. `CaptureView` có dấu hiệu rò rỉ bộ nhớ đã xác nhận do đăng ký event của ViewModel singleton nhưng không hủy khi View bị unload.
2. Việc chuyển về Admin/timeout màn hình có thể dọn workspace trong khi `FrameSelectionViewModel.Finish()` hoặc preview compose vẫn chạy.
3. Timeout capture camera không tạo được “hàng rào” đối với callback/transfer đến muộn; caller có thể nghĩ thao tác đã kết thúc trong khi native transfer vẫn tiếp tục.
4. Một số async event/task chạy kiểu fire-and-forget chưa được quản lý theo phiên, đặc biệt là tạo QR/share ở màn Complete và xử lý `CamerasChanged`.

Không có bằng chứng cho thấy dự án cần tái kiến trúc toàn bộ. Hướng an toàn là sửa từng mắt xích nhỏ, thêm test tái hiện trước, đo lại sau từng thay đổi và giữ nguyên các cơ chế đang hoạt động tốt. Không nên đồng thời đổi target x86/x64, bật SQLite WAL, thay toàn bộ pipeline ảnh, đổi sang print queue và sửa lifecycle trong cùng một đợt.

## 2. Phạm vi và giới hạn

Đã kiểm tra các project thuộc solution desktop đang hoạt động:

- `PhotoBooth.Admin.UI`
- `PhotoBooth.Customer.UI`
- `PhotoBooth.Core`
- `PhotoBooth.Business`
- `PhotoBooth.Infrastructure`
- `PhotoBooth.Database`
- `PhotoBooth.Shared`
- `PhotoBooth.FrameEngine`
- `PhotoBooth.Color.D3D11`
- `PhotoBooth.OpenCvRetouch`
- `PhotoBooth.UnitTests`
- ranh giới tích hợp đang được solution dùng trong `CameraControl.Devices`

Không kiểm tra hoặc đưa khuyến nghị thay đổi cho:

- `kotlin/`, `AndroidPhotoBooth/`, `MiuSelfBooth/`
- `template/digiCamControl/`, `.legacy-archive/` và mã camera mẫu/di sản
- `prototypes/`
- Worker, web-admin, web-customer và frontend web

Đây là audit mã nguồn và lifecycle. Không thể khẳng định tuyệt đối hành vi của driver/SDK Canon, Nikon, WIA hoặc máy in thật chỉ bằng test không phần cứng. Những điểm liên quan native/hardware được đánh dấu rủi ro cao và yêu cầu kiểm tra thiết bị thật trước khi triển khai.

## 3. Kết quả kiểm chứng hiện trạng

Các lệnh đã chạy trên working tree hiện tại:

```text
dotnet test PhotoBooth.UnitTests\PhotoBooth.UnitTests.csproj --configuration Debug --no-restore
Kết quả: 79 passed, 0 failed

dotnet build PhotoBooth.sln --configuration Debug --no-restore
Kết quả: build thành công, 0 error, 14 warning

PhotoBooth.Admin.UI\bin\Debug\net48\PhotoBooth.Admin.UI.exe --camera-smoke
Kết quả: passed
```

Các warning build đáng lưu ý:

- Không truy cập được nguồn kiểm tra lỗ hổng NuGet (`NU1900`) trong môi trường hiện tại. Điều này có nghĩa là **chưa xác minh được**, không có nghĩa package an toàn hoặc có lỗ hổng.
- `PortableDeviceLib` tham chiếu ruleset `AllRules.ruleset` không tồn tại.
- Cảnh báo lệch kiến trúc giữa `PhotoBooth.Infrastructure` MSIL và `Accord.Video.FFMPEG.dll` x86. Tiến trình chính hiện là x86 nên đường chạy hiện tại vẫn build và smoke test thành công.
- Binding redirect khai báo trong `App.config` xung đột với redirect tự sinh cho `System.Memory`, `System.Runtime.CompilerServices.Unsafe` và `System.Buffers`; output build đang dùng phiên bản tự sinh mới hơn.

Working tree đã có thay đổi trước audit ở một số file camera/Live View và có các thư mục chưa track. Báo cáo đánh giá đúng trạng thái hiện tại nhưng không thay đổi hoặc hoàn nguyên chúng.

## 4. Ma trận ưu tiên

| ID | Mức | Trạng thái bằng chứng | Vấn đề | Rủi ro nếu sửa | Quyết định |
|---|---|---|---|---|---|
| P0-01 | P0 | Đã xác nhận từ code | `CaptureView` giữ event của ViewModel singleton sau unload | Thấp–trung bình | Nên sửa đầu tiên, có test unload/reload |
| P0-02 | P0 | Đã xác nhận từ luồng | Return Admin/idle timeout có thể dọn file khi compose/finish còn chạy | Cao | Sửa theo state/lifetime, không chèn cancellation tùy tiện |
| P0-03 | P0 | Đã xác nhận từ code; cần hardware để tái hiện | Timeout camera không chặn callback/transfer đến muộn | Cao | Cần test fake + camera thật, triển khai riêng |
| P0-04 | P0 | Đã xác nhận từ code | `CamerasChanged` có thể cập nhật collection ngoài UI thread | Trung bình | Marshal UI có kiểm soát, không block Dispatcher |
| P1-01 | P1 | Đã xác nhận từ luồng | Share task phiên cũ có thể cập nhật phiên mới | Trung bình | Gắn generation/session ID và track task |
| P1-02 | P1 | Đã xác nhận từ code | Storage retention không chạy trong executable hiện tại; cách cleanup hiện tại lại không an toàn | Cao | Thiết kế cleanup theo DB reference/quarantine trước khi bật |
| P1-03 | P1 | Đã xác nhận từ code | Ghi toàn bộ `Settings` từ nhiều VM có thể lost update; hai bảng không cùng transaction | Trung bình–cao | Thêm test concurrency, transaction/version/patch |
| P1-04 | P1 | Đã xác nhận từ code | Local Share tăng dictionary/file theo phiên và thiếu giới hạn client chậm | Trung bình | Sweep + timeout + concurrency gate |
| P1-05 | P1 | Đã xác nhận từ code | Cờ `Video` và `VideoNativeEncoder` có thể bất nhất | Thấp–trung bình | Dùng một effective capability |
| P1-06 | P1 | Đã xác nhận từ code | Print queue chạy nền nhưng pipeline in lại bỏ qua queue | Cao | Chốt semantics trước; không thay một dòng trực tiếp |
| P2-01 | P2 | Đã xác nhận từ code | Preview compose tạo hàng đợi task/CTS khi thao tác nhanh | Trung bình | Latest-wins/coalescing, tách khỏi final compose |
| P2-02 | P2 | Đã xác nhận từ code | N+1 query khi nạp frame/session/capture | Thấp–trung bình | Bulk query trong commit riêng |
| P2-03 | P2 | Đã xác nhận từ code | Chuỗi preset dùng `GetPixel/SetPixel` và ghi PNG giữa từng effect | Cao | Benchmark + golden tests, tối ưu theo từng nhóm |
| P2-04 | P2 | Đã xác nhận từ code | GIF nền decode PNG lại trên mỗi tick | Trung bình | Cache frame có giới hạn hoặc release đúng lifecycle |
| P2-05 | P2 | Đã xác nhận từ code | Nikon/no-frame có thể đánh thức UI loop khoảng mỗi 1 ms | Trung bình | Đo empty/request ratio rồi backoff nhỏ theo adapter |
| P2-06 | P2 | Đã xác nhận từ code | Logging file đồng bộ trên thread gọi | Trung bình | Chỉ đổi khi profiling chứng minh nghẽn |
| P2-07 | P2 | Đã xác nhận từ code | Dịch vụ/loop/dependency không dùng vẫn được khởi tạo | Trung bình | Xác minh deployment rồi loại dần |
| P2-08 | P2 | Đã xác nhận từ code | Kiểm tra video đọc toàn bộ MP4 vào RAM dù chỉ cần header | Thấp | Đọc 12 byte đầu, giữ hash streaming |
| P2-09 | P2 | Đã xác nhận từ code | Path confinement dùng so khớp prefix chưa đủ chặt | Thấp | Dùng root có separator + test escape |
| P3-01 | P3 | Drift tài liệu | Tài liệu lifecycle/print/UI không còn khớp code | Không ảnh hưởng runtime | Cập nhật sau khi chốt hành vi |
| P3-02 | P3 | Build warning | Kiến trúc, binding redirect, ruleset và package audit | Trung bình nếu gom sửa | Xử lý riêng từng warning |

`P0` không có nghĩa phải sửa nóng không kiểm thử. Nó có nghĩa đây là nơi có khả năng gây crash, rò rỉ hoặc sai dữ liệu cao nhất và cần test tái hiện trước.

## 5. Phân tích chi tiết và kiểm tra rủi ro từng đề xuất

### P0-01 — Rò rỉ `CaptureView` qua event subscription

**Bằng chứng**

- `PhotoBooth.Customer.UI/Views/CaptureView.xaml.cs:18-27` đăng ký `DataContextChanged`, sau đó đăng ký `CaptureViewModel.PropertyChanged`.
- Chỉ hủy event khi `DataContext` đổi; không hủy khi control `Unloaded`.
- `CaptureViewModel` là singleton. Khi `ContentControl` chuyển template/page, singleton giữ delegate trỏ ngược tới View cũ, từ đó có thể giữ cả visual tree, ảnh Live View và thumbnail của phiên cũ.

**Ảnh hưởng**

Memory có thể tăng theo số lần đi qua màn Capture chứ không nhất thiết theo thời gian đứng yên ở một phiên. Stress 30 phút ít chuyển vòng đầy đủ có thể không thấy lỗi này. Vì app x86, trần không gian địa chỉ khiến rò rỉ lặp lại nguy hiểm hơn.

**Phương án an toàn**

- Đăng ký ViewModel event khi `Loaded`, hủy khi `Unloaded`, có cờ chống đăng ký hai lần; hoặc dùng `WeakEventManager` nếu đã kiểm chứng hoạt động đúng với reload.
- Không đổi lifetime singleton của toàn bộ ViewModel trong cùng thay đổi này.

**Nguy cơ phá vỡ khi sửa: thấp–trung bình**

Sai sót phổ biến là hủy event nhưng không đăng ký lại khi View được tái sử dụng, làm rotation/live image không cập nhật. Cần test tối thiểu 100 chu kỳ Waiting → Capture → FrameSelection → Complete → Waiting, unload/reload cùng View, và dùng `WeakReference` + forced GC trong WPF STA test để chứng minh View cũ được thu hồi. Theo dõi Private Bytes, GC heap, GDI handles và USER handles; các đường biểu diễn phải đạt plateau sau warm-up.

### P0-02 — Tác vụ page chưa được quiesce trước khi dọn workspace/chuyển quyền camera

**Bằng chứng**

- `FrameSelectionViewModel.Finish()` thực hiện final compose, video compose, lưu capture, in/chia sẻ với nhiều `CancellationToken.None` và không được lưu thành lifetime task.
- `FrameSelectionViewModel.ShutdownAsync()` chỉ hủy/chờ preview task, không chờ `Finish()`.
- `CustomerShellViewModel.PrepareReturnToAdminAsync()` chỉ gọi shutdown của FrameSelection/Capture.
- `CaptureViewModel.ShutdownAsync()` và `ResetToStartAsync()` có thể gọi cleanup session workspace.
- Idle timeout trong `CustomerShellViewModel` đưa workflow về Waiting qua `CaptureViewModel.ResetToStartAsync()` nhưng không quiesce FrameSelection trước. Preview compose vì vậy cũng có thể đụng file đang bị xóa.

**Ảnh hưởng**

Nếu người quản trị bấm trở về Admin hoặc idle timeout xảy ra đúng lúc final compose/video/DB/print đang chạy, app có thể xóa file đầu vào/đầu ra, resume camera Admin quá sớm, tạo capture không đầy đủ, hoặc phát sinh exception từ async command/event.

**Phương án an toàn**

- Xây một lifecycle state rõ ràng cho page operation: preview, final compose, persist, print/spool, share.
- Trước cleanup/handoff, hủy **chỉ** các pha được xác định cancel-safe và chờ chúng kết thúc. Các pha không cancel-safe phải được cho hoàn tất hoặc chặn hành động Return Admin trong thời gian ngắn với thông báo rõ ràng.
- Preview và final compose phải có owner/task riêng; final compose không được dùng chung “latest wins” cancellation với preview.
- Cleanup chỉ chạy sau khi mọi producer/consumer của workspace đã quiesce.

**Nguy cơ phá vỡ khi sửa: cao**

Không được giải quyết bằng cách truyền cùng một CTS vào mọi API. Hủy giữa lúc ghi DB, promote file hoặc gửi job cho Windows spooler có thể tạo trạng thái tệ hơn. Test bắt buộc phải chèn delay/fault tại từng pha: preview compose, final compose, encoder con, promote file, DB save, trước/sau `PrintDocument.Print()`, QR/share; sau đó kích hoạt Return Admin và idle timeout. Tiêu chí: không xóa file đang mở, không resume camera trước quiescence, không có capture nửa vời, không in trùng, và thao tác thoát vẫn có deadline.

### P0-03 — Camera timeout chưa tạo hàng rào đối với native operation/callback đến muộn

**Bằng chứng**

- `PhotoBooth.Infrastructure/Cameras/CameraOperationGate.cs` trả timeout sau khoảng 20 giây, nhưng operation đã xếp trong dispatcher/native queue không thực sự bị hủy. Semaphore được nhả khi caller đã nhận lỗi.
- `PhotoBooth.Infrastructure/Services/CameraService.cs:327-365` timeout capture sau khoảng 15 giây rồi recovery và xóa pending/destination/transfer bookkeeping.
- Callback tại `CameraService.cs:368-503` vẫn có thể đến muộn và tiếp tục transfer/complete handle với `CancellationToken.None`.

**Ảnh hưởng**

Caller có thể bắt đầu disconnect, cleanup hoặc capture mới trong khi operation cũ vẫn chạy. Callback muộn có thể ghi vào path không còn hợp lệ, hoàn tất handle sai thời điểm hoặc đụng state của phiên mới.

**Phương án an toàn**

- Timeout phải đánh dấu camera context là `faulted/unknown`, từ chối operation mới cho tới controlled recovery/disconnect/reinitialize hoàn tất.
- Ownership của transfer phải tồn tại độc lập với task chờ của caller; timeout caller không được xóa active-transfer marker trước khi callback/cleanup thực sự kết thúc.
- Chỉ abort operation dispatcher nếu chứng minh nó chưa bắt đầu. Operation native đang chạy không được coi là đã dừng chỉ vì task wrapper timeout.
- Disconnect phải xử lý destination và handle theo một protocol idempotent, có generation ID cho từng capture.

**Nguy cơ phá vỡ khi sửa: cao**

Đây là vùng native SDK nhạy cảm. Không được “nhả khóa rồi retry ngay”, không được gọi release handle hai lần và không được thay timeout hàng loạt. Cần fake adapter có callback trễ hơn 15/20 giây, callback trùng, disconnect giữa transfer; sau đó chạy camera Canon/Nikon thật, rút cáp/USB và reconnect. Kiểm tra file không partial, mỗi handle được complete đúng một lần, capture mới không nhận callback cũ và app vẫn thoát được.

### P0-04 — `CamerasChanged` có thể cập nhật WPF collection ngoài UI thread

**Bằng chứng**

- `CaptureViewModel` đăng ký `cameras.CamerasChanged += async ... RecoverCamera()`.
- `RecoverCamera()` thay đổi property, state machine và có thể clear/update `ObservableCollection`.
- Event từ camera manager/SDK không có bảo đảm luôn được phát trên WPF Dispatcher. `HomeViewModel` đã có một số đoạn marshal rõ ràng, còn đường Customer này chưa có.

**Ảnh hưởng**

Lỗi thread affinity có thể rất hiếm, phụ thuộc adapter và thời điểm rút/cắm camera. Nó có thể biểu hiện thành exception collection, UI state sai hoặc crash qua `DispatcherUnhandledException`.

**Phương án an toàn**

- Tách phần I/O/recovery chạy nền và phần apply state/collection chạy trên Dispatcher.
- Serialize event storm bằng gate hiện có, nhưng kiểm tra generation/camera ID trước khi publish state.

**Nguy cơ phá vỡ khi sửa: trung bình**

Không được dùng `.Result`, `.Wait()` hoặc giữ camera gate trong khi đồng bộ chờ Dispatcher nếu UI có thể đang chờ camera task; đó là công thức deadlock. Test phát event từ MTA và STA background trong countdown, capture, inter-shot, preview và lúc Return Admin.

### P1-01 — Share generation của phiên cũ có thể ghi đè UI phiên mới

**Bằng chứng**

- `CompleteViewModel` khởi chạy `GenerateShare()` theo kiểu fire-and-forget khi vào Complete.
- Countdown có thể gọi `Done()`, cleanup context và chuyển Waiting mà không cancel/chờ share task.
- Task cũ vẫn có thể thay `QrSource`, `DownloadUrl`, `Status`, `IsGenerating`; cờ `IsGenerating` cũng có thể ngăn phiên mới tạo share.

**Phương án an toàn**

- Mỗi lần Activate tạo `generationId`/session ID và snapshot bất biến của capture/settings.
- Track task và CTS. Khi rời page, có thể ngừng chờ/đọc thumbnail, nhưng không được vô tình hủy ticket/server đang phục vụ người dùng; kết quả cũ chỉ bị bỏ qua khi publish UI.

**Nguy cơ phá vỡ khi sửa: trung bình**

Local Share cần tiếp tục phục vụ URL đã phát hành sau khi màn Complete đóng. Vì vậy không được đồng nhất “cancel UI generation” với “thu hồi ticket”. Test thumbnail chậm, GIF lớn, timeout, lỗi mạng nội bộ và bắt đầu phiên kế tiếp ngay lập tức.

### P1-02 — Retention cleanup hiện không chạy, nhưng bật nguyên trạng có thể xóa dữ liệu còn được DB tham chiếu

**Bằng chứng**

- `StorageManager.CleanupAsync()` chỉ được gọi trong `PhotoBooth.Customer.UI/App.xaml.cs`.
- Customer UI hiện là class library; file App này bị loại khỏi executable tích hợp. Admin App không gọi cleanup.
- Cleanup hiện xóa `Temp`, `Preview`, `Captures` chỉ theo mtime, không đối chiếu capture/session/print/share đang tham chiếu.
- `FrameService.DeleteAsync()` chỉ xóa row, không xử lý managed frame file; import lỗi sau khi save file trước DB cũng có thể để orphan.
- `SessionWorkspace.Cleanup()` nuốt lỗi I/O/Unauthorized mà không log.

**Ảnh hưởng**

Disk có thể tăng vô hạn qua session, preview, frame/interface asset và preset temp sau crash. Ngược lại, chỉ thêm lời gọi cleanup hiện có vào startup có thể làm lịch sử/Local Share tham chiếu file đã bị xóa.

**Phương án an toàn**

- Thiết kế reconciliation dựa trên DB reference và trạng thái active session/ticket/print job.
- Chuyển file hết hạn vào quarantine trước, ghi manifest, chỉ xóa vĩnh viễn sau một grace period.
- Chạy sau startup theo background/idle schedule có owner và cancellation rõ ràng, không chạy trong capture/compose/print/share.
- Dọn riêng các temp có prefix do PhotoBooth sở hữu và quá tuổi; tuyệt đối không quét/xóa rộng `%TEMP%`.

**Nguy cơ phá vỡ khi sửa: cao**

Không bật `CleanupAsync()` hiện tại trong production trước khi làm reference-aware. Test file hết hạn nhưng còn là final capture, frame mặc định, print source, Local Share ticket, workspace đang capture, output directory ngoài data root, file khóa và phiên bị crash giữa promote/DB commit.

### P1-03 — Settings có thể bị lost update và ghi hai bảng không nguyên tử

**Bằng chứng**

- Nhiều Admin ViewModel thực hiện read → sửa vài property → save toàn bộ `Settings` độc lập.
- Chỉ `HomeViewModel` serialize các lần save của chính nó; `InterfaceViewModel`, `LocalShareViewModel`, printer/preset và các luồng khác không dùng cùng gate/version.
- `SqliteSettingsRepository.SaveAsync()` ghi `WorkflowSettings` rồi `ProductionSettings` trên hai connection/command, không cùng transaction.
- `InitializePhotoBooth()` save lại settings ở mọi startup kể cả khi không thay đổi.

**Ảnh hưởng**

Hai màn lưu gần nhau có thể ghi đè property của nhau bằng snapshot cũ. Crash giữa hai câu lệnh có thể để hai nhóm settings không đồng bộ. Startup tạo một lượt ghi thừa.

**Phương án an toàn**

- Trước hết thêm test concurrency/lost-update và log version.
- Chọn một trong hai contract rõ ràng: patch theo nhóm field, hoặc optimistic concurrency bằng row version; serialize toàn cục chỉ phù hợp nếu chắc chắn không có process khác cùng ghi.
- Ghi hai bảng trong một transaction trên cùng connection.
- Startup chỉ save khi thật sự vừa tạo/migrate default.

**Nguy cơ phá vỡ khi sửa: trung bình–cao**

Thay repository contract ảnh hưởng nhiều UI. Không được thêm lock toàn cục rồi giữ lock qua interaction/Dispatcher. Test save đồng thời của layout, printer, preset, local share; DB cũ thiếu column; crash/fault giữa hai lệnh; và nếu còn deployment Customer process độc lập thì test multi-process locking.

### P1-04 — Local Share thiếu sweep runtime và giới hạn client chậm

**Bằng chứng**

- Ticket trong dictionary chỉ bị loại khi chính URL hết hạn đó được request; tạo ticket mới không sweep toàn bộ.
- Thumbnail directory cleanup chủ yếu xảy ra khi service khởi tạo, không theo từng vòng đời ticket.
- Accept loop tạo handler theo client mà chưa có concurrency cap, header byte cap hoặc deadline async rõ ràng; client LAN gửi header cực chậm có thể giữ task/socket.
- Dispose chưa theo dõi/chờ toàn bộ handler.

**Phương án an toàn**

- Sweep ticket hết hạn khi create/request hoặc bằng timer duy nhất có lifecycle.
- Thêm semaphore giới hạn client, giới hạn tổng header/line count và timeout đọc bằng cancellation/task deadline.
- Track handler để shutdown có deadline; cập nhật địa chỉ LAN khi adapter thay đổi.

**Nguy cơ phá vỡ khi sửa: trung bình**

Xóa sớm sẽ làm QR còn trên điện thoại mất hiệu lực. Test ticket trước/sau expiry, response đang stream lúc sweep, 100 client đồng thời, slowloris, header lớn, đổi Wi-Fi/Ethernet và đóng app khi client đang download.

### P1-05 — Capability video có thể báo bật trong UI nhưng encoder thực tế tắt

**Bằng chứng**

- `VideoService` coi video khả dụng khi cả `Video` và `VideoNativeEncoder` bật.
- `FeatureFlagService.IsEnabledAsync("Video")` chỉ trả cờ `Video`.
- `FrameSelectionViewModel` có thể quyết định compose video theo feature flag rồi gọi service đang tắt và nhận lỗi.

**Phương án an toàn**

- Dùng một `effective video capability` duy nhất hoặc một property `IsAvailable` từ `IVideoService`; UI, capture buffer và compose đều dựa trên cùng kết quả.

**Nguy cơ phá vỡ khi sửa: thấp–trung bình**

Test cả bốn tổ hợp hai cờ môi trường, PictureOnly/PictureAndVideo, encoder thiếu file và tiến trình con lỗi. Khi encoder tắt, không được yêu cầu video asset hoặc làm capture tĩnh thất bại.

### P1-06 — Print queue được khởi động nhưng production print bỏ qua nó

**Bằng chứng**

- `PrintPipeline` nhận `IPrintQueueService` nhưng không lưu/dùng; gọi `IPrinterService.PrintAsync()` trực tiếp rồi ghi `PrintJobRecord` trạng thái `Success`.
- `HealthStatusService` phụ thuộc queue, vì vậy `PrintQueueService` cùng worker nền có thể được tạo ở startup dù đường production không enqueue.
- `PrintRetryCount` trong Settings không được gán vào `PrintQueueItem.MaximumAttempts`; model dùng mặc định 3.
- `PrintQueueService.Save()` dùng cùng file `.tmp` mà không có lock riêng trong khi producer và worker đều có thể gọi `Changed/Save`.

**Ảnh hưởng**

Kiến trúc và runtime semantics không khớp: có worker/state file nhưng retry durability không bảo vệ lệnh in thực. Nếu sau này chỉ thay direct print bằng enqueue, UI có thể báo xong trước khi in, retry gây in trùng, và race persistence có thể làm mất queue.

**Phương án an toàn**

- Trước hết quyết định nghĩa của “Success”: đã gửi cho Windows spooler hay giấy đã ra; hệ thống chỉ đáng tin cậy ở mức spool submission.
- Nếu cần queue, thiết kế enqueue + quan sát terminal state + idempotency record; serialize state persistence và dùng atomic replace an toàn.
- Nếu không cần queue, tránh khởi tạo worker và sửa diagnostics/docs cho đúng.

**Nguy cơ phá vỡ khi sửa: cao**

Không được chỉ đổi `printer.PrintAsync` thành `queue.EnqueueAsync`. Test máy in thật: crash trước/sau spool, offline rồi online, retry, app restart, đổi default printer, nhiều copies và bảo đảm không in trùng.

### P2-01 — Preview compose có thể tích lũy task/CTS khi người dùng thao tác nhanh

**Bằng chứng**

- Mỗi `UpdatePreview()` tạo CTS mới, cancel CTS trước nhưng không dispose ngay.
- Các task chờ `composeGate.WaitAsync()` không truyền cancellation; task bị cancel vẫn xếp hàng rồi mới vào gate và thoát.
- `previewTask` chỉ giữ task mới nhất; task cũ không nằm trong một collection lifecycle.

**Phương án an toàn**

- Dùng một worker latest-wins/coalescing hoặc debounce ngắn; gate wait phải cancellation-aware.
- Dispose CTS đã thay thế sau khi task tương ứng kết thúc.
- Final compose dùng lane/gate rõ ràng, không bị drop theo chiến lược preview.

**Nguy cơ phá vỡ khi sửa: trung bình**

Coalescing sai dễ hiển thị preview cũ hoặc hủy final output. Test 100 lần đổi slot/zoom/pan liên tiếp: chỉ generation cuối được publish, số task/temp file có giới hạn, sau đó Finish vẫn dùng đúng assignment cuối.

### P2-02 — N+1 query sẽ làm màn quản trị chậm dần theo dữ liệu

**Bằng chứng**

- `SqliteFrameRepository.GetAllAsync()` mở thêm connection/query `LoadSlots()` cho từng frame.
- `SqliteSessionRepository.GetAllAsync()` gọi `LoadImages()` cho từng session; các hàm chỉ cần default/base session vẫn nạp toàn bộ shot.
- Capture graph cũng nạp photos/sources theo từng capture/asset.
- API có tên async nhưng nhiều repository SQLite thực hiện I/O đồng bộ rồi trả `Task.FromResult`; caller UI có thể bị block.

**Phương án an toàn**

- Bulk-load bằng 2–3 query và group dictionary, giữ nguyên ordering/projection.
- Thêm query chuyên biệt cho default/base/by-id thay vì luôn `GetAllAsync()`.
- Trước khi đẩy toàn bộ SQLite sang thread pool, đo UI stall và kiểm tra connection/thread semantics; không tạo `Task.Run` tràn lan.

**Nguy cơ phá vỡ khi sửa: thấp–trung bình**

N+1 là tối ưu tương đối an toàn nếu giữ object graph. Test so sánh tuyệt đối kết quả cũ/mới trên DB rỗng, DB legacy và DB lớn: thứ tự frame slot/shot, default session, captured files/video IDs phải giống. Benchmark 1.000+ session và nhiều capture trước/sau.

### P2-03 — Pipeline preset tốn CPU, RAM và I/O lớn

**Bằng chứng**

- Các `ImageEffectProcessor` mở lại bitmap, chạy `GetPixel/SetPixel` theo từng pixel và ghi một PNG trung gian cho mỗi effect.
- Sharpen đọc nhiều pixel lân cận cho mỗi pixel.
- Có thể xếp nhiều processor, nên một ảnh máy ảnh độ phân giải cao bị decode/encode nhiều lần.
- Temp có prefix PhotoBooth được dọn trong đường thành công, nhưng crash có thể để lại file trong `%TEMP%`.

**Phương án an toàn**

1. Bước ít rủi ro: benchmark từng effect và dọn **chỉ** temp prefix của PhotoBooth đã quá tuổi.
2. Bước hiệu năng cao nhưng rủi ro: dùng `LockBits` và hợp nhất các phép màu tương thích trong đúng thứ tự toán học; giữ blur/sharpen/watermark/resize thành boundary riêng ban đầu.
3. Giữ cancellation giữa tile/row và không giữ nhiều bitmap 24 MP song song trong tiến trình x86.

**Nguy cơ phá vỡ khi sửa: cao**

Thay toàn bộ pipeline một lần có thể đổi màu, alpha, DPI, orientation, output naming hoặc cancellation cleanup. Bắt buộc có golden-image/pixel tolerance cho từng effect và chuỗi effect, ảnh alpha, EXIF orientation, ảnh rất lớn/nhỏ, cancellation giữa chừng, x86 peak Private Bytes; triển khai theo từng nhóm nhỏ có rollback.

### P2-04 — Animated GIF nền tạo allocation lớn theo mỗi frame

**Bằng chứng**

- `AnimatedImage` đọc toàn bộ GIF bằng `File.ReadAllBytes`.
- Mỗi timer tick chọn frame, save lại thành PNG trong `MemoryStream`, rồi tạo `BitmapImage` mới.
- `Unloaded` dừng timer nhưng chưa giải phóng toàn bộ image/stream/source cho tới lần đổi path hoặc GC.

**Phương án an toàn**

- Hoặc decode/scaled/freeze các frame một lần vào cache có giới hạn; hoặc giải phóng image/stream/source khi unload và reload khi loaded.
- Không cache vô hạn GIF độ phân giải gốc; đặt giới hạn pixel/frame/byte.

**Nguy cơ phá vỡ khi sửa: trung bình**

Timing GIF, disposal của `System.Drawing.Image`, transparency và reload rất dễ sai. Test nền GIF 30–120 phút, unload/reload nhiều lần, GIF lỗi, GIF một frame, nhiều frame, portrait/landscape và memory plateau.

### P2-05 — Backoff 1 ms khi không có Live View frame có thể gây wake-up cao

**Bằng chứng**

- Admin và Customer Live Loop gọi `Task.Delay(1)` khi frame null/trùng.
- Adapter Nikon hiện có thể chủ động trả null giữa các frame để tránh dùng cached frame. Khi call trả rất nhanh, UI context có thể thức dậy hàng trăm lần/giây chỉ để nhận null.
- Canon path có thể có hành vi blocking khác, nên một delay chung lớn có thể giảm FPS không cần thiết.

**Phương án an toàn**

- Dùng metrics hiện có để đo request FPS, published FPS, empty/duplicate ratio, UI CPU cho từng adapter.
- Chỉ sau đó thêm adaptive backoff nhỏ hoặc poll interval do adapter/service cung cấp; reset backoff ngay khi nhận frame mới.

**Nguy cơ phá vỡ khi sửa: trung bình**

Delay quá lớn làm preview giật hoặc tăng shutter-to-preview latency. Test Canon và Nikon riêng ở nhiều FPS, beauty on/off, Admin/Customer, cùng CPU và frame latency. Không hardcode một giá trị toàn cục chỉ dựa trên một camera.

### P2-06 — Logger ghi file đồng bộ trên thread gọi

**Bằng chứng**

- `RotatingFileLoggerProvider` lock, rotate và `File.AppendAllText()` ngay trên thread phát log.
- Nếu SDK/camera callback log nhiều hoặc disk/antivirus chậm, callback có thể bị giữ lại.

**Phương án an toàn**

- Chỉ đổi khi ETW/profiling hoặc log timing chứng minh có block đáng kể.
- Nếu dùng queue ghi log, queue phải bounded, ưu tiên Error, có flush deadline lúc shutdown và counter báo dropped low-level logs.
- Metrics Live View 10 giây/lần nên được giữ trong giai đoạn đo; có thể feature-gate ở release sau khi có baseline.

**Nguy cơ phá vỡ khi sửa: trung bình**

Async logger có thể mất đúng log crash quan trọng hoặc giữ app không thoát. Test disk full, file locked, log burst, crash/normal shutdown, rotation và thứ tự Error. Không tối ưu chỉ vì nhìn thấy synchronous I/O.

### P2-07 — Một số service/loop/dependency không còn khớp executable tích hợp

**Bằng chứng**

- `HomeViewModel.MonitorCustomerPriority()` chạy vòng 500 ms vô hạn, không có dispose/cancel owner. Trong integrated Customer mode hiện tại không thấy đường set priority tương ứng; có thể là di sản của executable Customer riêng.
- `RecoveryService` được đăng ký và được Stop, nhưng không thấy Start trong executable tích hợp.
- `PrintQueueService` được `HealthStatusService` kéo vào DI dù production print không dùng queue.
- `FrameSelectionViewModel` nhận một số dependency nhưng không dùng, làm service bị khởi tạo và gây nhầm lifecycle.
- `PhotoBooth.Customer.UI/App.xaml.cs` chứa startup/cleanup của app standalone nhưng project hiện là class library và file không tham gia executable.
- `PhotoBooth.Color.D3D11` chứa D3D surface/shader/package mà XAML hiện chỉ dùng `LatestJpegImage` WPF; chưa thấy consumer của surface D3D.

**Phương án an toàn**

- Lập danh sách deployment thực tế trước: có còn chạy Customer exe/process ngoài solution hay plugin reflection nào không.
- Sau đó loại từng loop/dependency/service không dùng hoặc chuyển sang lazy activation; build/package diff và chạy full smoke sau mỗi thay đổi.

**Nguy cơ phá vỡ khi sửa: trung bình**

“Không thấy reference” chưa đủ để xóa vì có thể có deployment ngoài solution, XAML/reflection hoặc kế hoạch LUT/D3D chưa bật. Không gom việc dọn dead code với sửa lifecycle. Đề xuất này là backlog sau khi chủ dự án xác nhận deployment.

### P2-08 — Integrity check video đọc toàn bộ MP4 vào RAM không cần thiết

**Bằng chứng**

- `CaptureIntegrityService.ValidateVideo()` dùng `File.ReadAllBytes(path)` nhưng chỉ kiểm tra 12 byte đầu và signature `ftyp`.
- Hash SHA-256 ở cùng service đã đọc streaming đúng cách.

**Phương án an toàn**

- Đọc chính xác phần header cần thiết bằng stream, giữ kiểm tra file length/hash streaming hiện có.

**Nguy cơ phá vỡ khi sửa: thấp**

Test MP4 hợp lệ, file dưới 12 byte, file bị truncate, extension sai, file lớn và cancellation. Lưu ý kiểm tra `ftyp` hiện tại chỉ là sanity check chứ không chứng minh toàn bộ MP4 decode được; không mở rộng parser trong cùng thay đổi.

### P2-09 — Kiểm tra path prefix chưa đủ chặt

**Bằng chứng**

- `FileStorageService.Resolve()` kiểm tra `path.StartsWith(_root)` nhưng `_root` không được chuẩn hóa với trailing separator.
- Một path sibling có cùng prefix, ví dụ `Data-escape`, có thể qua kiểm tra prefix. Call nội bộ hiện dùng tên có kiểm soát, nhưng đây là Core boundary công khai.
- `BackupService.ImportAsync()` dùng mẫu prefix tương tự khi copy từ staging vào data directory.
- `ColorLutPathResolver` và `SessionWorkspace.Contains()` đã có pattern root + separator chặt hơn để tái sử dụng.

**Phương án an toàn**

- Chuẩn hóa root tuyệt đối có separator; chỉ chấp nhận exact root nếu contract cho phép, còn lại phải bắt đầu bằng `root + separator` với comparison phù hợp Windows.
- Từ chối rooted/UNC path đầu vào nếu API yêu cầu relative path.

**Nguy cơ phá vỡ khi sửa: thấp**

Test `..`, sibling-prefix, separator `/` và `\`, khác casing, exact root, UNC/drive khác và path hợp lệ hiện tại. Không thay quy tắc path của asset đã lưu trong DB nếu chưa có migration.

### P2-10 — Backup live chưa có snapshot nhất quán

**Bằng chứng**

- `BackupService.ExportAsync()` zip trực tiếp toàn bộ data directory trong khi SQLite và file capture có thể đang thay đổi.
- Import copy đè vào data directory khi các service/connection có thể vẫn hoạt động.
- Service hiện chưa thấy được dùng trong UI chính, nên rủi ro runtime hiện thấp nhưng rất cao nếu đưa ra giao diện quản trị mà không đổi protocol.

**Phương án an toàn**

- Không expose live import/export hiện trạng.
- Thiết kế maintenance mode: quiesce camera/workflow/share/print, dùng SQLite backup API hoặc snapshot đã checkpoint, manifest + hash, extract staging, validate rồi atomic swap khi DB/service đã đóng.

**Nguy cơ phá vỡ khi sửa: rất cao**

Không “sửa nhanh” bằng cách bật WAL hoặc copy thêm `-wal/-shm`. Test crash giữa export/import, backup trong capture, DB schema cũ/mới, thiếu file, zip traversal, disk full và rollback. Đây là hạng mục độc lập, không phải tối ưu ngắn hạn.

### P2-11 — Video buffer đã bounded nhưng có thể tối ưu theo session, không được bỏ clone tùy tiện

**Bằng chứng và đánh giá**

- Video service giới hạn khoảng `18 fps × 8 giây + margin`; đây là thiết kế bounded đúng.
- `AddLiveViewFrame` clone mảng JPEG để tránh caller/camera tái sử dụng buffer. Khi snapshot để encode, trong thời gian ngắn có thể giữ cả snapshot cũ và buffer đang nhận mới, nhưng vẫn có giới hạn.
- Encoder chạy tiến trình con, có watchdog và validate MP4; điều này bảo vệ tiến trình x86 khỏi nhiều lỗi native và nên được giữ.

**Phương án an toàn**

- Chỉ buffer khi có customer capture session và effective video capability bật.
- Có thể cấu hình duration theo countdown + margin thực tế, nhưng phải benchmark chất lượng/video coverage.

**Nguy cơ phá vỡ khi sửa: cao nếu bỏ clone/pool sai ownership**

Không trả thẳng buffer camera vào ring hoặc pool lại trước khi encoder snapshot kết thúc nếu contract ownership chưa rõ. Test adapter tái sử dụng cùng mảng, capture liên tục, encoder chậm và peak memory. Đây không phải ưu tiên trước các leak/lifecycle P0.

### P2-12 — Shutdown dùng một deadline chung có thể bỏ qua cleanup camera cuối chuỗi

**Bằng chứng**

- Admin App tạo một CTS khoảng 5 giây rồi dùng tuần tự cho recovery stop, customer/capture shutdown và camera disconnect.
- Nếu pha đầu tiêu thụ gần hết deadline, disconnect nhận token đã hủy. Watchdog sau đó có thể force-kill tiến trình để bảo đảm update/restart.

**Phương án an toàn**

- Giữ một overall deadline nhưng chia phase budget và log rõ pha nào hết hạn.
- Thứ tự: chặn operation mới → quiesce workflow/file producer → dừng live view → giải phóng camera/native handle → dispose provider.
- Giữ force-kill làm last resort cho tới khi chứng minh shutdown luôn bounded.

**Nguy cơ phá vỡ khi sửa: cao**

Kéo dài timeout vô hạn có thể làm update/restart treo; rút ngắn có thể làm file/handle dang dở. Test task compose treo, callback camera treo, local share client chậm, print đang spool và update restart. Chỉ thay sau khi có telemetry phase timing.

### P2-13 — Single-instance có thể coi startup chậm là process treo

**Bằng chứng**

- Cơ chế single-instance có grace period hữu hạn và có thể kill process cùng executable nếu sau thời gian đó chưa có main window/không responding.
- Startup DB backfill hoặc native SDK trên máy chậm có thể kéo dài; một lần bấm mở app thứ hai có thể tác động process đầu còn hợp lệ.

**Phương án an toàn**

- Dùng named synchronization/heartbeat với trạng thái `starting/ready/shutting-down` thay cho suy luận chỉ từ window handle và timeout cố định.

**Nguy cơ phá vỡ khi sửa: trung bình**

Nếu protocol sai, app có thể không bao giờ mở lại sau crash hoặc cho phép hai process cùng camera/DB. Test startup cố ý delay, crash trước ready, second launch, update restart và process cũ thật sự treo.

## 6. SQLite và dữ liệu: những thay đổi không nên làm vội

### Không bật WAL chỉ để “tăng tốc”

Hiện connection chỉ bật foreign keys và chưa có WAL/busy timeout rõ ràng. WAL có thể tăng concurrency, nhưng làm xuất hiện sidecar `-wal/-shm` và thay đổi yêu cầu backup/checkpoint/shutdown. Vì `BackupService` hiện zip live data, bật WAL riêng lẻ có thể làm backup thiếu transaction mới nhất hoặc khó restore.

Hướng an toàn:

1. Thêm timing cho command/transaction và thống kê `SQLITE_BUSY` trước.
2. Giảm N+1 và rút ngắn transaction.
3. Cân nhắc bounded busy timeout/serialized writes sau test contention.
4. Chỉ đánh giá WAL cùng một thiết kế backup/restore mới và test power-loss.

Rủi ro nếu bật ngay: **cao**. Lợi ích chưa được đo; không khuyến nghị ở phiên bản kế tiếp.

### Transaction session/settings phải giữ invariant lịch sử

`SqliteSessionRepository.SaveAsync()` và settings có thao tác qua nhiều lệnh/connection. Gom transaction là đúng hướng, nhưng DB có trigger/invariant bảo vệ capture history. Khi chỉnh phải test DB migrated, immutable capture asset, default session và crash injection. Không thay schema và repository cùng lúc với lifecycle P0.

## 7. Những điểm đang làm tốt và nên giữ nguyên

- `LatestJpegImage` dùng newest-frame-wins, decode nền, tái sử dụng `WriteableBitmap`, pool BGRA có giới hạn và dừng khi invisible/unloaded. Đây là cơ chế phù hợp cho Live View; không thay bằng tạo `BitmapImage` mới mỗi frame.
- OpenCV retouch dispose native `Mat`, serialize full-quality processing và blend theo tile để giới hạn bộ nhớ. Live preview có sampling/cached analysis và reset/dispose. Giữ thứ tự shutdown hiện tại: dừng live loop trước khi dispose provider/native service.
- Video encoder được cách ly trong process con, có timeout/watchdog và kiểm tra output. Không chuyển native encode trở lại process WPF chỉ để giảm overhead.
- Capture pipeline dùng staging/promote/cleanup và lưu hash/lineage. Không bỏ integrity check để tăng tốc; chỉ tối ưu cách đọc header/video.
- Live View/camera handoff Admin–Customer nhìn chung tránh reconnect không cần thiết và đã track/cancel nhiều vòng loop quan trọng.
- Frame engine chỉ phụ thuộc Core + `System.Drawing`, hard limit tám slot và có test độc lập.
- Các tài nguyên `Bitmap`, stream, PrintDocument và OpenCV ở các đường chính phần lớn được bọc `using`/dispose đúng.
- Global unhandled handler đóng ứng dụng thay vì nuốt mọi lỗi. Khi bổ sung command/event error handling, không được biến thành blanket swallow khiến app tiếp tục trong state hỏng.

## 8. Các thay đổi tuyệt đối không gom vào một đợt

1. Không đổi toàn solution từ x86 sang x64/AnyCPU trong lúc sửa camera lifecycle. Canon/native DLL và Accord FFmpeg có ràng buộc kiến trúc.
2. Không bật SQLite WAL trước khi thay backup protocol và test power-loss/restore.
3. Không gọi ngay retention cleanup hiện có ở startup; nó chưa biết DB references và active session.
4. Không thay direct print bằng queue bằng một dòng; cần semantics/idempotency/restart test.
5. Không bỏ clone của video/live frame nếu chưa chứng minh ownership của buffer camera.
6. Không parallelize nhiều effect/bitmap 24 MP trong process x86; có thể tăng peak memory và OutOfMemory nhanh hơn.
7. Không truyền một CTS chung vào compose, DB, promote, print và share rồi coi cancel là rollback.
8. Không xóa Customer standalone code, priority loop, D3D surface hoặc package chỉ vì `rg` không thấy reference trước khi xác nhận deployment/XAML/reflection.
9. Không sửa đồng thời binding redirects, platform targets và package versions; mỗi nhóm cần clean-machine startup/package test riêng.
10. Không tăng/loại watchdog shutdown cho tới khi có phase timing và fault-injection.

## 9. Kế hoạch triển khai an toàn đề xuất

### Giai đoạn 0 — Baseline và test tái hiện, không đổi hành vi

- Chụp baseline Private Bytes, managed heap, GDI/USER handles, thread count, CPU, Live View request/published/empty ratio, disk growth và thời gian SQLite query.
- Bổ sung harness chuyển 100 vòng Customer workflow và 100–500 lần Admin/Customer handoff.
- Thêm fault-injection/delay cho compose, video child, DB save, printer, share và camera callback.
- Lưu artifact/log theo generation/session ID để truy vết task cũ.

Rủi ro: thấp nếu instrumentation bounded và có thể tắt. Không ghi log theo từng frame ở production.

### Giai đoạn 1 — Sửa leak View và stale UI task

- P0-01 `CaptureView` subscribe/unsubscribe.
- P1-01 Complete share generation fencing.
- P2-08 streaming video header.
- P2-09 path confinement.

Đây là nhóm có phạm vi nhỏ nhất. Vẫn nên tách thành các commit/release candidate riêng để rollback dễ.

### Giai đoạn 2 — Lifecycle/handoff

- P0-02 page-operation ownership/quiescence.
- P0-04 dispatcher-safe camera event.
- P2-01 preview coalescing sau khi lifecycle test đã có.
- P2-12 phase-aware shutdown sau khi đo timeout.

Không làm camera native timeout P0-03 trong cùng commit với UI lifecycle.

### Giai đoạn 3 — Native camera timeout

- Fake adapter tests trước.
- Một adapter/hardware family mỗi lần.
- Canary trên đúng model camera/driver thực tế, theo dõi reconnect và late callback.
- Có feature switch/rollback về protocol cũ trong giai đoạn xác nhận.

### Giai đoạn 4 — Dữ liệu, retention, Local Share và print

- Settings concurrency/transaction.
- Bulk query N+1.
- Reference-aware retention/quarantine.
- Local Share sweep/concurrency.
- Quyết định queue semantics rồi mới triển khai hoặc loại queue.

Mỗi phần phải có backup DB test fixture và restore verification; không bật WAL trong giai đoạn này trừ khi là project riêng.

### Giai đoạn 5 — Tối ưu CPU/RAM lớn

- Benchmark preset pipeline và GIF animation.
- Tối ưu từng nhóm effect bằng golden tests.
- Điều chỉnh Live View backoff theo từng adapter dựa trên metrics.
- Dọn service/dependency/package không dùng sau khi xác nhận deployment.

## 10. Ma trận kiểm thử hồi quy bắt buộc

| Nhóm | Kịch bản tối thiểu | Tiêu chí đạt |
|---|---|---|
| Workflow memory | 100 vòng đầy đủ, ảnh/video on/off, quay lại Waiting | Sau warm-up, Private Bytes/handles đạt plateau; View cũ được GC |
| Handoff | Return Admin ở mọi pha compose/save/print/share | Không cleanup chồng tác vụ; camera owner duy nhất; không file/DB nửa vời |
| Idle timeout | Timeout trong Preview/FrameSelection/Complete | Page operation quiesce đúng; phiên mới không nhận state cũ |
| Camera | Callback trễ, callback trùng, transfer treo, rút cáp | Handle hoàn tất đúng một lần; camera fault fencing; reconnect được |
| UI threading | Phát `CamerasChanged` từ MTA/STA | Không cross-thread exception/deadlock; state cuối đúng |
| Share | Slow thumbnail, 100 client chậm, expiry, network đổi | Task/socket bounded; ticket đúng TTL; shutdown hoàn tất |
| Print | Offline/retry/restart/crash quanh spool | Không báo sai/nghẽn worker/in trùng; state persistence nguyên vẹn |
| SQLite | DB rỗng/legacy/lớn, concurrent settings save | Object graph/order giữ nguyên; không lost update/partial two-table save |
| Storage | Active/expired/orphan/referenced/locked files | Không xóa asset còn tham chiếu; quarantine/rollback hoạt động |
| Image | Golden images, 24 MP, alpha, EXIF, cancel | Sai khác trong tolerance đã chốt; output contract không đổi; peak bounded |
| GIF | 30–120 phút, unload/reload, corrupt GIF | Memory/CPU plateau; timing và transparency đúng |
| Shutdown | Các service cố ý treo/lỗi | Deadline hữu hạn; log nêu phase; lần khởi động sau sạch |
| Packaging | Debug/Release x86, máy sạch | Không BadImageFormat/binding failure; smoke và 79+ tests pass |

Không nên dùng một ngưỡng MB tuyệt đối cho mọi máy/camera. Tiêu chí quan trọng là **độ dốc sau warm-up**: tài nguyên không tăng tuyến tính theo số workflow, ticket, page transition hoặc callback.

## 11. Đánh giá codebase và lifecycle tổng thể

### Codebase

Dependency direction cốt lõi nhìn chung vẫn hợp lý: Core giữ contract/model; Business giữ pipeline; Database/Infrastructure triển khai; UI tiêu thụ qua DI. Các vấn đề chính không nằm ở việc “thiếu thêm abstraction” mà ở lifetime ownership, async boundary và một số implementation chưa khớp contract đã có. Không nên thêm hàng loạt service/interface mới; ưu tiên làm rõ owner của task/resource trong abstraction hiện tại.

Một số tài liệu đã lệch code: kiến trúc cũ mô tả UI/page/print queue khác hiện trạng; solution nay có Color D3D11/OpenCV. Drift này không gây crash trực tiếp nhưng làm người sửa sau dễ kích hoạt nhầm service hoặc tin cleanup/queue đang hoạt động. Cập nhật tài liệu sau khi chốt semantics là thay đổi ít rủi ro.

### Lifecycle

Lifecycle camera/live loop chính có chủ ý tốt, nhưng lifecycle **page operation và session artifact** chưa kín. Chuỗi an toàn cần được định nghĩa thống nhất:

```text
Ngăn tác vụ mới
  → hủy/chờ tác vụ cancel-safe
  → hoàn tất hoặc cô lập tác vụ không cancel-safe
  → dừng producer Live View/camera
  → đóng transfer/native handle
  → cleanup/quarantine workspace
  → chuyển camera owner hoặc dispose DI
```

Hiện một vài đường Return Admin/idle/Complete bỏ qua một hoặc nhiều bước giữa, là nguồn rủi ro lớn hơn các micro-optimization.

### Hiệu năng và tài nguyên

Có thể giảm tài nguyên đáng kể, nhưng thứ tự đúng là:

1. Chặn leak và task phiên cũ.
2. Giảm I/O/query thừa và runtime retention.
3. Tối ưu GIF/preset pipeline dựa trên benchmark.
4. Tuning adapter-specific cho Live View.

Các tối ưu nhỏ như bỏ một allocation hoặc đổi async logger không đáng đánh đổi độ ổn định nếu chưa có profile. Lợi ích lớn nhất dự kiến đến từ việc giải phóng View đúng lifecycle, tránh pipeline ảnh decode/encode lặp, bulk-load SQLite và cleanup storage an toàn.

## 12. Quyết định đề xuất

Phiên bản hiện tại có thể tiếp tục làm baseline/candidate vì build, unit test và camera smoke đều đạt. Tuy vậy, trước khi coi là ổn định dài hạn hoặc chạy kiosk nhiều ngày, nên hoàn thành ít nhất:

1. Test tái hiện và sửa P0-01.
2. Thiết lập ownership/quiescence cho P0-02.
3. Test late callback và thiết kế fencing cho P0-03 trên camera thật.
4. Marshal đúng UI thread cho P0-04.
5. Fence share task P1-01.
6. Chưa bật retention cleanup hiện tại; thiết kế P1-02 trước.

Mọi thay đổi nên là commit nhỏ, có test trước/sau và rollback độc lập. Không có khuyến nghị nào trong báo cáo này yêu cầu phá kiến trúc hoặc viết lại dự án.
