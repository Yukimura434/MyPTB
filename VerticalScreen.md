# Kế hoạch triển khai Customer UI màn hình dọc

## 1. Mục tiêu

Triển khai chế độ hiển thị dọc chuyên nghiệp cho **PhotoBooth Customer UI** mà không tạo thêm một ứng dụng, solution hoặc luồng nghiệp vụ độc lập.

Chế độ dọc phải:

- Chỉ áp dụng cho Customer UI; Admin UI luôn giữ layout quản trị dành cho desktop.
- Có thể được chọn từ Admin bằng tùy chọn `Ngang` hoặc `Dọc`.
- Luôn hiển thị theo tỷ lệ dọc khi Admin chọn `Dọc`, bất kể màn hình vật lý hoặc Windows đang báo hướng ngang hay dọc.
- Khi Customer chạy fullscreen trên màn hình ngang, hiển thị một viewport dọc căn giữa; phần còn lại dùng nền an toàn thay vì kéo giãn hoặc đổi về layout ngang.
- Có bố cục được thiết kế riêng cho không gian dọc, không chỉ chuyển nguyên các cột ngang thành các hàng một cách cơ học.
- Giữ đầy đủ mọi chức năng của chế độ ngang.
- Dùng chung ViewModel, command, state machine, service, pipeline và model với chế độ ngang.
- Giữ cùng design language: typography, màu, khoảng cách, button, card, trạng thái và cách phản hồi thao tác.
- Không làm tăng đáng kể chi phí bảo trì hoặc nâng cấp sau này.

## 2. Phạm vi

### Trong phạm vi

- Thiết lập chế độ Customer `Landscape` / `Portrait` tại Admin.
- Thiết lập `Rotate Live View 180°` độc lập với `AutoFlip` hiện có.
- Shell Customer và toàn bộ màn hình Customer:
  - Waiting.
  - Capture / Live View.
  - Countdown / Smile / Inter-shot delay.
  - Review / Retake.
  - Frame selection / Photo assignment.
  - Printer connection.
  - Printing.
  - Complete / GIF / QR.
  - Error, reconnect và các overlay trạng thái.
- Xử lý fullscreen dọc trên màn hình vật lý ngang.
- Migration SQLite và tương thích cấu hình cũ.
- Kiểm thử layout, luồng hoạt động và chuyển đổi Admin → Customer.

### Ngoài phạm vi

- Không thay đổi state machine Customer.
- Không thay đổi quy tắc capture, frame composition, preset, QR, upload hoặc print pipeline.
- Không thay đổi thuật toán xử lý ảnh.
- Không tạo `PhotoBooth.Customer.Portrait.UI` hoặc solution thứ hai.
- Không xoay file ảnh thành phẩm khi bật `Rotate Live View 180°`; tùy chọn này chỉ tác động phần xem camera.
- Không áp dụng chế độ dọc cho Admin UI.

## 3. Quyết định kiến trúc

### 3.1. Một Customer UI, hai layout

Giữ nguyên `PhotoBooth.Customer.UI` và toàn bộ ViewModel hiện tại. Mỗi màn hình có thể cung cấp hai `DataTemplate` hoặc hai view layout:

```text
CustomerShellViewModel
        │
        ├── Landscape DataTemplate
        │       └── Landscape view/layout
        │
        └── Portrait DataTemplate
                └── Portrait view/layout

Hai layout dùng chung chính ViewModel, command và service.
```

Không nhân đôi ViewModel. Không tạo state machine dọc. Không dùng điều kiện `if portrait` trong nghiệp vụ.

### 3.2. Chia sẻ component thay vì sao chép toàn bộ XAML

Các thành phần sau phải được đưa về resource hoặc control dùng chung khi xuất hiện ở cả hai layout:

- Primary, secondary, ghost và destructive button.
- Status pill, progress pill và notification banner.
- Camera overlay.
- Countdown overlay.
- Error dialog.
- Thumbnail card.
- Frame card.
- Print-copy stepper.
- Zoom controls.
- QR card.
- Admin access button.

Landscape và Portrait chỉ quyết định cách sắp xếp các component. Màu, typography, control state và interaction không được định nghĩa lại riêng rẽ nếu không cần thiết.

### 3.3. Không dựa vào hướng màn hình của Windows để chọn layout

Nguồn quyết định duy nhất là cấu hình do Admin lưu:

```csharp
public enum CustomerLayoutMode
{
    Landscape = 0,
    Portrait = 1
}
```

Windows orientation và tỷ lệ cửa sổ chỉ được dùng để tính cách đặt viewport, không được tự động đổi `Portrait` thành `Landscape`.

Điều này giải quyết trường hợp Surface Pro bị khóa orientation ngang nhưng người vận hành vẫn muốn Customer UI dọc.

## 4. Mô hình cấu hình

Bổ sung vào `Settings` và `WorkflowSettings`:

```csharp
public CustomerLayoutMode CustomerLayoutMode { get; set; }
public bool RotateLiveView180 { get; set; }
```

Giữ nguyên:

```csharp
public bool AutoFlip { get; set; }
```

Giá trị mặc định cho database cũ:

- `CustomerLayoutMode = Landscape` để không làm thay đổi hệ thống đang vận hành.
- `RotateLiveView180 = false`.

SQLite migration:

```sql
ALTER TABLE WorkflowSettings
ADD COLUMN CustomerLayoutMode INTEGER NOT NULL DEFAULT 0;

ALTER TABLE WorkflowSettings
ADD COLUMN RotateLiveView180 INTEGER NOT NULL DEFAULT 0;
```

Phải dùng cơ chế `EnsureColumn` hiện có để migration có thể chạy lặp lại an toàn.

## 5. Thiết lập tại Admin

Thêm vào khu vực Setting của Admin Home:

- `Bố cục màn hình Customer`
  - `Ngang`.
  - `Dọc`.
- Checkbox `Tự động lật ảnh xem trước` — binding với `AutoFlip` hiện có.
- Checkbox `Xoay Live View 180°` — binding với `RotateLiveView180`.

Yêu cầu UX:

- Có mô tả ngắn: “Chế độ dọc luôn giữ viewport dọc kể cả khi màn hình Windows đang nằm ngang.”
- Khi đổi cấu hình trong lúc Customer đang mở, áp dụng từ lần mở Customer tiếp theo; không hot-swap giữa một phiên chụp.
- Không thêm thiết lập dọc vào layout hoặc navigation của Admin.

## 6. Portrait viewport và fullscreen

### 6.1. Portrait design canvas

Thiết kế portrait dựa trên canvas logic `1080 × 1920` với tỷ lệ `9:16`.

Không khóa pixel tuyệt đối cho control. Canvas logic được scale đồng nhất bằng `Viewbox` hoặc một host tương đương:

```text
Customer Window
└── Safe background fills physical display
    └── PortraitViewportHost (9:16, centered)
        └── Customer content 1080 × 1920
```

### 6.2. Màn hình vật lý dọc

- Viewport dọc mở rộng tối đa trong client area.
- Giữ tỷ lệ 9:16.
- Không cắt nội dung tương tác.
- Có safe-area cho title bar khi không chạy kiosk.

### 6.3. Màn hình vật lý ngang nhưng cấu hình dọc

- Customer vẫn dùng layout dọc.
- Viewport 9:16 được căn giữa.
- Hai vùng bên ngoài viewport dùng:
  - Nền tối trung tính; hoặc
  - Background image đã blur/dim nếu màn hình Waiting có ảnh nền.
- Không kéo giãn viewport theo chiều ngang.
- Không crop button, text, panel hoặc vùng tương tác.
- Không tự đổi sang layout ngang.

### 6.4. Fullscreen

Fullscreen chỉ mở rộng window ra toàn màn hình; không thay đổi mode layout.

```text
Mode = Portrait + Fullscreen + Physical landscape
=> Fullscreen window + centered portrait viewport
```

Không lấy `ActualWidth > ActualHeight` làm lý do chuyển về landscape.

## 7. Transform Live View

### 7.1. Thứ tự transform

Live View sử dụng một `TransformGroup` dùng chung cho CPU image và GPU color surface:

1. `ScaleTransform` theo `AutoFlip`.
2. `RotateTransform` theo `RotateLiveView180`.

```text
AutoFlip = false, Rotate180 = false  => bình thường
AutoFlip = true,  Rotate180 = false  => phản chiếu ngang
AutoFlip = false, Rotate180 = true   => xoay 180°
AutoFlip = true,  Rotate180 = true   => phản chiếu ngang + xoay 180°
```

`RenderTransformOrigin` phải là `0.5, 0.5`.

### 7.2. Phạm vi áp dụng

Áp dụng đồng nhất tại:

- Live View màn hình chụp.
- Live View màn hình Waiting.
- Admin Live View để người quản trị căn camera chính xác.

Không áp dụng tự động vào:

- File ảnh camera trả về.
- Thumbnail ảnh đã chụp.
- Composite cuối cùng.

Nếu sau này cần xoay ảnh đầu ra, phải tạo một cấu hình processing riêng để tránh trộn display transform với image processing.

### 7.3. Crop Live View trong portrait

- Dùng `UniformToFill` trong vùng camera.
- Cho phép crop hai cạnh theo tỷ lệ nguồn camera.
- Không letterbox bên trong vùng Live View.
- Đặt chủ thể vào vùng trung tâm an toàn.
- Overlay countdown, hướng dẫn và progress không bị rotate theo camera; chỉ lớp video bị transform.

## 8. Layout chi tiết theo màn hình

### 8.1. Waiting

Giữ nguyên đầy đủ khả năng dùng ảnh tĩnh, GIF và Live View.

Portrait layout:

- Background phủ toàn viewport.
- Vùng CTA “Chạm để bắt đầu” nằm trong lower-middle safe area.
- Live View card tùy chọn giữ tọa độ tương đối theo portrait canvas.
- Admin access được đặt ở góc trên, ngoài vùng CTA.
- Nếu chạy dọc trên màn hình ngang, background có thể mở rộng ra side area nhưng CTA chỉ nằm trong portrait viewport.

### 8.2. Capture / Live View

Portrait layout:

- Live View là vùng lớn nhất, phủ từ đầu viewport đến action tray.
- Progress/photo count là pill nổi ở trên.
- Countdown ở chính giữa Live View.
- Action tray cố định phía dưới:
  - Bắt đầu chụp.
  - Chụp lại.
  - Tiếp tục chọn frame.
- Admin access không che progress hoặc action.

Không giảm kích thước vùng chạm dưới 48 logical pixels.

### 8.3. Review / Retake

Portrait layout:

- Ảnh đang chọn chiếm phần trên và giữa.
- Thumbnail đặt thành strip cuộn ngang ở phía dưới ảnh chính.
- Chọn ảnh cần chụp lại vẫn hiển thị rõ trên thumbnail.
- Hai hành động `Chụp lại ảnh đã chọn` và `Tiếp tục chọn frame` nằm tại bottom action tray.
- Không chuyển thumbnail thành danh sách dọc chiếm phần lớn chiều cao.

### 8.4. Frame selection / Photo assignment

Portrait layout chuyên biệt:

- Header gọn: tiêu đề, trạng thái số ô đã gán và hướng dẫn.
- Preview frame chiếm vùng trung tâm lớn.
- Frame library là strip cuộn ngang phía trên hoặc ngay dưới header.
- Captured photo library là strip cuộn ngang phía dưới preview.
- Slot vẫn tương tác trực tiếp trên preview.
- Zoom `100–300%` đặt nổi ở cạnh preview.
- Khi zoom, hỗ trợ pan bằng touch.
- Bottom action tray giữ đầy đủ:
  - Bỏ ảnh khỏi ô đang chọn.
  - Quay lại.
  - Giảm/tăng số bản in.
  - Hoàn tất.

Không đặt hai thư viện thành hai panel cao cạnh nhau như layout ngang.

### 8.5. Printer connection

Portrait layout:

- Dùng flow theo chiều dọc, nhưng vẫn hiển thị cùng một màn hình:
  1. Trạng thái kết nối.
  2. Danh sách máy in cuộn ngang hoặc danh sách dọc có chiều cao giới hạn.
  3. Profile settings dạng section/card.
  4. Save/continue action tray.
- Không ẩn Paper Size, Paper Type, Quality, Copies hoặc các checkbox.
- Form chính có ScrollViewer dọc.

### 8.6. Printing và trạng thái trung gian

- Progress nằm giữa viewport.
- Nội dung trạng thái tối đa hai dòng.
- Không hiển thị card trắng toàn màn hình.
- Retry/cancel luôn nằm trong safe area.

### 8.7. Complete / GIF / QR

Portrait layout:

- GIF/composite preview ở nửa trên.
- QR card ở nửa dưới.
- Countdown và status nằm ngay dưới QR.
- Nút Hoàn tất cố định gần cạnh dưới.
- QR phải giữ hình vuông và đủ lớn để quét ở khoảng cách sử dụng thực tế.

## 9. Bảng đối chiếu chức năng

| Chức năng | Landscape | Portrait | Yêu cầu |
|---|---:|---:|---|
| Waiting image/GIF | Có | Có | Dùng chung nguồn asset |
| Waiting Live View | Có | Có | Dùng chung flip/rotate |
| Start capture | Có | Có | Cùng command |
| Countdown / Smile | Có | Có | Cùng state |
| Review ảnh | Có | Có | Portrait dùng horizontal strip |
| Chọn ảnh retake | Có | Có | Không bỏ checkbox |
| Chọn frame | Có | Có | Cùng collection và selection |
| Gán ảnh vào slot | Có | Có | Cùng command |
| Zoom / pan | Có | Có | 100–300% |
| Xóa ảnh khỏi slot | Có | Có | Cùng command |
| Chọn số bản in | Có | Có | Stepper cảm ứng |
| Kết nối máy in | Có | Có | Cùng service |
| Cấu hình profile in | Có | Có | Không lược bỏ trường |
| Print / retry | Có | Có | Cùng pipeline |
| GIF / QR | Có | Có | Cùng ViewModel |
| Admin access | Có | Có | Không che nội dung |

Mọi pull request liên quan portrait phải cập nhật bảng này nếu thêm chức năng Customer mới.

## 10. Cấu trúc mã đề xuất

```text
PhotoBooth.Customer.UI/
├── Layout/
│   ├── CustomerLayoutMode.cs
│   ├── CustomerViewportHost.cs
│   └── CustomerLayoutTemplateSelector.cs
├── Views/
│   ├── Landscape/
│   │   ├── CaptureView.xaml
│   │   ├── FrameSelectionView.xaml
│   │   ├── PrinterConnectionView.xaml
│   │   └── CompleteView.xaml
│   ├── Portrait/
│   │   ├── CaptureView.xaml
│   │   ├── FrameSelectionView.xaml
│   │   ├── PrinterConnectionView.xaml
│   │   └── CompleteView.xaml
│   └── Shared/
│       ├── WaitingContent.xaml
│       ├── CameraOverlay.xaml
│       ├── ErrorOverlay.xaml
│       ├── ThumbnailCard.xaml
│       └── PrintCopiesStepper.xaml
├── Themes/
│   └── CustomerTheme.xaml
└── ViewModels/
    └── giữ nguyên ViewModel hiện tại
```

Đây là cấu trúc mục tiêu. Không cần di chuyển tất cả file trong một commit. Thực hiện dần theo từng màn hình để giảm rủi ro.

## 11. Loại bỏ phương án responsive trước đây

Phần adaptive theo `ActualHeight > ActualWidth` đã triển khai thử nghiệm trước đây không đáp ứng yêu cầu mới vì nó phụ thuộc tỷ lệ cửa sổ.

Khi triển khai chính thức cần:

- Gỡ logic tự đổi orientation khỏi code-behind Customer views.
- Gỡ logic tự thu gọn Admin chỉ vì màn hình dọc nếu logic đó được thêm riêng cho yêu cầu Customer.
- Không áp dụng portrait layout cho Admin.
- Thay bằng template selection dựa trên `Settings.CustomerLayoutMode`.

## 12. Trình tự triển khai

### Giai đoạn 1 — Settings và migration

1. Thêm `CustomerLayoutMode` và `RotateLiveView180` vào Core model.
2. Thêm cột SQLite bằng `EnsureColumn`.
3. Cập nhật settings repository đọc/ghi hai trường.
4. Thêm option và checkbox vào Admin Home.
5. Thêm unit test cho giá trị mặc định và round-trip SQLite.

### Giai đoạn 2 — Portrait viewport host

1. Tạo `CustomerViewportHost`.
2. Hỗ trợ fixed portrait ratio 9:16.
3. Căn giữa portrait viewport khi physical screen ngang.
4. Bổ sung side-area background.
5. Bảo đảm mouse/touch coordinate chính xác sau scale.

### Giai đoạn 3 — Live View transform

1. Thêm `RotateLiveView180` vào Capture và Waiting ViewModel surface state.
2. Tạo shared transform resource/control.
3. Áp dụng cho Image và GPU `LiveColorSurface`.
4. Kiểm thử bốn tổ hợp flip/rotate.

### Giai đoạn 4 — Portrait views

Thực hiện theo thứ tự rủi ro:

1. Complete.
2. Printer connection.
3. Review.
4. Frame selection.
5. Capture / Live View.
6. Waiting.

Mỗi màn hình chỉ được coi là hoàn tất khi mapping chức năng đạt 100% so với landscape.

### Giai đoạn 5 — Shell và fullscreen

1. Customer shell đọc layout mode lúc khởi động.
2. Fullscreen không thay đổi layout mode.
3. Test màn hình vật lý ngang, dọc và Surface orientation lock.
4. Admin → Customer → Admin không làm mất camera ownership.

### Giai đoạn 6 — Cleanup và tài liệu

1. Xóa orientation code-behind thử nghiệm.
2. Gom shared styles/component.
3. Cập nhật `README.md` và developer guide.
4. Thêm checklist khi phát triển chức năng Customer mới.

## 13. Kiểm thử

### 13.1. Ma trận độ phân giải

| Physical resolution | Windows orientation | Config | Kết quả mong đợi |
|---|---|---|---|
| 1920×1080 | Landscape | Landscape | Full landscape |
| 1920×1080 | Landscape | Portrait | Portrait viewport căn giữa |
| 1080×1920 | Portrait | Portrait | Full portrait |
| 1200×1920 | Portrait | Portrait | Full portrait, giữ 9:16 safe area |
| 2160×3840 | Portrait | Portrait | Scale DPI đúng |
| 2736×1824 Surface | Landscape locked | Portrait | Portrait viewport, không tự đổi ngang |
| 1080×1920 | Portrait | Landscape | Landscape viewport letterbox nếu Admin cố ý chọn ngang |

### 13.2. Functional regression

- Camera connect/disconnect/reconnect.
- Waiting → Capture.
- Capture đủ số ảnh.
- Retake một hoặc nhiều ảnh.
- Chọn frame và gán tất cả slot.
- Zoom, pan và clear slot.
- Chọn số bản in bằng touch.
- Printer reconnect.
- Print success/failure/retry.
- QR/GIF completion.
- Timeout trở về Waiting.
- Admin access và trả quyền camera.

### 13.3. Transform tests

- Không flip, không rotate.
- Flip, không rotate.
- Không flip, rotate 180°.
- Flip và rotate 180°.
- CPU preview và GPU LUT surface phải cùng orientation.
- Overlay UI không bị rotate.

### 13.4. Touch và accessibility

- Target tương tác tối thiểu 48×48 logical pixels.
- Không có horizontal scrollbar ngoài component được thiết kế cuộn ngang.
- Text không bị cắt ở DPI 100%, 125%, 150% và 200%.
- Contrast đạt mức dễ đọc trong môi trường booth.
- Focus và keyboard vẫn hoạt động cho cấu hình/kỹ thuật viên.

## 14. Tiêu chí nghiệm thu

Chỉ nghiệm thu khi tất cả điều kiện sau đạt:

1. Admin chọn Portrait và Customer luôn mở dọc, kể cả trên Windows landscape locked.
2. Admin UI không đổi sang layout dọc.
3. Landscape Customer giữ nguyên hành vi và chức năng.
4. Portrait Customer có đủ toàn bộ chức năng trong bảng đối chiếu.
5. Không có logic nghiệp vụ bị nhân đôi giữa portrait và landscape.
6. Flip và Rotate 180° hoạt động độc lập, đồng nhất ở Capture và Waiting.
7. GPU LUT và ảnh CPU có cùng orientation.
8. Không có control bị crop ở các độ phân giải mục tiêu.
9. Build `PhotoBooth.sln` thành công.
10. Toàn bộ unit tests hiện có và tests mới đều pass.
11. Camera smoke test không hồi quy.

## 15. Rủi ro và biện pháp

### Rủi ro: XAML bị nhân đôi

Biện pháp: tách shared component và theme; portrait/landscape chỉ giữ layout composition.

### Rủi ro: GPU surface không xoay giống Image

Biện pháp: transform ở container chung hoặc bổ sung orientation property trực tiếp cho surface; kiểm thử bốn tổ hợp.

### Rủi ro: Portrait trên màn hình ngang trông nhỏ

Biện pháp: thiết kế viewport 9:16 đúng yêu cầu, dùng side-area background có chủ đích; không kéo giãn.

### Rủi ro: Thêm chức năng mới chỉ cập nhật landscape

Biện pháp: checklist bắt buộc và bảng mapping chức năng trong tài liệu này.

### Rủi ro: Hot-switch giữa phiên chụp

Biện pháp: chỉ đọc layout mode khi mở Customer; thay đổi Admin có hiệu lực ở lần mở sau.

## 16. Khả năng nâng cấp

Thiết kế này cho phép:

- Thêm theme mới mà không sửa layout selection.
- Thêm tỷ lệ portrait khác thông qua viewport host.
- Thêm component Customer mới một lần rồi đặt vào hai layout.
- Dùng lại ViewModel và automation tests cho cả hai mode.
- Tách Portrait thành package riêng sau này nếu thực sự cần mà không tách nghiệp vụ.

Nguyên tắc duy trì lâu dài:

> Một hành vi, một ViewModel, một command; nhiều layout chỉ là nhiều cách trình bày cùng hành vi đó.

