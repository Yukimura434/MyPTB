# Motion Photo Development Plan

## 1. Mục tiêu

PhotoBooth phải lưu và quản lý hai nhánh đầu ra độc lập cho mỗi lần bấm chụp:

- `Picture`: ảnh JPEG đã áp LUT theo preset.
- `MotionPhoto`: Google Motion Photo không áp LUT, gồm primary JPEG và video MP4 ba giây trước thời điểm chụp.

Sau khi người dùng sắp xếp frame, hệ thống tạo thêm:

- `Composite`: ảnh ghép frame từ các `Picture`, có LUT.
- `MotionPhotoComposite`: một Motion Photo tổng hợp từ các `MotionPhoto`, không LUT.

Với `N` lần chụp, capture hoàn chỉnh phải thỏa:

```text
N Picture + 1 Composite
=
N MotionPhoto + 1 MotionPhotoComposite
```

## 2. Quy tắc màu và hình học

| Asset | LUT | Auto-flip | Nguồn |
|---|---:|---:|---|
| Picture | Có | Có | Ảnh still từ camera |
| MotionPhoto | Không | Có | Still camera + live-view raw |
| Composite | Có | Theo ảnh nguồn | Picture |
| MotionPhotoComposite | Không | Theo nguồn | MotionPhoto |

- LUT hiển thị live-view bằng GPU không được ghi vào buffer Motion Photo.
- Auto-flip là hiệu chỉnh hướng, được áp dụng nhất quán cho cả Picture và Motion Photo.
- Ảnh/video trong slot dùng crop `UniformToFill` và phủ kín 100% slot.
- Slot tròn hoặc hình đặc biệt sử dụng alpha mask của frame PNG; UI không tự thêm border hoặc bo góc.

## 3. Mô hình định danh và quan hệ

Một lần bấm chụp là một aggregate duy nhất; không quản lý bằng các danh sách path/ID song song:

```text
CapturedShot
├── Id (CapturedImageId)
├── Sequence
├── PicturePath (bắt buộc)
├── MotionPhotoPath (bắt buộc khi MediaMode=PictureAndMotion)
└── CapturedAtUtc
```

`Session.CapturedShots` là nguồn sự thật duy nhất của lượt chụp. Các projection đường dẫn legacy chỉ được phép tồn tại ở boundary tương thích dữ liệu cũ và không được dùng để cập nhật/retake.

Mỗi capture lưu snapshot chế độ tại thời điểm tạo:

- `PictureOnly`: Picture và Composite là bắt buộc; không có MotionPhoto/MotionPhotoComposite.
- `PictureAndMotion`: mỗi shot bắt buộc đủ Picture + MotionPhoto; capture bắt buộc đủ Composite + MotionPhotoComposite.

Không suy luận chế độ capture cũ từ feature flag hiện tại.

Mỗi lần bấm chụp có đúng một `CapturedImageId`. Cùng ID này sở hữu:

- Tối đa một asset `Picture`.
- Tối đa một asset `MotionPhoto`.

Mỗi capture có:

- Một `CaptureId`.
- Đúng một `Composite`.
- Đúng một `MotionPhotoComposite`.
- `Composite.SourceAssetIds` chỉ trỏ đến `Picture.Id` cùng capture.
- `MotionPhotoComposite.SourceAssetIds` chỉ trỏ đến `MotionPhoto.Id` cùng capture.
- Asset nguồn và asset dẫn xuất không được đổi chủ sở hữu capture.

Mọi asset phải có `Id`, `CaptureId`, `PhotoType`, `Position`, `LocalPath`, `MimeType`, `FileLength`, `ContentHashSha256`, `CreatedAtUtc`, `AssetStatus` và quan hệ nguồn phù hợp. Asset gốc phải có `CapturedImageId`.

## 4. Pipeline chụp nguyên tử

```text
camera staging JPEG
├── motion still: auto-flip, không LUT
│   └── MotionPhoto (primary JPEG + raw live-view MP4)
└── picture still: auto-flip, áp LUT
    └── Picture JPEG
```

Trình tự commit:

1. Chụp vào staging riêng theo attempt ID.
2. Tạo motion still không LUT.
3. Tạo Picture và chỉ áp LUT trên bản Picture.
4. Đóng gói và xác thực MotionPhoto.
5. Xác thực cả hai file, kích thước và hash.
6. Commit cặp file và `CapturedImageId` cùng transaction logic.
7. Chỉ xóa dữ liệu cũ sau khi cặp mới đã commit.

Không được giữ Picture mới với MotionPhoto cũ hoặc ngược lại.

## 5. Retake nguyên tử

Retake thay thế một `CapturedShot` bằng thao tác repository cấp aggregate (`ReplaceCapturedShotAsync`):

- Tạo `CapturedImageId` mới.
- Tạo Picture mới và MotionPhoto mới trong staging.
- Kiểm tra cả hai.
- Thay đúng vị trí trong session/workflow.
- Commit liên kết mới.
- Xóa cặp cũ sau commit.

Retake thất bại phải giữ nguyên toàn bộ cặp cũ.

## 6. Ghép ảnh tĩnh

- Màn hình frame chỉ đọc danh sách `Picture`.
- Preview và output dùng Picture đã có LUT.
- Kết quả là `Composite` PNG/JPEG.
- Source IDs chỉ gồm các Picture được gán vào slot.
- Không áp LUT lần thứ hai lên ảnh nguồn; preset hậu kỳ composite chỉ được dùng nếu được định nghĩa rõ và kiểm thử chống double-LUT.

## 7. Ghép Motion Photo

Màn hình `MotionPhotoSelection` nằm sau màn hình ghép ảnh tĩnh:

- Panel phải chỉ chứa MotionPhoto thuộc capture hiện tại.
- Cho phép gán và đổi vị trí độc lập với Picture.
- Cho phép lặp nguồn khi số slot lớn hơn số MotionPhoto.
- Mọi slot phải có nguồn trước khi hoàn tất.

Encoder tổng hợp:

1. Xác thực từng MotionPhoto nguồn.
2. Trích MP4 bằng length trong XMP container directory.
3. Chuẩn hóa 3 giây ở 18 FPS.
4. Crop từng video `UniformToFill` vào slot.
5. Phủ frame PNG theo alpha mask.
6. Tạo primary JPEG composite từ still không LUT.
7. Nhúng MP4 và ghi Google Motion Photo XMP.
8. Xác thực JPEG, XMP, `ftyp`, video length và output không rỗng.

Native encoder chạy trong tiến trình con. Tiến trình chính chỉ nhận output sau xác thực.

## 8. Database và migration

Các loại asset hợp lệ:

- `Picture`
- `MotionPhoto`
- `Composite`
- `MotionPhotoComposite`
- `Gif`
- `ShareArchive`

Migration phải:

- Không xóa hoặc đổi ID dữ liệu cũ.
- Bổ sung constraint/index mới theo cách tương thích dữ liệu cũ.
- Gắn trạng thái legacy/incomplete cho capture cũ không đủ cặp; không tạo asset giả.
- Cho phép thống kê tách asset gốc và composite.

## 9. Capture transaction

Capture `PictureAndMotion` chỉ được ghi ở trạng thái hoàn chỉnh khi có:

```text
PictureCount == MotionPhotoCount
CompositeCount == 1
MotionPhotoCompositeCount == 1
```

Repository phải ghi capture, assets và lineage trong một SQLite transaction. Nếu một insert hoặc constraint thất bại, toàn bộ transaction rollback.

Capture `PictureOnly` hợp lệ khi có đúng `N Picture`, một `Composite` và không có asset Motion Photo. Việc tắt module không được tạo path `null`, asset giả hoặc làm thay đổi luồng ảnh tĩnh.

## 9.1. Biên module Motion Photo

Toàn bộ buffer, đóng gói, ghép video và validation nằm sau `IMotionPhotoService`. Feature `MotionPhoto` quyết định capability ở workflow:

- Tắt: không buffer, không gọi encoder, bỏ qua màn hình Motion Photo và hoàn tất bằng Picture/Composite.
- Bật: yêu cầu đầy đủ MotionPhoto cho mọi `CapturedShot` và điều hướng qua màn hình ghép Motion Photo.

`MotionPhotoNativeEncoder` chỉ là cờ kỹ thuật của provider, không thay thế cờ capability `MotionPhoto`.

## 10. ZIP, QR và upload

Archive có cấu trúc logic:

```text
pictures/
motion-photos/
composites/
additional/
```

Trước khi tạo QR/upload:

- Xác minh file tồn tại, length và SHA-256.
- Xác minh Motion Photo có XMP và MP4.
- Xác minh số lượng cặp.
- Mở lại ZIP và so sánh byte từng entry với file nguồn.

Không tạo QR hoặc upload capture không toàn vẹn.

## 11. Admin diagnostics

Thống kê riêng:

- Picture gốc.
- MotionPhoto gốc.
- Composite tĩnh.
- MotionPhotoComposite.
- Capture thiếu cặp.
- Asset thiếu file hoặc sai hash.
- Lineage sai loại hoặc sai capture.

Trang diagnostics chỉ đọc SQLite; không gọi camera, máy in, storage manager hoặc process.

## 12. Checkpoint triển khai

### Gate 1 — Data model

- Migration database mới và database legacy thành công.
- Constraint nhận `MotionPhotoComposite` nhưng vẫn yêu cầu `CapturedImageId` cho Picture/MotionPhoto gốc.
- Lineage khác capture hoặc sai loại bị từ chối.

### Gate 2 — Capture pair

- Picture có LUT; MotionPhoto primary/video không LUT.
- Cả hai dùng cùng `CapturedImageId`.
- Không để lại cặp nửa hoàn tất.
- Session chỉ cập nhật qua `CapturedShot`; không có danh sách song song bị lệch.
- `PictureOnly` chạy hoàn chỉnh khi module Motion Photo bị tắt.

### Gate 3 — Retake

- Retake thay cả cặp.
- Nhiều lần retake không liên kết chéo và không tạo file mồ côi.

### Gate 4 — Static composition

- Chỉ dùng Picture.
- Không double-LUT.
- Preview giống output.

### Gate 5 — Motion composition

- Ghép nhiều MP4 vào slot thành công.
- Primary JPEG và video không LUT.
- Mask đặc biệt đúng.

### Gate 6 — Capture integrity

- Capture/assets/lineage commit cùng transaction.
- Công thức số lượng được bảo đảm.

### Gate 7 — Delivery

- ZIP giữ nguyên byte và đủ asset.
- QR/upload chỉ chạy với capture hợp lệ.

### Gate 8 — Diagnostics

- Số liệu phản ánh đúng bốn loại ảnh chính.
- Phát hiện capture legacy/incomplete mà không sửa dữ liệu ngầm.

## 13. Luật dừng

Chỉ dừng ngay tại gate hiện tại khi lỗi có mức độ đặc biệt nghiêm trọng, có khả năng phá hủy solution, dữ liệu hoặc luồng Customer chính. Lỗi biên dịch cơ học, import thiếu, test double/assertion chưa đồng bộ và lỗi cục bộ có cách sửa an toàn phải được tự động khắc phục rồi chạy lại checkpoint.

Các lỗi bắt buộc dừng:

- Build hoặc unit test cho thấy contract production bị phá vỡ trên diện rộng và không thể sửa cục bộ an toàn.
- Migration làm mất, đổi ID hoặc không đọc được dữ liệu cũ.
- Constraint không bảo vệ được ownership/pair/lineage.
- Picture và MotionPhoto không cùng `CapturedImageId`.
- LUT xuất hiện trong MotionPhoto hoặc Picture không nhận LUT khi được cấu hình.
- Retake tạo cặp chéo, asset/file mồ côi hoặc làm mất cặp cũ khi thất bại.
- Encoder crash, tạo JPEG giả, thiếu XMP/MP4 hoặc output không đọc được.
- Capture được commit khi thiếu một asset bắt buộc.
- ZIP làm thay đổi byte, thiếu asset hoặc QR/upload chạy trước validation.
- Cần thay đổi kiến trúc/public contract ngoài phạm vi tài liệu mà chưa đánh giá tác động.

Không kích hoạt luật dừng:

- Thiếu `using`, typo hoặc lỗi biên dịch cơ học.
- Test double chưa cập nhật theo contract đã chủ động thay đổi.
- Assertion cũ không còn phản ánh data model mới.
- Cảnh báo tooling, rule set hoặc vulnerability feed không truy cập được nhưng build vẫn có thể kiểm chứng offline.
- Lỗi cục bộ có nguyên nhân rõ ràng, không ghi dữ liệu thực và có bản sửa nhỏ, có thể kiểm thử.

Khi dừng phải báo: gate, lỗi tái hiện, dữ liệu bị ảnh hưởng, thay đổi đã thực hiện và phương án sửa. Không che lỗi bằng fallback tạo dữ liệu giả.

## 14. Tiêu chí hoàn tất

- Build toàn solution thành công.
- Toàn bộ unit/integration test đạt.
- Camera smoke test độc lập phần cứng đạt.
- Kiểm thử camera thật xác nhận LUT/non-LUT.
- Capture mới thỏa công thức số lượng và lineage.
- Retake, ZIP, QR và restart giữa workflow không phá toàn vẹn dữ liệu.

## 15. Nhật ký triển khai

### Quyết định tích hợp — 2026-08-25

Motion Photo được tích hợp trực tiếp vào solution và workflow Customer hiện tại. Không tạo executable, database, lifecycle hoặc deployment module riêng. Các interface/service hiện có vẫn là ranh giới kỹ thuật để kiểm thử và cho phép tắt tính năng bằng feature flag, nhưng không được phép làm phân mảnh transaction, ownership hay state của một lượt chụp.

Ưu tiên thực thi:

1. Tính toàn vẹn `CapturedShot` và luồng Customer hiện hữu.
2. Picture-only phải tiếp tục hoạt động khi Motion Photo tắt hoặc không khả dụng trước khi bắt đầu lượt chụp.
3. Một lượt đã chụp ở `PictureAndMotion` không được âm thầm hạ cấp thành Picture-only giữa chừng.
4. Không tách thêm project/process/module nếu không giải quyết một rủi ro đã đo được.

### 2026-08-25 — Dừng tại Gate 2

Đã triển khai:

- Thêm aggregate `CapturedShot` và dùng `Session.CapturedShots` làm nguồn sự thật.
- Pipeline tạo Picture đã áp LUT và Motion Photo từ still sạch LUT, cùng một ID.
- Repository SQLite có thao tác thêm/thay thế cặp ở cấp aggregate.
- Context và luồng UI chính đã chuyển sang quản lý cặp thay vì hai danh sách đường dẫn song song.
- `CaptureService` nhận `CapturedShot`, kiểm tra đủ cặp và đóng dấu `MediaMode`.
- Build riêng Core/Business/Database thành công; build các project ứng dụng, Infrastructure, Customer UI và Admin UI trong solution thành công.

Lỗi tái hiện tại checkpoint:

```text
dotnet build PhotoBooth.sln --configuration Debug
CS0535 CapturePipelineTests.FakeSessionRepository chưa triển khai
ISessionRepository.AddCapturedShotAsync và ReplaceCapturedShotAsync.
```

Phạm vi dữ liệu bị ảnh hưởng: không có database thực hoặc dữ liệu người dùng nào được ghi/sửa trong checkpoint này. Lỗi chỉ nằm ở test double tại thời điểm biên dịch project unit test.

Trạng thái: **HALTED — Gate 2 chưa đạt** theo luật “build hoặc unit test thất bại”. Chưa triển khai Gate 3 trở đi.

Phương án tiếp tục sau khi được cho phép: nâng fake repository và test assertions sang `CapturedShot`, bổ sung test thay cặp nguyên tử, chạy lại toàn solution và toàn bộ unit test. Chỉ khi Gate 2 xanh mới chuyển sang Gate 3.

### 2026-08-25 — Tiếp tục Gate 2, dừng tại checkpoint test lần 2

Đã hoàn tất phần việc được nêu ở checkpoint trước: `FakeSessionRepository` và assertions đã chuyển sang `CapturedShot`, bao gồm thao tác thay thế cặp.

Lỗi tái hiện mới:

```text
dotnet test PhotoBooth.UnitTests\PhotoBooth.UnitTests.csproj --configuration Debug
CS1061: IReadOnlyList<CapturedShot> không tìm thấy Concat/Where/Select
trong CapturePipelineTests.cs.
```

Nguyên nhân: file test chưa import namespace `System.Linq` sau khi fake repository sử dụng các phép chiếu collection. Production projects không phát sinh lỗi mới; không có dữ liệu thực bị thay đổi.

Trạng thái lịch sử: checkpoint này từng dừng theo luật cũ. Theo luật dừng đã hiệu chỉnh, đây là lỗi cơ học phải tự sửa và không còn là điều kiện dừng.

### 2026-08-25 — Hoàn tất phạm vi tự động

Kết quả triển khai:

- Gate 1: schema/constraint/legacy migration và `MediaMode` migration version 7 đã có.
- Gate 2: `CapturedShot` là nguồn sự thật; Picture nhận LUT, Motion Photo lấy still sạch LUT; feature-off chạy Picture-only.
- Gate 3: retake thay nguyên cặp bằng transaction repository; test xác nhận không còn ID/cặp cũ.
- Gate 4: static frame tiếp tục dùng Picture đã xử lý LUT; preview/output dùng cùng cơ chế crop phủ kín slot.
- Gate 5: Motion Photo composite dùng primary composite dựng từ primary JPEG sạch LUT của các Motion Photo, video từng slot chuyển động; encoder cố định 18 fps trong 3 giây.
- Gate 6: capture `PictureAndMotion` bảo đảm `N Picture + 1 Composite = N MotionPhoto + 1 MotionPhotoComposite`, cùng `CapturedImageId` và lineage đúng loại.
- Gate 7: integrity validator chạy trước tạo ZIP/QR; ZIP được mở lại và so sánh SHA-256 từng entry với file nguồn.
- Gate 8: thống kê SQLite và constraint lineage tiếp tục được kiểm thử; dữ liệu legacy không bị tự sửa thành asset Ready.

Xác minh:

```text
dotnet build PhotoBooth.sln --configuration Debug
Build succeeded, 0 errors.

dotnet test PhotoBooth.UnitTests\PhotoBooth.UnitTests.csproj --configuration Debug
Passed: 59, Failed: 0.

PhotoBooth.Admin.UI.exe --camera-smoke
Camera smoke passed.
```

Cảnh báo `NU1900` do vulnerability feed không truy cập được và cảnh báo ruleset legacy không ảnh hưởng kết quả build/test.

Phần chưa thể tự động xác nhận trong môi trường không điều khiển phần cứng: chụp bằng camera thật và đối chiếu trực quan Picture có LUT/Motion Photo không LUT trên thiết bị Android hỗ trợ Motion Photo. Đây là bước nghiệm thu phần cứng, không phải lỗi chặn solution.

### 2026-08-25 — Ổn định native encoder cho Motion Photo tổng hợp

Lỗi `AccessViolationException` tại `sws_scale` được xác định xảy ra khi tiến trình con vừa giữ nhiều `VideoFileReader` vừa ghi bằng `VideoFileWriter`, đặc biệt khi cùng một Motion Photo được gán cho nhiều ô và chiều rộng bitmap 24-bit không chia hết cho 4.

Đã xử lý theo pipeline hai pha: mỗi nguồn chỉ mở một decoder, dựng đủ 54 frame tổng hợp trong bộ nhớ quản lý, đóng toàn bộ decoder rồi mới mở encoder. Kích thước video được căn theo bội số 4 và tỉ lệ crop dùng scale độc lập theo hai trục. Không thay đổi dữ liệu capture, liên kết asset hay luồng Customer chính.

Xác minh: toàn solution build thành công và 59/59 unit test đạt.
