# Kế hoạch tích hợp OpenCV Retouch cho PhotoBooth

## 1. Mục tiêu và kết luận nhanh

Mục tiêu là thêm beauty retouch cho **ảnh chụp tĩnh**, với sáu mức điều chỉnh trong một trang mới của Admin:

- Smooth Skin: `0–100`
- Brighten Skin: `0–100`
- Skin Tone: `0–100`
- Sharpen: `0–100`
- Eye Size: `0–100`
- Slim Face: `0–100`

Thứ tự xử lý mục tiêu:

```text
Ảnh camera
  -> auto-flip / chuẩn hóa file
  -> face detection + landmarks
  -> skin mask
  -> retouch bằng OpenCvSharp
       - bilateral smoothing
       - skin brighten
       - reduce redness / skin tone
       - local contrast nhẹ
       - sharpen vùng mắt/tóc
       - blend với ảnh gốc
  -> LUT .cube hiện có
  -> ảnh capture dùng cho chọn frame / ghép / in
```

**Kết luận tương thích:** phần managed của OpenCvSharp 4 tương thích với `.NET Framework 4.8`, nhưng ứng dụng hiện tại chạy **x86**. OpenCvSharp 4.13 chỉ phát hành native runtime Windows x64, nên **không thể dùng 4.13 trong cấu hình hiện hành**. Có hai hướng khả thi:

1. Khuyến nghị ngắn hạn: ghim toàn bộ package OpenCvSharp ở bản **4.11.0.20250507**, là tag phát hành cuối thực tế trước 4.13 và có Windows x86; vẫn phải chạy smoke test trên artifact phát hành.
2. Hướng dài hạn: chuyển toàn bộ executable cùng camera SDK/native dependencies sang x64 rồi dùng OpenCvSharp 4.13.x. Đây là thay đổi platform lớn và không thuộc phạm vi triển khai beauty ban đầu.

Không nên đưa source clone OpenCvSharp vào `PhotoBooth.sln`. Dùng package NuGet được ghim phiên bản giúp kiểm soát native asset, license và quy trình build tốt hơn.

## 2. Kết quả kiểm tra thư mục clone hiện tại

Đường dẫn được kiểm tra:

```text
CameraEngine/OpenCVRetouch/opencvsharp
```

Kết quả kiểm tra cuối ngày 26/08/2026 sau khi thay clone:

- clone mới checkout detached HEAD đúng tag `4.11.0.20250507`;
- commit: `2360cafffe47273dc659fb45dbb5bf07bdd65f85`;
- remote `origin` trỏ tới `https://github.com/shimat/opencvsharp.git`;
- submodule `samples` đã checkout commit `42322d297b313d9f115cba80773f2496a8c9cd9f`;
- source tag này dùng OpenCV 4.11.0 và có wrapper core, DNN, face/contrib, WPF/GDI extensions;
- `src/OpenCvSharp/OpenCvSharp.csproj` target `netstandard2.0;netstandard2.1;net48;net6.0`, do đó tương thích compile với PhotoBooth `net48`;
- `nuget/OpenCvSharp4.runtime.win.nuspec` khai báo cả `runtimes/win-x86/native` và `runtimes/win-x64/native`;
- với .NET Framework, `OpenCvSharp4.runtime.win.props` copy cả hai architecture vào `dll/x86` và `dll/x64`; `WindowsLibraryLoader` chọn thư mục theo architecture của process;
- source có các API cần thiết như `Cv2.BilateralFilter`, DNN `ReadNetFromONNX` và contrib `FacemarkLBF`;
- clone vẫn độc lập, chưa được tham chiếu bởi `PhotoBooth.sln` hay `.csproj` của ứng dụng.

Kiểm tra trực tiếp package NuGet chính thức `OpenCvSharp4.runtime.win` version `4.11.0.20250507` xác nhận có:

- `runtimes/win-x86/native/OpenCvSharpExtern.dll`, 42,627,072 byte, PE machine `0x014C` (x86);
- `runtimes/win-x86/native/opencv_videoio_ffmpeg4110.dll`;
- native x64 tương ứng trong `runtimes/win-x64/native`.

Upstream hiện ghi người dùng x86 nên ở lại “4.12.x”, nhưng kiểm tra toàn bộ remote tag cho thấy **không tồn tại tag 4.12.x**: tag cuối trước 4.13 là `4.11.0.20250507`. Vì vậy tài liệu khóa bản 4.11 này thay cho tên 4.12 không tồn tại.

Kết luận: clone mới phù hợp để nghiên cứu và làm compatibility spike cho PhotoBooth `net48/x86`. Artifact ứng dụng vẫn nên dùng NuGet được ghim version hoặc pipeline native có checksum; không project-reference trực tiếp toàn bộ repository upstream.

## 3. Ràng buộc tương thích của ứng dụng

| Hạng mục | Ứng dụng hiện tại | Ảnh hưởng |
|---|---|---|
| Runtime | .NET Framework 4.8 | Dùng OpenCvSharp 4; không dùng OpenCvSharp 5 vì bản 5 yêu cầu .NET 8+ |
| Process | `PhotoBooth.Admin.UI` là x86, `Prefer32Bit=true` | Native `OpenCvSharpExtern.dll` bắt buộc phải là x86 |
| UI | WPF; Customer UI là class library trong process Admin | Không cần `OpenCvSharp.WpfExtensions` cho pipeline xử lý file |
| Ảnh | `System.Drawing`, JPEG/PNG | Có thể dùng `Cv2.ImRead`/`ImWrite`, hoặc GDI extensions; ưu tiên file/byte API để giảm coupling |
| DI | Core contracts; Business orchestration; Infrastructure adapter | OpenCvSharp và model runtime chỉ đặt trong Infrastructure |
| Camera native | Canon EDSDK/WIA và nhiều DLL x86 | Không đổi app sang x64 chỉ để lấy runtime OpenCvSharp mới |
| Packaging | SDK-style `net48` | Phải kiểm tra native DLL thực sự được copy vào output/installer và không bị bỏ sót khi publish |

OpenCvSharp yêu cầu native binding `OpenCvSharpExtern.dll`; sai bitness sẽ gây `BadImageFormatException`, thiếu DLL/phụ thuộc VC++ sẽ gây `DllNotFoundException` hoặc lỗi load native. Máy kiosk cần Visual C++ Redistributable phù hợp với bản runtime đã chọn.

### Quyết định package đề xuất

- Tạo project mới `PhotoBooth.OpenCvRetouch` hoặc đặt implementation trong `PhotoBooth.Infrastructure/Imaging/Retouch`. Ưu tiên project riêng để cô lập native dependency và test.
- Target `net48`, `PlatformTarget=x86`.
- Ghim `OpenCvSharp4`, runtime Windows và package extension (nếu thật sự cần) ở cùng version **4.11.0.20250507**; không dùng floating version.
- Không dùng bản 4.13.x trong process x86.
- Không cần `highgui`, camera/video I/O hoặc WPF extension cho tính năng này.
- Lưu notice/license của OpenCvSharp/OpenCV và model face/landmark trong `THIRD_PARTY_NOTICES.md` và installer.

Tên chính xác của version/package x86 phải được khóa sau spike ở mục 11; không ghi đoán một version vào production trước khi kiểm tra nội dung native asset.

## 4. Luồng ảnh thực tế và vị trí tích hợp

### 4.1 Capture hiện tại

`PhotoBooth.Business/Pipelines/CapturePipeline.cs` đang thực hiện:

```text
camera.CaptureAsync(..., *.staging.jpg)
  -> FinalizeImage(staging, rawStill, AutoFlip)
  -> copy rawStill thành pictureDestination
  -> ColorLutService.ApplyCaptureAsync(presetId, pictureDestination)
  -> tạo video từ rawStill (chưa LUT)
  -> ghi CapturedShot vào session
```

Điểm chèn đúng:

```text
FinalizeImage
  -> copy/retouch sang pictureDestination
  -> ApplyCaptureAsync (LUT)
```

`rawStill` nên tiếp tục là ảnh sạch dùng tạo video. Beauty chỉ xử lý `pictureDestination`; như vậy không làm tăng thời gian/CPU của video và giữ nguyên hành vi video hiện tại.

### 4.2 Live View hiện tại và vị trí tích hợp mới

`LiveViewService` chỉ là camera adapter: lấy `LiveViewFrame.ImageData` rồi trả frame thô cho UI. Không chèn Beauty vào service này. Customer UI tạo bản Beauty nhẹ in-memory; cùng bản này được hiển thị và chuyển cho `IVideoService.AddLiveViewFrame`, trong khi frame camera gốc vẫn không bị sửa.

Trong Customer UI, frame thô phải tiếp tục đi theo hai nhánh độc lập:

```text
LiveViewService.GetFrameAsync
  ├─ raw ImageData -> VideoService (giữ nguyên)
  └─ raw ImageData -> Beauty preview nhẹ -> LiveColorSurface -> LUT shader -> màn hình
```

`LiveColorSurface` hiện đã dùng cơ chế latest-frame (`Interlocked.Exchange`) và pixel shader áp dụng LUT. Vì vậy vị trí dài hạn phù hợp nhất cho preview là sau khi frame được upload lên D3D11 và trước phép tra LUT. Face detection/landmark/mask vẫn chạy CPU theo chu kỳ thưa trên ảnh downscale; shader chỉ nhận mask/tham số và thực hiện hiệu ứng nhẹ. Nếu D3D11 hoặc Beauty lỗi, `<Image>` WPF hiện có tiếp tục hiển thị frame JPEG thô.

Không tái sử dụng nguyên pipeline bilateral full-resolution của ảnh chụp cho từng frame Live View. Có thể làm CPU prototype để đo, nhưng không đưa vào production nếu phải decode + retouch + encode JPEG ở mỗi vòng `40 ms`.

### 4.3 Frame/preset hiện tại

Trong `FrameSelectionViewModel.Compose`:

1. các ảnh capture đã qua LUT được đưa vào `IImageCompositionService` để ghép frame;
2. chỉ khi tạo final, `IPresetProcessor` mới chạy các effect GDI+ như brightness, contrast, sharpen, watermark, resize trên **ảnh composite**;
3. LUT không nằm trong `PresetProcessor`.

Vì vậy không đăng ký beauty như một `IImageEffectProcessor` thông thường trong danh sách hiện tại: processor đó sẽ chạy sau khi ghép, có thể nhận diện khuôn mặt trên cả frame/overlay, làm mềm chữ hoặc trang trí, và phá thứ tự “retouch rồi LUT”.

### 4.3 Luồng đề xuất sau sửa đổi

```text
staging.jpg
  -> auto-flip -> rawStill
                  |-> VideoService (giữ nguyên)
                  `-> copy pictureDestination
                         -> IBeautyRetouchService.ProcessAsync
                         -> IColorLutService.ApplyCaptureAsync
                         -> session CapturedShot
                         -> preview / chọn ảnh / ghép frame
                         -> các preset effect hậu kỳ hiện có (nếu được cấu hình)
                         -> final composite / print / share
```

## 5. Kiến trúc phần mềm đề xuất

Giữ dependency direction `UI -> Core <- Business/Infrastructure/Database`.

### 5.1 Core

Thêm model và contract, không tham chiếu OpenCvSharp:

```csharp
public sealed class BeautySettings
{
    public bool Enabled { get; set; }
    public int SmoothSkin { get; set; }      // 0..100
    public int BrightenSkin { get; set; }    // 0..100
    public int SkinTone { get; set; }        // 0..100
    public int Sharpen { get; set; }         // 0..100
    public int EyeSize { get; set; }         // 0..100
    public int SlimFace { get; set; }        // 0..100
}

public interface IBeautySettingsService
{
    Task<BeautySettings> GetAsync(CancellationToken token);
    Task SaveAsync(BeautySettings value, CancellationToken token);
}

public interface IBeautyRetouchService
{
    Task<BeautyRetouchResult> ProcessAsync(
        string inputPath,
        string outputPath,
        BeautySettings settings,
        CancellationToken token);
}
```

`BeautyRetouchResult` nên có `Applied`, `FacesDetected`, `DurationMilliseconds` và warning/error code không chứa ảnh hay dữ liệu khuôn mặt.

### 5.2 Business

- `CapturePipeline` lấy snapshot `BeautySettings` một lần cho mỗi shot.
- Sau khi tạo `pictureDestination`, gọi beauty service trước LUT.
- Không giữ `Mat` hoặc model native trong Business.
- Cấu hình 0 hoặc `Enabled=false` thì bỏ qua hoàn toàn, không decode ảnh.
- Failure policy mặc định là **fail-open**: nếu detector/retouch lỗi, log warning và tiếp tục với ảnh gốc; lỗi LUT/capture giữ hành vi hiện tại. Có thể thêm feature flag để tắt beauty tức thời.

### 5.3 Infrastructure / OpenCV adapter

Implementation giữ các trách nhiệm:

- load và validate model một lần;
- detect face/landmark trên bản downscale;
- scale landmark về ảnh gốc;
- dựng skin/eye/hair masks;
- chạy phép toán OpenCV;
- ghi file tạm cùng thư mục rồi replace/copy an toàn;
- dispose mọi `Mat`, `InputArray`, model và native resource;
- giới hạn concurrency bằng `SemaphoreSlim` (khởi đầu `1`) để tránh nhiều capture đồng thời làm cạn RAM x86.

Đăng ký đúng một implementation mặc định trong `PhotoBooth.Infrastructure.DependencyInjection`.

### 5.4 Database

Beauty là cấu hình toàn cục theo yêu cầu “một tab mới trong menu Admin”, nên dùng bảng singleton riêng thay vì nhồi vào `PresetProcessingSettings`:

```sql
CREATE TABLE IF NOT EXISTS BeautySettings (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    Enabled INTEGER NOT NULL DEFAULT 0,
    SmoothSkin INTEGER NOT NULL DEFAULT 0,
    BrightenSkin INTEGER NOT NULL DEFAULT 0,
    SkinTone INTEGER NOT NULL DEFAULT 0,
    Sharpen INTEGER NOT NULL DEFAULT 0,
    EyeSize INTEGER NOT NULL DEFAULT 0,
    SlimFace INTEGER NOT NULL DEFAULT 0,
    ModifiedAtUtc TEXT
);
```

Repository phải clamp `0..100`. Migration/`EnsureColumn` phải giữ database cũ chạy được. Export/import settings hiện chỉ serialize `Settings`, nên cần mở rộng `SettingsTransferService` sang envelope có version để beauty settings được backup/restore; vẫn đọc được file export cũ.

Nếu về sau cần beauty khác nhau theo sự kiện/preset, chuyển sang `PresetBeautySettings(PresetId, ...)` ở phase sau. Không nên làm ngay vì UI được yêu cầu là trang global và luồng capture hiện chỉ chọn một preset chủ yếu cho LUT.

## 6. Thiết kế thuật toán

### 6.1 Face detection và landmarks

OpenCvSharp là wrapper xử lý ảnh, không tự cung cấp model landmark đã huấn luyện. Cần chọn và đóng gói model riêng.

Phương án đã khóa cho phase hiện tại:

- detection/landmark chạy trên ảnh thu nhỏ, cạnh dài khoảng `960–1280 px`;
- model chạy offline, không gửi ảnh ra mạng;
- dùng Haar Cascade `frontface.xml` để tạo face ROI và OpenCV contrib `FacemarkLBF` với model đóng gói local `lbfmodel.yaml` để lấy 68 landmark;
- model file được đóng gói local trong runtime data/asset, không đặt trong Camera SDK và không tải từ Internet khi ứng dụng chạy;
- warm-up model lúc khởi tạo trang Customer mode hoặc trước shot đầu tiên, không block UI thread;
- nếu không tìm thấy mặt, trả ảnh gốc và `FacesDetected=0`.

Haar rectangle chỉ dùng làm đầu vào cho `FacemarkLBF`, không dùng trực tiếp làm skin mask. Landmark LBF đã chạy thành công trong harness `net48/x86`; vẫn cần UAT ảnh booth cho mặt nghiêng, kính, che khuất và thiếu sáng. File mẫu tải về đang có tên `LFBmodel.yaml`; khi đóng gói đổi sang tên chuẩn `lbfmodel.yaml` và cấu hình loader theo đúng tên duy nhất này.

### 6.2 Skin mask

Mỗi mặt tạo mask từ landmark oval, sau đó loại trừ:

- mắt và lông mày;
- môi;
- lỗ mũi nếu landmark/model cho phép;
- vùng ngoài face oval.

Kết hợp thêm điều kiện màu da ở YCrCb hoặc HSV để tránh làm mịn tóc/nền. Mask cần erode nhẹ rồi Gaussian feather; hợp nhất nhiều mặt bằng `max`, không cộng cường độ để tránh vùng overlap bị xử lý hai lần.

### 6.3 Các bước retouch

1. **Bilateral smoothing:** chạy trên ROI quanh từng mặt hoặc ảnh downscale/upscale có kiểm soát; blend chỉ qua skin mask.
2. **Brighten skin:** tăng luminance ở Lab/YCrCb, không cộng đều RGB để tránh cháy màu.
3. **Skin tone/reduce redness:** giảm có giới hạn kênh đỏ/chroma trên skin mask; giữ hue tự nhiên và không “whiten” ngoài da.
4. **Local contrast nhẹ:** CLAHE hoặc unsharp luminance với clip limit thấp, không chạy mạnh trên da.
5. **Sharpen eyes/hair:** eye mask lấy từ landmark; hair mask không thể suy ra đáng tin cậy từ landmark mặt. Phase 1 nên sharpen vùng mắt + vùng ngoài skin mask trong ROI với ngưỡng edge; muốn hair mask chính xác cần segmentation model riêng ở phase 2.
6. **Blend với ảnh gốc:** output cuối của retouch luôn là `original * (1-alpha) + processed * alpha` theo mask và slider.

### 6.4 Mapping slider

UI hiển thị số nguyên `0..100`; thuật toán nhận `0..1` và dùng curve để vùng thấp dễ tinh chỉnh:

```text
s = clamp(slider / 100.0, 0, 1)
effective = s * s * (3 - 2*s)   // smoothstep
```

Mapping ban đầu để tuning, không phải hằng số cuối cùng:

| Slider | Điều khiển | Mức 100 vẫn phải giới hạn |
|---|---|---|
| Smooth Skin | bilateral sigma + alpha của skin mask | giữ texture; không blur mắt/môi |
| Brighten Skin | tăng L/Y + blend | khoảng tối đa 0.3–0.5 EV tương đương |
| Skin Tone | giảm redness/chroma + cân hue | không thay màu da ngoài mask |
| Sharpen | unsharp amount trên eye/edge mask | không tạo halo; không sharpen skin noise |

`Contrast nhẹ` là thành phần nội bộ phụ thuộc Smooth/Sharpen, không thêm slider riêng. `Eye Size` và `Slim Face` là geometric warp nhẹ dựa trên landmark; giá trị 0 không remap ảnh. Preset `SharpenProcessor` hiện có vẫn là sharpen toàn ảnh composite; cần ghi rõ trong UI hoặc cân nhắc mặc định nó về 0 để tránh sharpen hai lần.

## 7. Trang Beauty mới trong Admin

Thêm route `beauty` vào `MainViewModel` và button `Beauty` vào sidebar `MainWindow.xaml`. Tạo:

```text
PhotoBooth.Admin.UI/Views/BeautyView.xaml(.cs)
PhotoBooth.Admin.UI/ViewModels/BeautyViewModel.cs
```

Trang gồm:

- toggle `Bật Beauty Retouch`;
- sáu slider `0–100`, hiển thị giá trị `%`;
- nút `Lưu` và `Khôi phục mặc định`;
- trạng thái model/runtime: Ready, Disabled, Missing model, Native runtime error;
- preview before/after từ một ảnh do Admin chọn, debounce khoảng `250–400 ms`;
- nút giữ chuột hoặc split preview để so sánh ảnh gốc/đã xử lý;
- cảnh báo rằng thay đổi áp dụng cho **ảnh chụp tiếp theo**, không sửa lại ảnh đã chụp.

Không lưu DB ở mỗi tick của slider. ViewModel cập nhật local state, preview có cancellation/debounce, và chỉ persist khi bấm `Lưu` (hoặc sau debounce dài nếu UX yêu cầu autosave). Preview chạy background, không block UI thread, và chỉ kết quả request mới nhất được hiển thị.

Mặc định triển khai an toàn:

```text
Enabled = false
Smooth Skin = 25
Brighten Skin = 10
Skin Tone = 10
Sharpen = 15
```

Feature tắt mặc định cho phép deploy runtime/model trước, chạy smoke test, rồi mới bật tại kiosk.

## 8. Quản lý file và chất lượng ảnh

- Retouch ghi ra file tạm mới; không sửa input trong lúc native pipeline đang chạy.
- Chỉ replace `pictureDestination` sau khi encode thành công.
- Giữ DPI và orientation đã được normalize; metadata EXIF cần có policy rõ ràng. Ít nhất giữ DPI, không yêu cầu giữ thumbnail/GPS.
- JPEG hiện được encode lại bởi LUT. Để tránh hai lần lossy encode, phương án tốt hơn là retouch ra PNG/temp hoặc mở rộng pipeline để beauty và LUT cùng làm việc trên một buffer rồi encode JPEG quality 100 một lần. Đây là optimization phase 2; phase 1 ưu tiên đúng hành vi và atomic file handling.
- Ước lượng RAM trước khi chạy full-resolution: một ảnh 6000×4000 BGRA khoảng 96 MB cho mỗi `Mat`; nhiều bản clone có thể vượt giới hạn address space x86. Phải xử lý theo ROI, tái sử dụng buffer và giới hạn concurrency.
- Không giữ ảnh/landmark trong log. Log chỉ duration, kích thước ảnh, số mặt, mức config và mã lỗi.

## 9. Danh sách thay đổi dự kiến theo project

| Project/file | Thay đổi |
|---|---|
| `PhotoBooth.Core/Models` | `BeautySettings`, `BeautyRetouchResult` |
| `PhotoBooth.Core/Services` | `IBeautySettingsService`, `IBeautyRetouchService`, health/status contract nếu cần |
| `PhotoBooth.Database` | bảng/repository BeautySettings và migration tương thích DB cũ |
| `PhotoBooth.Business/Pipelines/CapturePipeline.cs` | gọi retouch trước `ApplyCaptureAsync` |
| `PhotoBooth.OpenCvRetouch` (mới, khuyến nghị) | OpenCvSharp adapter, detector, mask builder, processor, model loader |
| `PhotoBooth.Infrastructure/DependencyInjection.cs` | đăng ký đúng một implementation/service |
| `PhotoBooth.Admin.UI/ViewModels/MainViewModel.cs` | route `beauty` |
| `PhotoBooth.Admin.UI/MainWindow.xaml` | menu Beauty |
| `PhotoBooth.Admin.UI/Views` | trang Beauty và preview |
| `PhotoBooth.Admin.UI/App.xaml.cs` | đăng ký ViewModel/View nếu composition hiện tại yêu cầu |
| `PhotoBooth.Infrastructure/Services/SettingsTransferService.cs` | export/import beauty có version |
| `PhotoBooth.UnitTests` | test config, pipeline order, fail-open, mask/slider invariants |
| `PhotoBooth.sln`, installer, notices | project/package/native/model asset và license |

## 10. Test và tiêu chí nghiệm thu

### 10.1 Compatibility spike bắt buộc

Tạo console/test harness `net48-x86` tối thiểu, dùng đúng package dự kiến và chạy trên máy kiosk:

1. load `OpenCvSharpExtern.dll`;
2. in `Cv2.GetVersionString()`;
3. decode JPEG mẫu;
4. chạy `Cv2.BilateralFilter`, `Cv2.CvtColor`, `Cv2.GaussianBlur`, `Cv2.AddWeighted`;
5. load và infer model face/landmark;
6. encode ảnh;
7. kiểm tra process không tăng native memory qua 100 vòng;
8. chạy lại từ output của installer sạch, không chỉ từ `bin/Debug`.

Spike chỉ pass khi xác nhận DLL native là x86, mọi dependency được đóng gói, và không có `BadImageFormatException`/`DllNotFoundException`.

### 10.2 Unit/integration tests

- slider được clamp `0..100` và serialize/deserialize đúng;
- `Enabled=false` hoặc sáu slider bằng 0 không đổi ảnh và không gọi detector;
- pipeline order bằng fake service: `Finalize -> Beauty -> LUT -> AddCapturedShot`;
- beauty failure dùng ảnh gốc, vẫn chạy LUT và hoàn tất capture;
- cancellation không replace file bằng output dở;
- không có mặt thì ảnh không đổi;
- nhiều mặt không làm overlap tăng gấp đôi cường độ;
- mask không phủ mắt/môi;
- DB cũ được nâng cấp mà không mất preset/settings;
- export cũ vẫn import được; export mới giữ beauty settings;
- preview debounce/cancel không hiển thị kết quả cũ.

### 10.3 Performance gate đề xuất

Đo trên phần cứng booth thật với ảnh camera thật:

- P50/P95 thời gian detect, mask, retouch, encode riêng biệt;
- peak private bytes và native memory;
- thời gian từ shutter đến màn chọn ảnh;
- 20–100 lần chụp liên tục không tăng memory tuyến tính;
- ảnh nhiều mặt, không mặt, ngược sáng, da tối/sáng, kính, tóc che mặt.

Không đặt SLA cứng trước khi có baseline. Mục tiêu UX ban đầu hợp lý là không làm timeout workflow và hiển thị trạng thái “Đang xử lý ảnh” nếu tổng thời gian vượt khoảng 500 ms.

## 11. Lộ trình triển khai

### Phase 0 — Spike tương thích

- hoàn tất/loại bỏ clone rỗng;
- dùng OpenCvSharp `4.11.0.20250507` đã xác minh có native x86;
- xác minh package có native x86 và license;
- chọn model, kiểm tra license và DNN support;
- chạy harness trên máy dev và máy booth từ installer.

**Gate:** native runtime, model inference và memory x86 đều pass.

### Phase 1 — Vertical slice tắt mặc định

- thêm Core contracts, DB settings và Admin Beauty page;
- implement face/landmark, skin mask, smooth/brighten/tone/eye sharpen;
- chèn vào CapturePipeline trước LUT;
- fail-open, metrics/logging, health status;
- unit/integration test và camera smoke test.

### Phase 2 — Chất lượng và hiệu năng

- tuning trên bộ ảnh đại diện có đồng thuận sử dụng;
- ROI/buffer reuse, giảm số lần encode;
- preview nhanh bằng ảnh downscale;
- triển khai Beauty nhẹ cho Live View: detection/landmark định kỳ trên CPU, tái sử dụng mask giữa các lần detect, hiệu ứng GPU trước LUT và bỏ frame cũ khi quá tải;
- cân nhắc hair segmentation nếu chất lượng/latency chấp nhận được;
- cân nhắc beauty theo preset khi có yêu cầu nghiệp vụ rõ ràng.

### Phase 3 — Rollout

- deploy với `Enabled=false`;
- kiểm tra Diagnostics/runtime/model trên từng kiosk;
- bật mức thấp, theo dõi P95 và memory;
- rollback bằng toggle/feature flag, không cần gỡ package giữa sự kiện.

## 12. Các quyết định cần khóa trước khi code production

1. OpenCvSharp `4.11.0.20250507` đã vượt compatibility harness trong process `net48/x86`; còn gate artifact installer sạch.
2. Detector Haar + `FacemarkLBF` đã được khóa; còn hoàn thiện attribution/provenance trong tài liệu pháp lý phát hành.
3. Beauty là global (đề xuất phase 1) hay theo preset/sự kiện.
4. Fail-open có được chấp nhận khi model/runtime lỗi hay booth phải chặn capture.
5. Ngưỡng latency/RAM trên cấu hình máy booth thấp nhất.
6. Live View và MP4 dùng Beauty nhẹ với cùng sáu slider. GIF lấy trực tiếp các `PicturePath` đã Beauty hoàn chỉnh rồi LUT; không retouch GIF lần hai.

## 13. Kế hoạch thực thi chi tiết

Phần này là thứ tự triển khai bắt buộc. Mỗi work package phải build/test độc lập trước khi chuyển sang work package tiếp theo. Không gộp thay đổi platform x64 hoặc camera SDK. Live View/MP4 Beauty là work package riêng sau capture retouch.

### Trạng thái triển khai ngày 26/08/2026

- WP0 hoàn tất phần tự động: solution build thành công; `PhotoBooth.UnitTests` pass `66/66`. Camera smoke cần chạy thủ công với executable nên chưa thực hiện.
- WP1 hoàn tất trên máy hiện tại: harness độc lập `prototypes/PhotoBooth.OpenCvRetouch.Harness` chạy `net48/x86`, load OpenCV `4.11.0`, thực hiện 100 vòng color conversion/bilateral/blur/sharpen/blend/encode và pass. Private bytes tăng từ khoảng 17.5 MB lên 23.9 MB sau warm-up, không quan sát tăng tuyến tính trong vòng test này. Harness cũng đã load `frontface.xml` và `LFBmodel.yaml`, phát hiện 3 face ROI trong ảnh mẫu `harry-potter.jpg`, fit đúng 68 landmark cho mỗi ROI và xuất `harry-potter-landmarks.jpg`; kiểm tra nhanh cho thấy landmark bám đúng mắt, mũi, môi và jawline.
- Test trên máy booth khác được bỏ qua theo chỉ đạo.
- Model được chủ dự án chấp nhận để đóng gói local và phát hành cùng ứng dụng với attribution/provenance rõ ràng. SHA-256 không được ghi vào tài liệu công khai; nếu quy trình release cần kiểm tra integrity thì giá trị này chỉ nằm trong hồ sơ build/release nội bộ. OpenCvSharp 4.11 không có wrapper `FaceDetectorYN`, do đó phase hiện tại dùng Haar Cascade + `FacemarkLBF`.
- Trạng thái ngày 26/08/2026 trước khi triển khai production: chưa thêm OpenCvSharp vào production solution/project, chưa sửa capture pipeline, database hoặc Admin UI.
- Cập nhật ngày 27/08/2026: WP3–WP6B đã được triển khai. Có model/settings/repository SQLite mặc định tắt, provider `PhotoBooth.OpenCvRetouch` `net48/x86`, tab Admin Beauty, capture Beauty trước LUT và Live View Beauty nhẹ. MP4 buffer nhận frame Beauty nhẹ; GIF nhận `PicturePath` Beauty hoàn chỉnh + LUT.
- Unit tests pass `70/70`, full solution Debug build thành công và `PhotoBooth.Admin.UI.exe --camera-smoke` pass. Output Admin chứa cascade, `lbfmodel.yaml` và native `dll/x86/OpenCvSharpExtern.dll`.
- Việc thêm OpenCvSharp ban đầu làm phát sinh binding conflict `System.Memory`/`System.Runtime.CompilerServices.Unsafe` với SQLite. Đã thêm binding redirects có phạm vi tại executable; camera smoke sau sửa đổi pass. Các cảnh báo version conflict hiện hữu trong MSBuild vẫn cần được theo dõi ở packaging gate.
- Đang dừng tại manual gate WP6B: cần kiểm tra camera thật, FPS/latency, mask/warp Live View, MP4 và GIF. Chưa bắt đầu WP7 settings transfer/diagnostics hoặc WP8 installer.
- Tối ưu không đổi thuật toán đã áp dụng sau khi quan sát Live View giữ khoảng 30 FPS: cache JPEG output khi input/settings không đổi; cache skin/feature mask đã feather giữa các lần cập nhật landmark; chỉ tạo mask và chạy stage tương ứng với slider khác 0; cache được reset/dispose theo phiên camera. GPU vẫn đảm nhiệm render + LUT, chưa chuyển Beauty sang shader để tránh thay đổi chất lượng hình ảnh khi chưa cần thiết.

### WP0 — Khóa baseline và đo hiện trạng

**Mục đích:** có số liệu so sánh và điểm rollback trước khi thêm dependency native.

**Công việc:**

1. Ghi lại commit PhotoBooth, cấu hình máy dev và máy booth thấp nhất.
2. Chạy baseline:
   - `dotnet restore PhotoBooth.sln`;
   - `dotnet build PhotoBooth.sln --configuration Debug`;
   - `dotnet build PhotoBooth.sln --configuration Debug --platform "Any CPU"`;
   - `dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj --configuration Debug`;
   - `PhotoBooth.Admin.UI.exe --camera-smoke`.
3. Đo thời gian hiện tại từ shutter đến khi ảnh xuất hiện ở màn chọn ảnh, kích thước ảnh thật, private bytes và working set sau 20 lần chụp.
4. Chụp snapshot nội dung output/installer để so sánh native asset ở WP1 và WP8.

**Đầu ra:** bảng baseline trong pull request hoặc test report, không sửa hành vi ứng dụng.

**Gate WP0:** solution và camera smoke pass trước khi có OpenCvSharp.

### WP1 — Compatibility harness `net48/x86` độc lập

**Mục đích:** chứng minh package phát hành chạy được trong process có cùng runtime/bitness với Admin UI mà chưa tích hợp vào ứng dụng.

**Vị trí đề xuất:** `prototypes/PhotoBooth.OpenCvRetouch.Harness`; project này không được thêm vào production composition cho đến khi gate pass.

**Dependency khóa cứng:**

```xml
<PackageReference Include="OpenCvSharp4" Version="4.11.0.20250507" />
<PackageReference Include="OpenCvSharp4.runtime.win" Version="4.11.0.20250507" />
```

Chỉ thêm `OpenCvSharp4.Extensions` cùng version nếu harness thực sự cần chuyển đổi `Bitmap`; ưu tiên `Cv2.ImRead`/`Cv2.ImWrite` để giảm dependency.

**Cấu hình project:**

```xml
<TargetFramework>net48</TargetFramework>
<PlatformTarget>x86</PlatformTarget>
<Prefer32Bit>true</Prefer32Bit>
```

**Test harness:**

1. Assert `IntPtr.Size == 4`.
2. Gọi `Cv2.GetVersionString()` và xác nhận `4.11.0`.
3. Decode JPEG có kích thước tương đương camera thật.
4. Chạy BGR↔Lab/YCrCb, bilateral, Gaussian blur, CLAHE/unsharp và alpha blend.
5. Load detector/landmark model và infer ảnh 0/1/nhiều mặt.
6. Encode output, mở lại và kiểm tra width/height/channel.
7. Lặp 100 vòng, buộc dispose mọi `Mat`; ghi private bytes trước/sau.
8. Copy output sạch sang máy booth, chạy ngoài Visual Studio.

**Gate WP1:** không có lỗi native loading, inference hoạt động, memory không tăng tuyến tính, output chứa đúng `dll/x86/OpenCvSharpExtern.dll`. Nếu fail, dừng toàn bộ WP2–WP8; không thêm OpenCvSharp vào production project.

### WP2 — Chọn và quản lý model

**Mục đích:** khóa detector/landmark trước khi thiết kế mask quanh output cụ thể của model.

**Công việc:**

1. Dùng lựa chọn đã được harness xác nhận: Haar face detector + OpenCV `FacemarkLBF`, 68 landmark, CPU x86.
2. Đo accuracy thực dụng và latency với ảnh booth: mặt nghiêng, kính, nhiều mặt, da tối/sáng và thiếu sáng. ONNX chỉ là phương án thay thế nếu UAT chứng minh Haar/LBF không đạt, không còn là blocker trước khi code.
3. Ghi nguồn, version và attribution/provenance vào tài liệu pháp lý phát hành; không công khai SHA-256.
4. Chọn đường dẫn runtime dưới data/assets của PhotoBooth; model là content immutable, không lưu trong DB blob và không tải khi runtime.
5. Metadata public chỉ gồm `ModelId`, `Version`, loại input và số landmark. Dữ liệu kiểm tra integrity, nếu sử dụng, thuộc hồ sơ release nội bộ.
6. Loader validate existence và khả năng load trước khi bật Beauty; không công khai hash và không tải model từ mạng trong workflow capture.

**Gate WP2:** model và phương án attribution được chủ dự án phê duyệt; inference pass trên máy hiện tại. Test máy booth khác được bỏ qua theo chỉ đạo hiện hành.

### WP3 — Core contracts và persistence, chưa xử lý ảnh

**Files dự kiến:**

```text
PhotoBooth.Core/Models/BeautySettings.cs
PhotoBooth.Core/Models/BeautyRetouchResult.cs
PhotoBooth.Core/Services/IBeautySettingsService.cs
PhotoBooth.Core/Services/IBeautyRetouchService.cs
PhotoBooth.Core/Persistence/IBeautySettingsRepository.cs
PhotoBooth.Business/Services/BeautySettingsService.cs
PhotoBooth.Database/SqliteBeautySettingsRepository.cs
PhotoBooth.Database/SqliteDatabase.cs
PhotoBooth.Infrastructure/DependencyInjection.cs
PhotoBooth.UnitTests/BeautySettingsTests.cs
```

**Quy tắc model:**

- giá trị public là integer `0..100`;
- validate/clamp tại service boundary và DB constraint;
- `Enabled=false` là trạng thái mặc định sau migration;
- settings object không chứa `Mat`, model path tùy ý hoặc OpenCvSharp type;
- `BeautyRetouchResult` chỉ chứa metadata kỹ thuật, không chứa landmark/biometric data lâu dài.

**Migration:**

1. Tạo bảng singleton `BeautySettings` bằng `CREATE TABLE IF NOT EXISTS` trong `SqliteDatabase.Initialize`.
2. Thêm migration version kế tiếp sau version hiện có, không sửa lịch sử migration cũ.
3. Insert default row `Id=1, Enabled=0` theo cách idempotent.
4. Repository đọc thiếu row như default và save trong transaction.
5. Test mở database schema cũ, initialize hai lần và xác minh preset/session không đổi.

**Gate WP3:** build/test pass; app cũ nâng DB được; chưa có thay đổi capture output.

### WP4 — OpenCV retouch engine cô lập

**Project đề xuất:** `PhotoBooth.OpenCvRetouch/PhotoBooth.OpenCvRetouch.csproj`, target `net48/x86`. Project chỉ reference `PhotoBooth.Core` và package OpenCvSharp được khóa; không reference UI, Database, camera SDK hoặc Business.

**Cấu trúc:**

```text
PhotoBooth.OpenCvRetouch/
  OpenCvBeautyRetouchService.cs
  FaceLandmarkDetector.cs
  SkinMaskBuilder.cs
  BeautyParameterMapper.cs
  RetouchPipeline.cs
  RetouchWorkspace.cs
  ModelManifest.cs
```

**Thứ tự trong engine:**

```text
decode ảnh
  -> tạo detection image downscale
  -> detect face + landmark
  -> scale landmark về ảnh gốc
  -> tạo skin/eye/edge masks
  -> smooth skin
  -> brighten luminance
  -> reduce redness/skin tone
  -> local contrast nhẹ
  -> sharpen eye + non-skin edges
  -> composite theo mask
  -> global strength blend với original
  -> encode file tạm
  -> validate file tạm
  -> atomic replace/copy output
```

**Quy tắc implementation:**

- `SemaphoreSlim(1,1)` bao quanh native execution trong phase 1;
- mỗi `Mat`, `Net`, `CLAHE`, `InputArray` và `OutputArray` có ownership rõ và được dispose;
- detector/model được load một lần, nhưng không giữ ảnh hoặc kết quả landmark sau request;
- cancellation được kiểm tra giữa các stage; không replace output sau cancellation;
- xử lý ROI thay vì clone nhiều ảnh full-resolution;
- không gọi `Cv2.ImShow`, không mở native window;
- không log đường dẫn ảnh đầy đủ nếu log có thể upload; không log landmark.

**Fail-open boundary:** engine tự không nuốt mọi exception. Nó trả result cho trường hợp hợp lệ như “không có mặt”; lỗi native/model/I/O được ném lên orchestration để `CapturePipeline` quyết định dùng bản gốc và log warning.

**Gate WP4:** golden-image/invariant tests pass, output giữ kích thước, slider 0 tạo ảnh không đổi về pixel hoặc bỏ qua engine, memory test pass.

### WP5 — Admin Beauty page

**Files dự kiến:**

```text
PhotoBooth.Admin.UI/Views/BeautyView.xaml
PhotoBooth.Admin.UI/Views/BeautyView.xaml.cs
PhotoBooth.Admin.UI/ViewModels/BeautyViewModel.cs
PhotoBooth.Admin.UI/App.xaml
PhotoBooth.Admin.UI/App.xaml.cs
PhotoBooth.Admin.UI/MainWindow.xaml
PhotoBooth.Admin.UI/ViewModels/MainViewModel.cs
```

**Wiring chính xác theo app hiện tại:**

1. Thêm `DataTemplate` BeautyViewModel→BeautyView vào `App.xaml`.
2. Đăng ký `BeautyViewModel` singleton cạnh các page ViewModel trong `App.xaml.cs`.
3. Inject BeautyViewModel vào constructor `MainViewModel` và thêm dictionary route `beauty`.
4. Thêm menu button `Beauty` với `CommandParameter="beauty"` trong `MainWindow.xaml`.
5. Không đặt logic OpenCV hoặc DB trong code-behind.

**Hành vi ViewModel:**

- load settings async khi khởi tạo;
- giữ `SavedSettings` và `EditingSettings` riêng để hỗ trợ Cancel/Reset;
- slider clamp `0..100`;
- preview dùng ảnh Admin chọn, downscale, debounce `300 ms` và hủy request cũ;
- nút Save persist một lần; không save trên từng `ValueChanged`;
- status runtime/model riêng với message preview;
- khi rời trang với thay đổi chưa lưu, giữ nguyên state trong singleton page hoặc cảnh báo theo pattern UI hiện có.

**Gate WP5:** UI hoạt động khi OpenCV/model unavailable; vẫn lưu/tắt settings được; không block UI thread; chưa tác động capture vì `Enabled=false`.

### WP6 — Tích hợp CapturePipeline đúng thứ tự

Đây là work package đầu tiên thay đổi ảnh production.

**Constructor dependency mới:** `IBeautySettingsService` và `IBeautyRetouchService`. Cập nhật toàn bộ test tạo `CapturePipeline` bằng fake/no-op implementation.

**Thuật toán orchestration:**

```text
camera capture -> staging
FinalizeImage(staging, rawStill, autoFlip)
copy rawStill -> pictureDestination
load BeautySettings snapshot
if Enabled và có slider > 0:
    retouch pictureDestination qua file tạm
    nếu lỗi: khôi phục/giữ pictureDestination gốc, log warning
apply LUT vào pictureDestination
create video từ buffer Live View Beauty nhẹ (`rawStill` chỉ còn là input kiểm tra tồn tại)
AddCapturedShot
```

**Bất biến bắt buộc:**

- Beauty luôn trước LUT;
- video lấy buffer Live View Beauty nhẹ; không áp dụng LUT capture;
- `rawStill` không bị engine sửa;
- no-face là success/no-op, không phải error;
- Beauty error không ngăn capture, LUT, video hoặc session commit;
- LUT error giữ semantics hiện tại, không bị Beauty layer che mất;
- cleanup xóa mọi temp file cả success, exception và cancellation;
- chỉ `pictureDestination` đã hoàn tất mới được ghi vào session.

**Test order:** fake services ghi event vào list và assert chính xác `Capture → Finalize/Copy → Beauty → LUT → Video → AddCapturedShot`. Thêm test Beauty throw, cancellation và `Enabled=false`.

**Gate WP6:** toàn bộ unit test + camera smoke pass; ảnh capture thử xác nhận Beauty trước LUT bằng LUT/mask dễ nhận biết.

### WP6B — Beauty preview nhẹ cho Live View

WP này không thay đổi `ILiveViewService`/`LiveViewService`. Customer UI đưa cùng frame Beauty nhẹ vào hiển thị và `IVideoService`, đúng yêu cầu MP4 mới.

**Thiết kế triển khai:**

1. Mở rộng state hiển thị Live View (không đổi camera contract) để Beauty có thể bật độc lập với việc preset có LUT hay không.
2. `frame.ImageData` không bị sửa tại chỗ. Tạo JPEG Beauty nhẹ mới rồi dùng cho cả `LiveImage` và `videos.AddLiveViewFrame`.
3. Tạo face-analysis worker theo kiểu latest-frame-only:
   - decode/downscale frame phân tích;
   - detect + landmark khoảng mỗi `5–10` frame hoặc tối đa `3–5 Hz`;
   - hủy/bỏ kết quả cũ khi camera, kích thước hoặc phiên Live View thay đổi;
   - giữ mask/landmark gần nhất trong thời gian ngắn và làm mượt tọa độ để tránh rung;
   - không xếp hàng frame, không chạy nhiều native inference song song.
4. Bản triển khai hiện tại xử lý JPEG in-memory bằng OpenCV CPU, chỉ cho phép một request đồng thời, cập nhật Haar/LBF mỗi 6 frame và dùng cường độ rút gọn. `LiveColorSurface` tiếp tục áp LUT GPU sau khi nhận JPEG Beauty. Chuyển warp/effect sang shader chỉ thực hiện nếu manual profiling cho thấy CPU không đạt FPS yêu cầu.
5. `AutoFlip` tiếp tục là transform hiển thị hiện hành. Analysis/mask dùng cùng hệ tọa độ frame thô để toàn surface được flip nhất quán; reset cache nếu rotation/dimension đổi.
6. Khi Beauty tắt, slider đều `0`, không có mặt, processor đang bận hoặc model/native lỗi: dùng frame thô; lỗi đầu tiên vô hiệu Beauty cho phiên Live View hiện tại thay vì spam retry.

**Quyết định sau prototype:** dùng CPU in-memory làm implementation đầu tiên để bảo đảm Live View và MP4 nhận cùng pixel. Manual profiling là gate quyết định có cần chuyển effect/warp sang shader hay không.

**Test tự động:** processor in-memory không đổi kích thước, disabled/no-face/busy trả fallback, state reset đúng và dispose native resource. Customer loop chuyển đúng byte Beauty nhẹ cho cả hiển thị và video buffer.

**Manual gate:** chạy camera thật để đánh giá FPS, độ trễ, rung mask và mức tương đồng giữa preview với ảnh capture. Theo chỉ đạo hiện hành, dừng tại gate này để người dùng test; test trên máy khác được bỏ qua.

### WP7 — Settings transfer, diagnostics và observability

**Settings export/import:**

- đổi payload sang envelope versioned gồm workflow settings và beauty settings;
- import vẫn đọc được JSON legacy chỉ chứa `Settings`;
- validate toàn bộ payload trước khi save;
- nếu một phần save lỗi, tránh trạng thái half-import bằng transaction/service orchestration phù hợp.

**Diagnostics:**

- OpenCvSharp managed version;
- native OpenCV version;
- process architecture;
- model ID/version/hash status;
- Beauty enabled/disabled;
- lần chạy gần nhất: duration, faces detected và status code;
- không lưu landmark, face crop hoặc ảnh debug mặc định.

**Logging events:** `BeautySkippedDisabled`, `BeautySkippedNoFace`, `BeautyApplied`, `BeautyFailedOpen`, `BeautyModelInvalid`. Log duration theo stage ở Debug/Diagnostics, không spam Information cho từng phép toán.

**Gate WP7:** export cũ/mới round-trip, diagnostics xác định được lỗi native/model mà không cần mở log raw exception.

### WP8 — Installer và release artifact

**Công việc:**

1. Xác minh package restore copy native x86 đến output cuối cùng.
2. Xác minh Velopack/Installer không loại thư mục `dll/x86`, model và manifest.
3. Thêm OpenCvSharp/OpenCV/model license vào `THIRD_PARTY_NOTICES.md` và artifact.
4. Xác minh model tồn tại và load/inference được sau cài đặt; không hiển thị checksum trong diagnostics hoặc tài liệu công khai.
5. Test clean install trên máy không có source/NuGet cache/Visual Studio.
6. Test upgrade từ bản DB cũ và uninstall/reinstall theo quy trình hiện hành.

**Gate WP8:** `PhotoBooth.Admin.UI.exe --camera-smoke` và harness native pass từ thư mục cài đặt sạch; Beauty vẫn mặc định tắt.

### WP9 — Tuning, UAT và rollout

**Bộ ảnh UAT:** tối thiểu 0/1/2–6 mặt, khoảng cách khác nhau, mặt nghiêng, kính, tóc che mặt, ánh sáng gắt/yếu, màu da đa dạng và ảnh độ phân giải camera lớn nhất.

**Quy trình tuning:**

1. Chốt mapping slider bằng config/constants có test, không hard-code rải rác.
2. Review ở các mốc 0/10/25/50/75/100.
3. Xác nhận mức 100 vẫn không cháy da, đổi màu môi hoặc tạo halo.
4. So sánh Beauty→LUT trên tất cả LUT đang dùng; LUT không được dùng để che lỗi skin tone.
5. Đo P50/P95 và memory sau 100 capture.

**Rollout:**

- release đầu tiên `Enabled=false`;
- bật trên một booth thử nghiệm với giá trị 25/10/10/15;
- theo dõi lỗi native, P95 và memory trong ít nhất một phiên thực tế;
- mở rộng dần; rollback tức thời bằng toggle Beauty;
- nếu native load làm app không khởi động, rollback package/release chứ toggle không đủ.

**Gate WP9:** product owner duyệt ảnh, vận hành duyệt latency, không có memory growth và có quy trình rollback đã thử.

## 14. Phân chia commit và Definition of Done

### Commit boundaries đề xuất

1. `test: add OpenCvSharp x86 compatibility harness`
2. `feat(core): add beauty settings and retouch contracts`
3. `feat(database): persist global beauty settings`
4. `feat(retouch): add OpenCV beauty engine`
5. `feat(admin): add Beauty settings page and preview`
6. `feat(capture): run beauty retouch before capture LUT`
7. `feat(live): add lightweight Beauty preview before live LUT`
8. `feat(settings): export diagnostics and beauty configuration`
9. `build: package OpenCV runtime and model assets`
10. `docs: add licenses rollout and operations guidance`

Không trộn refactor không liên quan vào các commit này. Mỗi commit phải build được hoặc được giữ trong feature-disabled state rõ ràng.

### Definition of Done toàn tính năng

- [ ] OpenCvSharp/model version được khóa; dữ liệu integrity nếu có chỉ nằm trong hồ sơ release nội bộ.
- [ ] Admin có tab Beauty với toggle và sáu slider `0–100`.
- [ ] Cấu hình persist qua restart và export/import.
- [ ] Ảnh capture dùng retouch chính xác và Beauty luôn chạy trước LUT.
- [ ] Live View dùng Beauty nhẹ trước LUT, latest-frame-only và fail-open; không chạy full capture pipeline trên từng frame.
- [ ] MP4 nhận frame Live View Beauty nhẹ; GIF nhận ảnh capture Beauty hoàn chỉnh + LUT và không retouch lần hai.
- [ ] Disabled/slider 0 không decode hoặc xử lý OpenCV.
- [ ] No-face giữ ảnh gốc.
- [ ] Native/model failure fail-open và có diagnostics.
- [ ] Không có temp file sót sau success/failure/cancel.
- [ ] Build Debug/Any CPU, unit tests và camera smoke pass.
- [ ] Installer sạch chứa đúng native x86/model/license.
- [ ] 100 capture không có memory growth tuyến tính.
- [ ] UAT duyệt chất lượng với nhiều màu da/điều kiện sáng.
- [ ] Rollback bằng toggle và rollback release đã được diễn tập.

### Ngoài phạm vi phase 1

- chuyển toàn bộ PhotoBooth sang x64 hoặc .NET 8;
- Beauty toàn phần trên từng frame MP4 (MP4 chỉ dùng Beauty nhẹ);
- cloud face processing hoặc lưu biometric data;
- hair segmentation model riêng;
- Beauty theo từng preset/event;
- thay thế pipeline LUT hiện có;
- refactor các GDI+ preset processor không liên quan.

## 15. Nguồn tham khảo tương thích

- OpenCvSharp README: https://github.com/shimat/opencvsharp
- Hướng dẫn chọn package: https://github.com/shimat/opencvsharp/blob/main/docs/docfx/articles/getting-started/package-selection.md
- Native loading troubleshooting: https://github.com/shimat/opencvsharp/blob/main/docs/docfx/articles/troubleshooting/native-library-loading.md
- NuGet OpenCvSharp4: https://www.nuget.org/packages/OpenCvSharp4/

Các nguồn trên cần được kiểm tra lại tại thời điểm khóa dependency. Theo source và package đã kiểm tra, OpenCvSharp `4.11.0.20250507` hỗ trợ trực tiếp `net48` và chứa native Windows x86; Windows x86 bị loại khỏi dòng 4.13. Dòng chữ “4.12.x” trong README upstream không khớp với danh sách tag phát hành thực tế.
