# Kế hoạch phát triển hệ thống Color/LUT

## 1. Mục tiêu và phạm vi

Xây dựng hệ thống import, quản lý, chọn và áp dụng LUT `.cube` cho live view của PhotoBooth theo các nguyên tắc:

- Admin import và quản lý LUT; Customer chỉ sử dụng preset đã được đăng ký.
- File LUT runtime nằm trong data directory dùng chung, không nằm trong source/output riêng của `PhotoBooth.Admin.UI`.
- Database là nguồn dữ liệu chuẩn cho metadata, quan hệ preset–LUT và trạng thái tài sản.
- File system là nơi giữ nội dung `.cube`; không lưu toàn bộ LUT trong SQLite và không giữ LUT không hoạt động trong RAM.
- LUT đang dùng được upload lên GPU một lần và tái sử dụng qua các frame.
- Cache GPU có giới hạn theo số entry và dung lượng, dùng LRU và giải phóng an toàn theo vòng đời GPU.
- Giữ dependency flow `UI -> Core contracts <- Business/Infrastructure/Database`.

Phạm vi chỉ hỗ trợ LUT 3D `.cube` hợp lệ. LUT 1D, những transform OCIO phức tạp hơn, config `.ocio` và nhiều transform nối tiếp không thuộc phạm vi. OpenColorIO chỉ được dùng làm tài liệu tham khảo; production renderer dùng Direct3D 11/WPF interop.

### 1.1. Quyết định kỹ thuật đã chốt

- Live view dùng GPU 3D texture với trilinear interpolation.
- Ảnh chụp từ camera sau khi về PC được áp cùng LUT bằng tetrahedral interpolation để ưu tiên chất lượng.
- Windows mục tiêu: Windows 10/11; RAM tối thiểu 8 GB; GPU DirectX 11, VRAM tối thiểu 1 GB.
- LUT thông thường tối đa 65³. Import vẫn chấp nhận LUT lớn hơn 65³ để dùng cho ảnh chụp, nhưng hard limit tuyệt đối là 128³.
- LUT lớn hơn 65³ không được đưa vào live-view GPU cache; live view phải fallback an toàn và ghi cảnh báo, còn pipeline ảnh chụp vẫn có thể dùng tetrahedral trên CPU.
- Cache ban đầu: tối đa 5 entry, 128 MiB RAM và 64 MiB VRAM. Cả ba giới hạn cùng có hiệu lực.
- LUT active luôn được pin và không được eviction. Nếu active entry làm vượt budget, hệ thống giữ entry active, evict các entry không active và báo degraded metrics thay vì dispose cưỡng bức.

## 2. Quyết định kiến trúc

### 2.1. Vị trí lưu runtime

Thư mục chuẩn:

```text
%LOCALAPPDATA%\PhotoBooth\Data
├── Assets
│   └── Presets
│       └── Cubes
│           └── <asset-id>-<hash-prefix>.cube
├── Temp
│   └── LutImports
├── Logs
└── photobooth.db
```

Đường dẫn vật lý được tạo từ `ApplicationOptions.DataDirectory`. Database chỉ lưu đường dẫn tương đối chuẩn hóa, ví dụ:

```text
Assets/Presets/Cubes/51f...-a83c920d.cube
```

Không lưu:

- đường dẫn tuyệt đối theo máy;
- đường dẫn trỏ ra ngoài data directory;
- tên file do người dùng cung cấp làm tên file vật lý duy nhất;
- binary LUT trong bảng preset.

### 2.2. Phân trách nhiệm

| Tầng | Trách nhiệm |
|---|---|
| `PhotoBooth.Core` | Model, enum, kết quả validate và contract repository/service/cache; không biết SQLite, WPF hay GPU API cụ thể |
| `PhotoBooth.Business` | Quy tắc import, duplicate, gán LUT cho preset, xóa, state transition và orchestration độc lập UI |
| `PhotoBooth.Database` | Schema, migration, transaction, constraint, query và optimistic concurrency |
| `PhotoBooth.Infrastructure` | Resolve đường dẫn an toàn, staging/copy/hash, parser `.cube`, GPU renderer/cache, startup reconciliation |
| `PhotoBooth.Admin.UI` | Chọn file, hiển thị validation/import progress, quản lý preset và thông báo lỗi |
| `PhotoBooth.Customer.UI` | Chọn/nhận preset qua Core service; không đọc trực tiếp thư mục Admin và không thao tác repository |

Không đặt parser hoặc GPU object vào model `Preset`. Không để UI gọi SQLite hoặc OpenColorIO trực tiếp.

## 3. Mô hình dữ liệu và tính toàn vẹn DB

### 3.1. Bảng `ColorLutAssets`

Đề xuất migration mới tạo bảng:

```sql
CREATE TABLE ColorLutAssets (
    Id                 TEXT PRIMARY KEY NOT NULL,
    DisplayName        TEXT NOT NULL,
    RelativePath       TEXT NOT NULL,
    ContentHashSha256  TEXT NOT NULL,
    FileLength         INTEGER NOT NULL CHECK(FileLength > 0),
    LutKind            TEXT NOT NULL DEFAULT 'Cube3D' CHECK(LutKind = 'Cube3D'),
    CubeSize           INTEGER NOT NULL CHECK(CubeSize BETWEEN 2 AND 128),
    DomainMinR         REAL NOT NULL,
    DomainMinG         REAL NOT NULL,
    DomainMinB         REAL NOT NULL,
    DomainMaxR         REAL NOT NULL,
    DomainMaxG         REAL NOT NULL,
    DomainMaxB         REAL NOT NULL,
    LiveInterpolation  TEXT NOT NULL DEFAULT 'Trilinear'
                         CHECK(LiveInterpolation = 'Trilinear'),
    CaptureInterpolation TEXT NOT NULL DEFAULT 'Tetrahedral'
                         CHECK(CaptureInterpolation = 'Tetrahedral'),
    Status             TEXT NOT NULL DEFAULT 'Ready'
                         CHECK(Status IN ('Staging','Ready','Missing','Corrupt','PendingDelete')),
    ValidationVersion  INTEGER NOT NULL DEFAULT 1 CHECK(ValidationVersion >= 1),
    LastValidatedAtUtc TEXT NOT NULL,
    CreatedAtUtc       TEXT NOT NULL,
    ModifiedAtUtc      TEXT NOT NULL,
    RowVersion         INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion >= 1),
    CHECK(length(trim(DisplayName)) > 0),
    CHECK(length(ContentHashSha256) = 64),
    CHECK(DomainMinR < DomainMaxR),
    CHECK(DomainMinG < DomainMaxG),
    CHECK(DomainMinB < DomainMaxB)
);

CREATE UNIQUE INDEX UX_ColorLutAssets_RelativePath
    ON ColorLutAssets(RelativePath);

CREATE UNIQUE INDEX UX_ColorLutAssets_ContentHash
    ON ColorLutAssets(ContentHashSha256);

CREATE INDEX IX_ColorLutAssets_Status
    ON ColorLutAssets(Status);
```

Quyết định cho bản đầu: cùng một nội dung chỉ có một asset vật lý. Import lại cùng hash trả về asset hiện có thay vì tạo bản sao. Nếu sau này cần nhiều tên hiển thị cho cùng nội dung, tách `ColorLutContents` và `ColorLutAssets`; không bỏ unique hash một cách tùy tiện.

`RelativePath` cần được chuẩn hóa ở service trước khi ghi DB. SQLite constraint không thể tự chứng minh đường dẫn sau `Path.GetFullPath` vẫn nằm trong data directory, vì vậy validation đường dẫn bắt buộc phải có ở Infrastructure và được kiểm thử riêng.

### 3.2. Quan hệ preset–LUT

Với yêu cầu một preset có tối đa một LUT hoạt động, dùng bảng liên kết 1:0/1:

```sql
CREATE TABLE PresetColorSettings (
    PresetId       TEXT PRIMARY KEY NOT NULL,
    LutAssetId     TEXT,
    Strength       REAL NOT NULL DEFAULT 1.0 CHECK(Strength BETWEEN 0.0 AND 1.0),
    Enabled        INTEGER NOT NULL DEFAULT 1 CHECK(Enabled IN (0,1)),
    ModifiedAtUtc  TEXT NOT NULL,
    RowVersion     INTEGER NOT NULL DEFAULT 1 CHECK(RowVersion >= 1),
    FOREIGN KEY(PresetId)
        REFERENCES AdminPresets(Id) ON DELETE CASCADE,
    FOREIGN KEY(LutAssetId)
        REFERENCES ColorLutAssets(Id) ON DELETE RESTRICT
);

CREATE INDEX IX_PresetColorSettings_LutAssetId
    ON PresetColorSettings(LutAssetId);
```

Không dùng `ON DELETE CASCADE` từ LUT sang preset settings vì xóa nhầm một asset không được phép âm thầm làm mất cấu hình màu của nhiều preset. Business service phải kiểm tra usage và yêu cầu tháo liên kết trước khi xóa.

Nếu không có `PresetColorSettings`, preset được hiểu là không áp dụng LUT. `LutAssetId IS NULL` cũng được phép để tắt LUT nhưng giữ strength/config cho thao tác UI; cần chọn một cách biểu diễn thống nhất trong service để tránh hai trạng thái tương đương gây rối.

### 3.3. Sửa nền tảng persistence hiện tại trước khi thêm LUT

`SqlitePresetRepository` hiện dùng `INSERT OR REPLACE` cho `AdminPresets`, sau đó ghi `PresetProcessingSettings` bằng connection riêng. Trước khi triển khai Color phải thực hiện:

1. Thay `INSERT OR REPLACE` bằng UPSERT:

   ```sql
   INSERT INTO AdminPresets (...)
   VALUES (...)
   ON CONFLICT(Id) DO UPDATE SET ...;
   ```

2. Ghi `AdminPresets`, `PresetProcessingSettings` và `PresetColorSettings` trong cùng một `SqliteConnection` và `SqliteTransaction`.
3. Truyền transaction vào tất cả command con; rollback toàn bộ nếu bất kỳ bước nào lỗi.
4. Không tự ghi DB trong `GetAllAsync`. Việc đọc hiện có thể tạo settings còn thiếu; hành vi sửa dữ liệu khi đọc phải chuyển sang migration/backfill hoặc explicit repair transaction.
5. Kiểm tra `CancellationToken` trước transaction và giữa các thao tác dài; không để cancellation tạo trạng thái nửa chừng.
6. Thêm optimistic concurrency bằng `RowVersion` cho thao tác chỉnh sửa/xóa asset và liên kết preset:

   ```sql
   UPDATE ColorLutAssets
   SET ..., RowVersion = RowVersion + 1
   WHERE Id = $id AND RowVersion = $expected;
   ```

   Nếu affected rows bằng 0, trả về conflict thay vì ghi đè thay đổi mới hơn.

### 3.4. Migration

- Tăng version trong `SchemaMigrations`; không chỉ dựa vào `CREATE TABLE IF NOT EXISTS` rải rác.
- Migration chạy trong transaction duy nhất: tạo bảng, index, backfill rồi ghi migration record.
- Migration phải idempotent ở cấp version và có test từ database trắng lẫn database legacy.
- Không dùng `DROP COLUMN` hoặc destructive migration trong feature này.
- Sau migration chạy `PRAGMA foreign_key_check`; startup phải log và dừng tính năng Color nếu có vi phạm thay vì tiếp tục dùng dữ liệu không nhất quán.
- Cân nhắc bật `PRAGMA journal_mode=WAL` và `busy_timeout` phù hợp nếu Admin/Customer có khả năng chạy thành hai process đồng thời. Không giả định transaction trong một process giải quyết được tranh chấp nhiều process.

## 4. Contract và model Core

Thêm model tối thiểu:

- `ColorLutAsset`: metadata thuần, không chứa mảng LUT hoặc GPU handle.
- `PresetColorSettings`: `PresetId`, `LutAssetId`, `Strength`, `Enabled`, `RowVersion`.
- `ColorLutMetadata`: kind, size, domain, title/comments cần thiết.
- `ColorLutValidationResult`: success, normalized metadata, warnings và danh sách lỗi có mã.
- `ColorLutAssetStatus` và `ColorLutKind` enum.

Thêm contract:

- `IColorLutAssetRepository`: query, insert, update status, usage count, delete có transaction-aware implementation.
- `IPresetColorRepository`: get/save/remove association.
- `IColorLutService`: import, list, validate, attach/detach, delete và reconcile.
- `IColorLutParser`: parse/validate stream hoặc file staging; trả dữ liệu CPU có vòng đời rõ ràng.
- `IColorLutPathResolver`: resolve relative path và chặn path traversal.
- `ILiveColorRenderer` hoặc boundary tương đương trong Core, không làm lộ Direct3D/OpenGL/OCIO types.

Không mở rộng `IPresetRepository` thành một lớp làm tất cả nếu việc đó trộn persistence preset, file import và GPU lifecycle. Business service có thể orchestration nhiều contract trong một use case.

## 5. Luồng import an toàn

### 5.1. Luồng chuẩn

```text
Admin chọn .cube
→ kiểm tra extension và kích thước tối đa
→ copy vào Temp/LutImports bằng tên ngẫu nhiên
→ đọc tuần tự + SHA-256 trong lúc staging
→ parse và validate đầy đủ
→ kiểm tra duplicate hash trong DB
→ tạo tên đích từ asset ID + hash
→ ghi DB Status=Staging trong transaction
→ atomic move staging → Assets/Presets/Cubes
→ transaction cập nhật Status=Ready + metadata cuối
→ phát event/catalog refresh
→ dispose parser và dữ liệu CPU
```

### 5.2. Giới hạn validation

Parser phải từ chối:

- file rỗng, quá giới hạn byte hoặc có dòng quá dài;
- encoding/numeric token không hợp lệ, `NaN`, `Infinity`;
- thiếu hoặc lặp directive quan trọng;
- có cả `LUT_1D_SIZE` và `LUT_3D_SIZE` trong phiên bản chưa hỗ trợ kết hợp;
- cube size ngoài giới hạn;
- số dòng sample không đúng chính xác `size` hoặc `size³`;
- sample vượt policy cho phép;
- domain min/max không hợp lệ;
- trailing data không nhận diện được nếu policy là strict.

Kích thước tối đa và cube size phải cấu hình nhưng có hard ceiling để tránh file độc hại làm cạn RAM. Parser ưu tiên đọc streaming; chỉ tạo mảng float sau khi kích thước đã được xác thực.

### 5.3. Tính nguyên tử giữa file system và DB

SQLite transaction không thể bao phủ atomic move của NTFS. Dùng state machine và thao tác bù:

- DB `Staging`, chưa được Customer sử dụng.
- Move file thành công rồi mới chuyển DB sang `Ready`.
- Nếu move thất bại: rollback/xóa row `Staging`, giữ hoặc xóa staging theo policy retry.
- Nếu process chết sau move nhưng trước `Ready`: startup reconciliation tìm row `Staging`, kiểm tra hash/file rồi hoàn tất hoặc quarantine.
- Nếu process chết sau tạo file nhưng trước tạo row: reconciliation phát hiện orphan file và chuyển vào quarantine/xóa sau retention, không tự đăng ký mù.

Chỉ asset `Ready` mới được trả về cho Customer và GPU loader.

### 5.4. Duplicate và race condition

- Unique index trên hash là lớp bảo vệ cuối.
- Hai import đồng thời cùng nội dung: transaction thắng đầu tiên sở hữu asset; transaction còn lại bắt unique conflict, xóa staging của mình và trả về asset đã tồn tại.
- Không dùng logic “SELECT rồi INSERT” như bảo đảm duy nhất; vẫn phải xử lý constraint violation.

## 6. Chọn preset và chuẩn bị GPU

```text
Customer/Admin chọn preset
→ đọc PresetColorSettings + ColorLutAssets Status=Ready
→ tạo cache key bất biến
→ lookup bounded GPU cache
→ hit: pin entry + promote LRU
→ miss: background parse file và kiểm tra hash/length
→ tạo processor/shader description
→ upload texture trên GPU/render thread
→ build hoặc reuse shader program
→ publish cache entry hoàn chỉnh
→ atomic swap ActiveLut
→ dispose dữ liệu CPU không còn cần
```

Cache key tối thiểu:

```text
LutAssetId
+ ContentHashSha256
+ Interpolation
+ renderer/backend version
+ GPU device generation
```

Không dùng display name hoặc relative path làm cache identity. Khi file bị thay đổi ngoài hệ thống, hash không còn khớp: không load, đánh dấu `Corrupt` qua reconciliation/service và giữ LUT hoạt động trước đó hoặc fallback identity.

Cache entry phải chứa trọn tài nguyên cần render:

- GPU texture hoặc texture set;
- shader/pipeline reference nếu không dùng shader LUT cố định;
- domain/uniform data;
- kích thước VRAM ước tính;
- last-used sequence;
- pin/reference count;
- device generation và trạng thái disposal.

Đối với `.cube` 3D đơn giản, ưu tiên một shader cố định và thay texture/uniform. Chỉ đưa đầy đủ OpenColorIO processor vào production sau proof-of-concept xác nhận native packaging, x86 compatibility, GPU backend và licensing. Source dưới `CameraEngine/OpenColor` hiện là tài liệu/vendor reference, không được coi là integration sẵn có.

## 7. Live view không cấp phát theo frame

```text
Camera frame
→ decode vào pooled/reusable buffer
→ update reusable input texture
→ acquire/pin snapshot của ActiveLut
→ bind shader + LUT texture
→ render vào back buffer hoặc reusable render target
→ present
→ release frame pin
```

Yêu cầu:

- Không parse file, compile shader, tạo texture hoặc mở SQLite trong frame loop.
- Không giữ cache lock trong lúc GPU draw.
- Không cấp phát bitmap/byte array/render target mới cho từng frame nếu kích thước không đổi.
- Reallocate input texture/render target chỉ khi resolution, pixel format hoặc device generation thay đổi.
- Nếu decode/upload/render khác thread, dùng double/triple buffering và cơ chế ownership rõ ràng.
- Khi LUT mới chưa sẵn sàng, tiếp tục render LUT cũ; chỉ swap sau khi entry mới hoàn chỉnh.
- Lỗi LUT phải fallback identity hoặc LUT cũ, không làm dừng live view/camera session.
- Strength được truyền bằng uniform/dynamic property; thay strength không parse hoặc upload lại LUT.

## 8. Bounded LRU và giải phóng tài nguyên

Giới hạn đồng thời:

- `MaxEntries`: mặc định 5, cấu hình 3–5;
- `MaxGpuBytes`: mặc định ban đầu 128 MiB, cần đo trên hardware mục tiêu.

Eviction:

```text
cache vượt budget
→ chọn LRU không active, pin count = 0
→ remove khỏi lookup
→ enqueue deferred disposal
→ chờ frame boundary/GPU fence
→ dispose GPU texture/pipeline thuộc sở hữu riêng
→ release descriptor/processor/CPU arrays
```

Không evict entry đang active hoặc đang được một frame giữ. Nếu mọi entry đều pinned, cache được phép tạm vượt budget và retry eviction sau frame; không dispose cưỡng bức.

Khi GPU device/context reset:

- tăng device generation;
- vô hiệu toàn bộ entry cũ;
- deferred-dispose theo API cho phép;
- reload active LUT từ file khi renderer sẵn sàng;
- fallback identity trong thời gian phục hồi.

## 9. Xóa, thay thế và phục hồi asset

### 9.1. Xóa

1. Query usage count trong DB.
2. Nếu đang được preset tham chiếu, từ chối và trả danh sách preset, hoặc yêu cầu thao tác detach rõ ràng.
3. Transaction chuyển status sang `PendingDelete` với expected `RowVersion`.
4. Invalidate GPU cache entry; chờ không còn active/pinned.
5. Move file sang `Temp/Trash` để có thể phục hồi ngắn hạn.
6. Transaction xóa row.
7. Cleanup trash theo retention.

Nếu bước 5 lỗi, trả status về `Ready`. Nếu process chết giữa các bước, reconciliation xử lý `PendingDelete` dựa trên sự tồn tại và hash file.

### 9.2. Thay file LUT

Không ghi đè nội dung file đang có. Import nội dung mới thành asset/version mới, sau đó transaction đổi liên kết preset. Điều này bảo đảm cache key bất biến, rollback dễ và Customer không đọc file đúng lúc Admin đang ghi.

## 10. Startup reconciliation và kiểm tra toàn vẹn

Thêm `IColorLutReconciliationService`, chạy sau DB initialize và trước khi Color catalog được công bố:

1. Chạy `PRAGMA foreign_key_check`.
2. Với `Ready`: resolve path an toàn, kiểm tra tồn tại, length; hash lại theo lịch hoặc khi metadata thay đổi.
3. Thiếu file: chuyển `Missing`, không xóa quan hệ preset.
4. Hash sai/parse sai: chuyển `Corrupt`, không cho load.
5. Với `Staging`: hoàn tất hoặc rollback dựa trên staging/final file và hash.
6. Với `PendingDelete`: hoàn tất delete hoặc restore `Ready` theo trạng thái file.
7. Phát hiện orphan file: chuyển quarantine; chỉ xóa sau retention và ghi log.
8. Không tự sửa silent một row có quan hệ/metadata mâu thuẫn; ghi structured diagnostic để Admin thấy và chọn repair.

Không hash lại toàn bộ thư viện ở mỗi startup nếu số lượng lớn. Có thể dùng `FileLength + LastWriteTimeUtc` làm tín hiệu nhanh, nhưng SHA-256 mới là kết luận nội dung; timestamp không phải integrity key.

Backup/restore phải bao gồm cả `Assets/Presets/Cubes` và DB. Sau restore bắt buộc chạy reconciliation trước khi bật LUT.

## 11. UI/UX Admin và Customer

### Admin/Preset Import

- File picker chỉ hiển thị `.cube` nhưng service vẫn tự validate extension/content.
- Hiển thị tiến trình: staging, validating, registering, completed.
- Hiển thị metadata: type, size, domain, hash rút gọn, dung lượng và warnings.
- Duplicate hash: thông báo “LUT đã tồn tại” và cho phép gắn asset hiện có.
- Không hiển thị đường dẫn tuyệt đối trong normal UI.
- Xóa có cảnh báo usage; không cascade âm thầm.
- Cho phép disable LUT và chỉnh strength mà không import lại.

### Customer

- Chỉ nhận preset + resolved color selection từ service.
- Không biết file do Admin import hoặc nằm trong project nào.
- Preset `Missing/Corrupt` dùng fallback identity/LUT trước đó và ghi warning có rate limit.
- Khi chuyển Customer mode, preload default/selected LUT trước khi bắt đầu live view nếu có thể.

## 12. Logging và quan sát

Log theo structured fields, không log toàn bộ LUT data:

- asset ID, preset ID, hash prefix;
- import duration, parse duration, GPU upload/compile duration;
- cache hit/miss/eviction, entry count, estimated GPU bytes;
- validation error code và line number;
- reconciliation action;
- device reset và fallback reason.

Health snapshot nên mở rộng bằng trạng thái Color renderer, active asset ID, cache usage và số asset `Missing/Corrupt`, nhưng không để health check đọc/hash file nặng trên mỗi lần gọi.

## 13. Kế hoạch triển khai theo giai đoạn

### Giai đoạn 0 — Spike và quyết định renderer

- Xác nhận live-view pixel format, GPU API/render surface hiện tại và thread sở hữu device.
- Benchmark LUT 33³, 65³, 129³ trên hardware mục tiêu.
- Dùng fixed 3D LUT shader trên Direct3D 11/WPF interop; OpenColorIO chỉ là reference.
- Xác nhận x86 Direct3D interop và không thêm OCIO native binary vào production.
- Hard limit 128³; live-view target tối đa 65³; trilinear live view; tetrahedral capture; RAM/VRAM budget 128/64 MiB.

**Gate:** demo live view áp LUT mà không tạo texture mỗi frame; device reset phục hồi được.

### Giai đoạn 1 — Củng cố DB preset

- Bỏ `INSERT OR REPLACE`.
- Gom preset + processing settings vào một transaction.
- Loại write-on-read khỏi `GetAllAsync`.
- Thêm migration tests và foreign-key integrity tests.

**Gate:** lỗi ở bất kỳ command nào rollback toàn bộ preset; không mất child row khi update.

### Giai đoạn 2 — Schema và Core contracts Color

- Thêm migration `ColorLutAssets`, `PresetColorSettings`, index và constraints.
- Thêm models/enums/contracts trong Core.
- Implement repositories và optimistic concurrency.
- Thêm backfill mặc định: preset cũ không có LUT.

**Gate:** constraint/foreign key/unique/race tests vượt qua; database legacy migrate thành công.

### Giai đoạn 3 — Storage, parser và import service

- Tạo controlled storage area `Assets/Presets/Cubes` và staging.
- Implement path resolver chống traversal.
- Implement streaming parser/validator + SHA-256.
- Implement import state machine, duplicate handling và compensation.
- Implement delete/replace semantics.

**Gate:** fault-injection ở mọi ranh giới file/DB không tạo asset `Ready` sai; restart reconcile được trạng thái dở dang.

### Giai đoạn 4 — Admin UI

- Import dialog/progress/error mapping.
- Catalog LUT, metadata, attach/detach preset, strength và delete usage guard.
- Refresh theo service result/event, không truy vấn DB trực tiếp.

**Gate:** import, duplicate, corrupt, cancel, delete-in-use và concurrent edit có UX xác định.

### Giai đoạn 5 — GPU cache và renderer

- Implement immutable GPU cache entry.
- Background parse, GPU-thread upload, atomic active swap.
- Bounded LRU theo count + bytes, pinning và deferred disposal.
- Identity fallback, device generation/reset.
- Metrics và stress test đổi preset liên tục.

**Gate:** không allocation lớn đều đặn theo frame; không use-after-dispose; memory/VRAM ổn định sau hàng nghìn lần đổi preset.

### Giai đoạn 6 — Customer integration

- Resolve default/selected preset qua Core service.
- Preload và activate LUT khi Customer mode nhận live-view ownership.
- Áp LUT cho live view theo yêu cầu sản phẩm.
- Quyết định riêng việc áp cùng LUT cho ảnh final; không mặc định suy ra preview LUT chính là output pipeline.

**Gate:** Admin ↔ Customer handoff không reconnect camera, không mất active LUT và không block UI.

### Giai đoạn 7 — Reconciliation, backup và vận hành

- Startup reconciliation và repair UI/diagnostics.
- Backup/restore coverage cho DB + cube assets.
- Retention cho staging/quarantine/trash.
- Tài liệu vận hành và migration rollback procedure.

**Gate:** restore sang máy sạch giữ nguyên quan hệ preset–LUT; missing/corrupt file không crash Customer.

## 14. Kế hoạch kiểm thử

### Unit tests

- Parser hợp lệ cho 1D/3D, domain, whitespace/comments và scientific notation.
- Sai sample count, size, numeric, domain, oversized input.
- Path normalization/traversal, case và separator Windows.
- LRU order, pinning, byte budget, active protection và device generation.
- Cache key thay đổi theo hash/interpolation/device.

### Database integration tests

- Migration database trắng và database legacy.
- `foreign_key_check` sạch sau migration.
- Không thể gắn preset/LUT không tồn tại.
- Không thể xóa LUT đang được tham chiếu.
- Xóa preset cascade đúng `PresetColorSettings` nhưng không xóa LUT.
- Duplicate hash/path bị chặn.
- UPSERT không gây delete/insert side effect.
- Preset + processing + color rollback cùng nhau khi fault injection.
- RowVersion phát hiện concurrent update/delete.
- Hai import cùng hash chỉ tạo một asset.

### File/DB consistency tests

- Crash simulation trước/sau DB staging, move file và mark ready.
- Orphan file, missing file, hash mismatch, pending delete.
- Backup/restore và đổi data directory/máy.
- Không resolve được path ra ngoài data root kể cả path có `..` hoặc separator hỗn hợp.

### GPU/live-view tests

- Texture chỉ tạo một lần trên cache miss.
- Input texture/render target được reuse.
- Đổi nhanh preset trong khi render.
- Evict entry vừa hết pin; không evict active.
- Device reset, resize, camera stop/start và Admin/Customer handoff.
- Theo dõi managed RAM, native RAM, GPU bytes và frame latency trong soak test.

## 15. Tiêu chí hoàn thành

Feature chỉ được xem là hoàn thành khi:

- LUT runtime nằm dưới data directory chung và DB chỉ lưu relative path.
- Import không tạo row `Ready` nếu file chưa tồn tại đúng hash.
- Tất cả quan hệ DB được bảo vệ bằng foreign key, check/unique constraint và transaction.
- Update preset không còn dùng `INSERT OR REPLACE` và không ghi settings qua transaction riêng.
- Customer không tham chiếu project path của Admin.
- Không parse LUT hoặc tạo/dispose GPU texture theo từng frame.
- Cache có count/byte budget, pinning, LRU và deferred disposal.
- Missing/corrupt LUT có fallback an toàn.
- Migration, parser, integrity, fault-injection, GPU lifecycle và handoff tests đều đạt.
- Backup/restore giữ được cả metadata DB và asset file, sau đó reconciliation sạch.

## 16. Nhật ký triển khai và self-check

Nhật ký này phải được cập nhật sau khi hoàn tất/self-check từng phase và trước khi bắt đầu phase kế tiếp.

| Phase | Trạng thái | Self-check/ghi chú |
|---|---|---|
| Quyết định kỹ thuật | Hoàn tất | Đã chốt 3D-only, trilinear live view, tetrahedral capture, Direct3D 11/WPF interop, 65³ thông thường/128³ hard limit, cache 5 entry/128 MiB RAM/64 MiB VRAM và active pin |
| Phase 1 — Persistence preset | Hoàn tất | Đã thay `INSERT OR REPLACE` bằng UPSERT, loại write-on-read, ghi preset + processing settings trong một transaction. Build Database đạt; 33/33 unit tests đạt. Cảnh báo cũ: NU1900 do không truy cập vulnerability feed và mismatch MSIL/x86 của PortableDeviceLib |
| Phase 2 — Schema/Core/repository | Hoàn tất | Đã thêm schema 3D-only, hard constraint 128³, unique hash/path, FK RESTRICT/CASCADE đúng chiều, RowVersion, Core models/contracts và repositories. 35/35 tests đạt, gồm integration tests mới |
| Phase 3 — Storage/parser/import | Hoàn tất | Đã thêm controlled path chống traversal, strict 3D parser 2–128³, staging/hash/deduplicate/import state machine, attach/delete guard và reconciliation. 40/40 tests đạt |
| Phase 4 — Admin UI | Hoàn tất | Đã thêm catalog/import, attach/detach, strength, metadata, cảnh báo >65³ và delete usage guard. Build toàn solution đạt; 40/40 tests đạt |
| Phase 5A — D3D11 prototype | **Đạt trên máy kiểm thử hiện tại** | Build/visual output/LUT trilinear/1.000 resource cycles/active pin/resize/minimize/restore/lock-unlock/sleep-resume đều đạt; stress cuối exit 0. Kết quả mới chứng minh trên một máy Windows 10 build 19045, chưa thay thế hardware matrix |
| Phase 5B.0 — net48/x86 compatibility prototype | **Đạt trên máy kiểm thử hiện tại** | Local bridge, shader/LUT trilinear, readback, 1.000-cycle stress và bốn process teardown đều đạt. Manual visual/resize/minimize/restore/lock-unlock/sleep-resume/close cũng đạt |
| Phase 5B — D3D11 production integration | Vẫn bị khóa | Runtime net48/x86 đã khả thi trên một máy. Còn camera harness/concurrency, máy-GPU thứ hai và thiết kế feature flag/fallback trước khi chạm production |
| Phase 6 — Tetrahedral capture/Customer | Không bắt đầu | Không tiếp tục sau safety gate Phase 5; tránh tạo hai pipeline color lệch nhau khi live renderer contract chưa được xác nhận |
| Phase 7 — Tổng kiểm | Dừng ở phạm vi Phase 1–4 + prototype 5A | Active solution trước đó build đạt, 40/40 tests đạt. Prototype 5A build 0 lỗi/0 cảnh báo và D3D hardware smoke đạt; chưa chạy camera smoke hoặc production integration |

### 16.1. Điều kiện để gỡ safety gate Phase 5 trong một lượt triển khai riêng

Chỉ tiếp tục khi có đủ:

1. Chọn và pin một interop runtime hỗ trợ đồng thời .NET Framework 4.8, x86, Direct3D 11 và WPF `D3DImage`, kèm license/redistribution rõ ràng.
2. Prototype tách biệt chứng minh shared texture D3D11 → D3D9Ex/WPF hoạt động trên Windows 10 và 11, không dùng camera SDK.
3. Automated smoke harness cho create/render/resize/device-loss/dispose ít nhất 1.000 chu kỳ.
4. Hardware test trên GPU DX11 1 GB VRAM tối thiểu và ít nhất một GPU tích hợp phổ biến.
5. Identity fallback giữ WPF live view hiện tại nếu device creation, shader compilation hoặc shared-surface setup lỗi.
6. Xác nhận native resource ownership không chạy trên camera STA và không can thiệp `CameraOperationGate`/EDSDK lifecycle.

### 16.2. Phase 5A — prototype độc lập được phép triển khai

Phạm vi prototype:

```text
prototypes/PhotoBooth.Color.D3D11.Wpf
├── net8.0-windows executable
├── không ProjectReference tới PhotoBooth.* hoặc CameraControl.Devices
├── generated/reusable test frame
├── 3D LUT 2³/33³ test asset
├── D3D11 3D texture + trilinear sampler + HLSL pixel shader
└── WPF output qua shared D3D11 texture/D3D9Ex/D3DImage
```

Source `CameraEngine/Vortice.Windows` tại commit `9e609cb9439c9872aa1b339f177e40ec96f77239` (version 3.8.3, MIT) đã có `Vortice.Wpf.DrawingSurface` và `D3D11ImageSource`. Hướng giải quyết trong source:

1. Tạo D3D11 device với `BgraSupport`.
2. Tạo output texture `B8G8R8A8_UNorm`, `ResourceOptionFlags.Shared`.
3. Lấy shared handle từ D3D11 texture.
4. D3D9Ex mở cùng handle thành render-target texture.
5. Đưa `IDirect3DSurface9` vào `D3DImage.SetBackBuffer`.
6. Mỗi frame render D3D11, `Flush`, rồi `D3DImage.AddDirtyRect`.
7. Khi WPF front buffer mất/khôi phục, dừng render và recreate/bind targets.

Prototype phải giữ fallback visual/text báo lỗi nếu D3D device không tạo được. Prototype không được thêm vào `PhotoBooth.sln`, installer hoặc DI production. Sau build/smoke, cập nhật kết quả tại bảng nhật ký nhưng dừng trước Phase 5B.

#### Kết quả self-check Phase 5A — 2026-08-18

- Đã tạo project độc lập tại `prototypes/PhotoBooth.Color.D3D11.Wpf`; project không có `ProjectReference` tới `PhotoBooth.*`, `CameraControl.Devices` và không được thêm vào `PhotoBooth.sln`.
- Đã đọc implementation local `Vortice.Wpf.DrawingSurface`, `D3D11ImageSource` và `D3D9DeviceService`. Prototype dùng đúng bridge shared texture nêu trên thông qua package `Vortice.Wpf` 3.8.3.
- Dùng package NuGet được pin `Vortice.Direct3D11`, `Vortice.D3DCompiler`, `Vortice.Wpf` version 3.8.3. Không ProjectReference trực tiếp source local vì source hiện dùng C# 14 và multi-target .NET 8/9/10, không phù hợp SDK/toolchain production.
- Target prototype: `net8.0-windows`, `win-x86`; đây là sandbox kỹ thuật, **không phải** quyết định nâng ứng dụng production khỏi .NET Framework 4.8.
- Render generated full-screen frame, tạo một `Texture3D` immutable 33³ RGBA32F, bind `SamplerDescription.LinearClamp` để lấy trilinear interpolation, và tái sử dụng surface/texture qua các frame.
- `dotnet build ... --configuration Debug --no-restore`: đạt, 0 lỗi, 0 cảnh báo.
- GUI smoke `--smoke`: cửa sổ chạy khoảng 6 giây, xác nhận ít nhất 10 frame rồi thoát với code 0 trên máy phát triển.
- Có fallback hiển thị lỗi nếu device/shader/shared surface khởi tạo thất bại; các native shader/LUT view/texture/sampler được dispose khi unload.

Kết luận: hướng `Vortice.Wpf.DrawingSurface` giải quyết được feasibility của D3D11/WPF interop trên .NET 8 và GPU hiện tại. Kết quả này **chưa đủ để tích hợp production** vì active app là .NET Framework 4.8 x86, trong khi Vortice 3.8.3 không cung cấp bằng chứng tương thích net48. Trước Phase 5B phải hoàn tất toàn bộ gate tại mục 16.1, đặc biệt runtime net48, device-loss/resize stress 1.000 chu kỳ, Windows 10/11 và GPU tối thiểu.

**Điểm dừng bắt buộc:** dừng sau Phase 5A; không thay target framework, không thêm Vortice vào `PhotoBooth.sln`, không thay live view/camera pipeline.

#### Kết quả thử stress gate — 2026-08-18

Đã mở rộng prototype với chế độ `--stress`, không thay đổi active solution. Harness thực hiện:

- 1.000 vòng create/bind/render/dispose shader-resource trong khi giữ active LUT 33³ sống và rebind sau từng vòng.
- LUT test 2³, 33³, 65³ và 128³.
- Thay đổi kích thước surface, minimize/restore cửa sổ.
- Ghi process private bytes và DXGI local-memory telemetry.

File kết quả được ghi trước teardown cho thấy: 1.000/1.000 cycles, 102 WPF frames, active LUT còn pin, đủ bốn LUT sizes; private-bytes delta `48,279,552` bytes, DXGI current-usage delta `47,685,632` bytes và observed peak `171,458,560` bytes. DXGI value là usage của adapter/process context tại thời điểm đo, không được diễn giải như dung lượng riêng của LUT cache.

Tuy nhiên process sau đó thoát bất thường với code signed `-1073740771` (`0xC000041D`). Windows Error Reporting ghi `CLR20r3`, module lỗi `Vortice.Wpf 3.8.3.0`, exception `System.NullReferenceException`. Vì crash xảy ra trong callback/teardown thay vì được fallback an toàn, gate lifecycle/cleanup **không đạt** và dữ liệu “Passed=true” trong JSON không được coi là pass toàn bài test (nó được ghi trước khi WPF teardown hoàn tất).

Các gate chưa thể xác nhận:

- device removal thật và front-buffer loss do sleep/hibernate/screensaver;
- 1.000 vòng teardown/recreate toàn bộ D3D11/D3D9Ex bridge (harness mới stress LUT resource trên một device);
- Windows 10/11 và ma trận GPU tối thiểu;
- tương thích production .NET Framework 4.8 x86;
- camera/live-view concurrency.

**Safety stop:** phát hiện crash native/WPF lifecycle là nguy cơ có thể làm sập ứng dụng PhotoBooth. Dừng hoàn toàn tại đây; không retry, không sửa production, không tiếp tục Phase 5B cho đến khi nguyên nhân teardown được điều tra trong task riêng.

#### Điều tra và bản vá resize reentrancy — 2026-08-18

Task điều tra riêng đã được cho phép. Event `.NET Runtime` ID 1026 cung cấp stack chính xác:

```text
System.NullReferenceException
at Vortice.Wpf.DrawingSurface.Render()
at Vortice.Wpf.DrawingSurface.OnRendering(...)
at System.Windows.Media.MediaContext.Resize(...)
at System.Windows.Interop.HwndTarget.OnResize()
```

Nguyên nhân trực tiếp không phải LUT parser/cache và cũng chưa có bằng chứng là double-dispose khi teardown. Harness cũ thay đổi `Width/Height` ngay bên trong `Surface_Draw`. Thao tác đó kích hoạt WPF resize đồng bộ/reentrant; `DrawingSurface.OnRenderSizeChanged()` unbind/dispose/recreate targets trong khi callback render ngoài vẫn đang chạy. Source Vortice 3.8.3 chỉ guard `ColorTexture != null` trong `Render()` nhưng sau đó dereference `ColorTextureView!`, nên có cửa sổ trạng thái target không nhất quán. `Close()` đồng bộ từ callback cũng có cùng rủi ro dispose target khi frame chưa kết thúc.

Bản vá prototype:

- không thay đổi Window state/size hoặc gọi `Close()` đồng bộ trong `Draw`/render callback;
- xếp resize và close bằng `Dispatcher.BeginInvoke` ở `Background`/`ApplicationIdle`, sau khi frame hiện tại trả về;
- đặt `stressCompleted` trước khi schedule close để callback kế tiếp không chạm GPU context;
- minimize/restore chạy qua Dispatcher + timer riêng;
- đường fallback tự động cũng schedule close thay vì đóng đồng bộ trong callback.

Kết quả xác nhận sau vá:

- build: 0 lỗi, 0 cảnh báo;
- lượt đầu: 1.000 cycles, 100 frames, đủ 2³/33³/65³/128³, active LUT pin, exit 0;
- ba process stress độc lập tiếp theo: `0, 0, 0`;
- không có Application Error/.NET Runtime/Windows Error Reporting event mới cho prototype trong cửa sổ kiểm tra;
- kết quả cuối quan sát private-bytes delta khoảng 47.7 MB; số DXGI thay đổi theo usage toàn adapter và không dùng làm leak assertion tuyệt đối.

Đánh giá: khả năng crash đã tái hiện được và caller-level fix xử lý ổn định bốn lượt trên máy phát triển. Incident cũ vẫn là bằng chứng rằng `Vortice.Wpf.DrawingSurface` nhạy với reentrant resize; production renderer phải serialize resize/render/dispose rõ ràng và không dựa riêng vào null-forgiving guards của package. Phase 5B chưa được mở.

#### Visual-output gate và depth-state fix — 2026-08-19

Manual test ban đầu cho thấy frame counter tăng nhưng surface chỉ có màu đen. GPU readback tại vùng lẽ ra có màu trả `BGRA=(10,10,10,255)`, đúng bằng clear color; do đó shared D3D11 → D3D9Ex → WPF bridge vẫn present được, nhưng draw không tạo fragment. Shader chẩn đoán bypass LUT cũng không xuất màu, loại trừ 3D LUT upload/sampling là nguyên nhân.

Nguyên nhân: `Vortice.Wpf.DrawingSurface` mặc định tạo và bind depth-stencil target `D32_Float`. Prototype không đặt depth state riêng hoặc clear depth buffer; full-screen LUT pass có Z=0 bị default depth test loại bỏ. Bản vá tạo/bind `DepthStencilDescription.None` cho color-only full-screen pass và dispose state khi unload. `CullMode.None` cũng được đặt rõ ràng để pass không phụ thuộc winding mặc định.

Sau bản vá, manual test xác nhận đúng output chẩn đoán: checker màu trực tiếp và gradient qua LUT đều hiển thị. Điều này xác nhận geometry, pixel shader, Texture3D trilinear và D3D11/D3D9Ex/D3DImage presentation cùng hoạt động. Shader sau đó được trả về chế độ toàn khung qua LUT; GPU readback một lần được giữ làm diagnostic evidence.

#### Kết thúc Phase 5A và mở compatibility probe — 2026-08-23

Người kiểm thử xác nhận các bài thủ công lock/unlock, sleep/resume và stress cuối đều đạt như mong đợi; stress process trả exit code 0. Phase 5A được đánh dấu đạt trên máy Windows 10 build 19045 hiện tại. WMI/CIM không cho phép đọc RAM/GPU trong phiên agent, vì vậy model GPU, driver và RAM cần được bổ sung thủ công khi lập hardware matrix; không suy đoán các giá trị này.

Khảo sát runtime:

- `Vortice.Wpf` được thêm vào source từ 2024 với target sớm nhất `net7.0-windows;net8.0-windows`; source local hiện target .NET 8/9/10.
- Vortice 3.8.3 chỉ chứa assembly .NET 8/9/10 nên không thể reference trực tiếp từ production net48.
- Dòng Vortice 2.4.2 cung cấp các binding D3D11/D3D9/D3DCompiler dạng `netstandard2.0`, tương thích .NET Framework 4.8, nhưng chưa có helper `Vortice.Wpf` tương ứng.
- Vì vậy Phase 5B.0 dùng Vortice 2.4.2 cho native bindings và port một bridge D3D11 shared texture → D3D9Ex → `D3DImage` tối thiểu trong prototype. Không backport toàn bộ Vortice.Wpf và không thay dependency production ở bước này.

Kết quả compatibility gate đầu tiên:

- Đã tạo executable độc lập `prototypes/PhotoBooth.Color.D3D11.Net48.Wpf`, target `net48`, `x86`, `Prefer32Bit=true`; không có ProjectReference tới PhotoBooth/camera/DB và không nằm trong `PhotoBooth.sln`.
- Ghim đồng bộ `Vortice.Direct3D11`, `Vortice.Direct3D9`, `Vortice.D3DCompiler` version 2.4.2 cùng reference assemblies net48 chỉ dùng lúc build.
- Build Debug đạt 0 lỗi, 0 cảnh báo.
- Runtime smoke trên CLR .NET Framework đạt: load binding, tạo D3D11 hardware device với `BgraSupport`, dispose context/device, process exit 0 và `probe-result.txt=PASS`.
- Tại checkpoint đầu tiên chưa tạo shared texture/D3D9Ex/D3DImage/shader/LUT; checkpoint đó đã đạt trước khi tiếp tục port bridge tối thiểu như ghi dưới đây.

#### Phase 5B.0 — bridge/LUT automated gates — 2026-08-23

Sau compatibility gate, prototype net48/x86 đã triển khai local bridge tối thiểu, giữ MIT attribution từ `Vortice.Wpf` nhưng không reference package `Vortice.Wpf` hiện đại:

```text
D3D11 device (BGRA support)
→ reusable shared B8G8R8A8 texture + render-target view
→ DXGI shared handle
→ D3D9Ex shared texture/surface
→ WPF D3DImage back buffer
→ CompositionTarget render + dirty rect
```

Các guard được thêm ngay trong bridge: teardown idempotent, render/resize/target recreation được serialize, không dispose target trong render callback, front-buffer unavailable dừng render và available recreate target, Window operation của stress được schedule qua Dispatcher. Bridge color-only không tạo/bind depth buffer; LUT pass vẫn đặt `DepthStencilDescription.None` và `CullMode.None` rõ ràng.

LUT gate đã port:

- generated gradient → immutable 33³ RGBA32F Texture3D → `LinearClamp` hardware trilinear → full-screen shader;
- vertex/pixel shader và LUT texture/view/sampler/rasterizer/depth state tạo một lần trong `LoadContent`, dispose trong `UnloadContent`;
- GPU readback frame 10 bắt buộc khác clear/black và alpha hợp lệ;
- stress 1.000 vòng create/bind/draw/dispose candidate LUT 2³/33³/65³/128³, rebind active LUT sau từng vòng và xác nhận active texture/view vẫn pin;
- resize stress schedule ngoài render callback.

Kết quả tự động:

- build Debug net48/x86: 0 lỗi, 0 cảnh báo;
- shared-bridge smoke: 417 frames, exit 0;
- LUT visual readback smoke: 467 frames, `BGRA=(117,128,139,255)`, exit 0;
- stress chính: 1.000 cycles, 100 frames, đủ 2³/33³/65³/128³, active pin true, private-bytes delta `48,943,104`, exit 0;
- ba stress process độc lập bổ sung: exit `0,0,0`; không có WER/Application Error/.NET Runtime crash mới.

**Manual gate bắt buộc:** chạy prototype net48 không có tham số và xác nhận gradient hiển thị đúng; resize/minimize/restore; lock/unlock; sleep/resume; đóng bình thường. Automated GPU readback không chứng minh người dùng nhìn thấy đúng nội dung qua `D3DImage` và không thể tự kích hoạt an toàn sleep/lock. Dừng tại đây trước manual test; không thêm prototype vào solution hay production DI.

#### Kết quả manual Phase 5B.0 — 2026-08-23

Người kiểm thử xác nhận prototype net48/x86 đạt toàn bộ manual gate trên máy hiện tại: gradient hiển thị đúng, resize, minimize/restore, lock/unlock, sleep/resume, phục hồi frame và đóng bình thường. Kết hợp với automated gates ở trên, Phase 5B.0 được đánh dấu đạt trên máy kiểm thử hiện tại.

Kết quả này chưa mở production integration vì chưa chứng minh camera callback/upload synchronization hoặc hardware matrix. Bước kế tiếp an toàn là camera harness độc lập dùng frame thật với latest-frame bounded handoff và reusable upload texture; sau đó test trên ít nhất một máy/GPU khác trước khi đưa renderer vào solution chính sau feature flag.

#### Camera harness checkpoint — 2026-08-23

Đã tạo executable độc lập `prototypes/PhotoBooth.Color.CameraHarness`, target net48/x86. Harness tham chiếu các contract `ICameraService`/`ILiveViewService` và composition Infrastructure hiện có để giữ nguyên camera SDK threading/`CameraOperationGate`, nhưng không gọi `InitializePhotoBooth()`, không migrate/ghi DB, không capture, không vào Admin/Customer workflow và không thay production DI.

Luồng checkpoint:

```text
ICameraService scan/connect
→ ILiveViewService start/get detached JPEG bytes
→ capacity-one LatestFrameSlot (Interlocked.Exchange)
→ consume latest frame only
→ stop live view
→ disconnect/cleanup
```

Harness đóng gói Canon native runtime giống executable production và asset `logo_big.jpg` mà `FakeCameraDevice` yêu cầu. Working directory được cô lập về output folder để test asset không phụ thuộc shell launch directory.

Kết quả tự động `--fake-smoke`: PASS, 373 frames published, 373 consumed, 0 error, process exit 0. Build thành công; warning còn lại là warning dependency tồn tại sẵn (NuGet vulnerability feed không truy cập được, legacy ruleset và MSIL/x86 PortableDeviceLib), không phải warning code harness.

**Manual camera gate bắt buộc:** kết nối đúng một camera test, chạy harness không tham số, bấm `Scan and start first camera`, xác nhận counter published/consumed tăng; sau đó đóng cửa sổ và xác nhận camera được stop/disconnect. Test thêm rút/cắm lại thiết bị trong lượt riêng. Dừng trước gate này: fake camera không thể chứng minh SDK/hardware frame contract. GPU JPEG decode/reusable upload/LUT presentation chỉ triển khai sau khi frame thật đạt để tránh xây pipeline dựa trên giả định sai về format/kích thước/rotation.

Manual attempt đầu hiển thị một WIA/software device ID nhưng `published=0`. Harness cũ chọn phần tử discovery đầu tiên thay vì kiểm tra capability. Đã sửa chỉ chọn device có `SupportsLiveView=true` (fake mode có fallback riêng), hiển thị tên/capability của toàn bộ device khi không có lựa chọn hợp lệ và fail sau 10 giây nếu StartLiveView không trả frame. Đồng thời sửa manual exception path luôn gọi stop/disconnect trước khi bật lại nút, tránh giữ camera bận. Fake regression sau sửa đạt 378 published/consumed frames, exit 0.

Manual webcam gate sau sửa đạt với `OsmoAction5Pro` USB/UVC: harness connect đúng physical webcam thay vì virtual device và counter published/consumed tăng liên tục (ảnh bằng chứng tại 3.568/3.568 frames). Frame contract thật vì vậy được xác nhận đủ để bắt đầu JPEG decode → reusable GPU upload → LUT presentation trong harness.

Sau hardware frame gate, camera harness đã reference bridge net48/x86 đã kiểm chứng và thêm GPU consumer:

```text
latest detached JPEG
→ WPF decoder BGRA32
→ reusable CPU pixel buffer (recreate only on resolution change)
→ reusable Dynamic B8G8R8A8 Texture2D (Map WriteDiscard)
→ Texture2D sample
→ immutable 33³ Texture3D + LinearClamp trilinear
→ shared D3D11/D3D9Ex/D3DImage target
```

Producer không còn consume frame; render callback dùng `LatestFrameSlot.Take()`, vì vậy camera polling không chờ GPU và frame cũ tự bị thay nếu renderer chậm. Input texture/SRV chỉ recreate khi kích thước camera đổi; shader/LUT/sampler/states tạo một lần theo surface lifecycle. Fake-camera GPU regression đạt: 382 published, 381 JPEG decoded/uploaded/rendered, exit 0, không error. Chênh một frame khi shutdown phù hợp capacity-one handoff.

**Manual GPU camera gate:** đóng binary harness cũ, build output chuẩn, chạy với Osmo và xác nhận hình live thật xuất hiện trong surface, có warm LUT, counter `GPU uploaded` tăng và resolution ổn định. Resize/minimize/restore rồi đóng; dừng/báo ngay nếu hình đen, sai kênh màu/orientation, flicker, counter upload đứng hoặc camera không được giải phóng.

Manual GPU camera gate xác nhận ảnh Osmo hiển thị đúng qua reusable texture và LUT, nhưng sau khoảng 2.174 frame luồng dừng với `ArgumentException` tại `LiveViewService.ExtractImage`: offset/length vượt giới hạn mảng. Đây không phải lỗi D3D/LUT. Webcam driver có thể thay `LiveViewData.ImageData` giữa các lần đọc; implementation cũ đọc property một lần để tính kích thước đích rồi đọc lại làm nguồn `Buffer.BlockCopy`, tạo race khiến hai phép tính dùng hai mảng khác nhau.

Đã sửa tối thiểu tại production adapter `PhotoBooth.Infrastructure/Services/LiveViewService.cs`: snapshot `ImageData` đúng một lần vào local reference, kiểm tra offset theo chính snapshot đó, rồi cấp phát và sao chép từ cùng reference. Không thay public contract, camera lifecycle, GPU renderer, DB hay UI workflow.

Self-check sau sửa:

- Camera harness net48/x86 build thành công bằng output kiểm tra tách biệt.
- `PhotoBooth.UnitTests`: 44/44 đạt.
- Race cần thiết bị thật để xác nhận nên Phase 5 dừng tại manual soak gate: chạy Osmo tối thiểu 5.000 GPU-uploaded frames hoặc 10–15 phút, đồng thời resize/minimize/restore một lần; counter phải tiếp tục tăng và đóng app phải giải phóng camera. Nếu tái hiện lỗi, giữ nguyên `camera-result.txt` để lấy stack trace.

#### Kết quả manual camera soak sau race fix — 2026-08-23

Người kiểm thử xác nhận manual soak gate đạt với `OsmoAction5Pro`: ảnh live tiếp tục hiển thị đúng qua LUT ở độ phân giải 1280×720 và counter đạt `published=5519`, `GPU uploaded=5518`, vượt ngưỡng 5.000 frame mà không tái hiện `ArgumentException`/fail-safe. Chênh một frame phù hợp với capacity-one latest-frame handoff. Người kiểm thử cũng xác nhận toàn bộ thao tác manual trong gate đạt và camera được giải phóng khi đóng.

Kết luận checkpoint: bản sửa snapshot `LiveViewData.ImageData` đã vượt hardware regression từng thất bại khoảng frame 2.174; camera → detached JPEG → reusable upload texture → trilinear 3D LUT → WPF presentation đạt trên máy/GPU hiện tại. Kết quả này chưa tự động chứng minh hardware matrix Windows 10/11 hoặc GPU thứ hai.

Gate hardware matrix ban đầu yêu cầu chạy cùng net48/x86 camera harness trên ít nhất một máy Windows/GPU khác. Ngày 2026-08-23, chủ dự án quyết định **bỏ qua gate máy thứ hai** và chấp nhận phần rủi ro tương thích phần cứng chưa được kiểm chứng. Việc bỏ qua này không được diễn giải thành bằng chứng hỗ trợ Windows/GPU khác và không bỏ các guard production còn lại.

Phase 5B production integration được phép tiếp tục với các điều kiện bắt buộc: feature flag mặc định tắt; đường JPEG/WPF hiện tại là fallback; lỗi khởi tạo/render/device/front-buffer phải tự chuyển fallback và không dừng camera/session; resource ownership tách khỏi camera callback; không thay target framework/platform; build/test phải đạt trước manual production UI gate.

#### Phase 5B — production integration checkpoint — 2026-08-23

Đã tạo project production `PhotoBooth.Color.D3D11` trong active solution, target `net48`, `x86`, `UseWPF=true`, dùng Vortice 2.4.2 và local MIT-attributed D3D11 → D3D9Ex → `D3DImage` bridge đã được chứng minh trong prototype. Production không reference source/binary từ `prototypes/`.

Điểm ghép giữ nguyên camera ownership:

```text
CaptureViewModel polling hiện có
→ LiveImage detached JPEG hiện có
├─ JPEG/WPF Image (luôn tồn tại làm fallback)
└─ feature flag ColorGpuLiveView
   → capacity-one latest byte[]
   → reusable BGRA CPU buffer + reusable dynamic Texture2D
   → active Texture3D LUT + LinearClamp trilinear
   → shared D3D11/D3D9Ex/D3DImage surface
```

- Feature `ColorGpuLiveView` mặc định `false`; manual opt-in dùng environment variable `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=1` trước khi khởi chạy Admin executable.
- `IColorLutService.GetLiveAsync` resolve theo `DefaultPresetId → PresetColorSettings → Ready ColorLutAsset → safe data-directory path`; chỉ trả LUT 2³–65³.
- LUT >65³, missing/corrupt/disabled/unattached và custom `DOMAIN_MIN/MAX` hiện fallback JPEG. Custom domain chưa được GPU shader production hỗ trợ ở checkpoint này; không áp sai màu một cách âm thầm.
- Strength được bake một lần vào texture LUT bằng nội suy với identity lattice; shader mỗi frame chỉ sample Texture2D + Texture3D.
- GPU control không tạo D3D device khi feature bị collapse. Binding LUT có thể đến sau `Loaded` mà không fail sớm.
- Lỗi create/render/resize/front-buffer/LUT validation dispose GPU surface, collapse GPU control, log qua `WaitingViewModel` và để JPEG `<Image>` phía dưới tiếp tục hiển thị. Camera polling/session không bị restart hoặc dispose bởi renderer.
- Input texture/SRV và CPU pixel buffer chỉ recreate khi kích thước frame đổi; LUT/shader/state tạo một lần cho selection hiện hành; active LUT được giữ đến khi surface unload.

Self-check:

- `PhotoBooth.Color.D3D11` build net48/x86: đạt.
- `PhotoBooth.sln` Debug build: đạt, 0 error; warning chỉ gồm dependency/feed/ruleset tồn tại đã biết.
- `PhotoBooth.UnitTests`: 44/44 đạt.
- Admin output có `PhotoBooth.Color.D3D11.dll`, Vortice D3D11/D3D9 runtime và `Assets/LiveColor.hlsl`.
- `git diff --check`: không có whitespace error.

**Manual production UI gate bắt buộc:** kiểm tra Customer waiting live view trong executable `PhotoBooth.Admin.UI`: (1) không đặt flag và xác nhận JPEG live view cũ hoạt động; (2) gán LUT 3D ≤65³ cho default preset, đặt `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=1`, khởi chạy lại và xác nhận LUT xuất hiện; (3) resize/minimize/restore, chuyển Admin ↔ Customer, chạy ≥5.000 frame rồi đóng; (4) xác nhận camera được giải phóng và `Logs/Error.log` không có GPU/live-view exception mới. Dừng trước gate này.

Build từ Visual Studio sau khi thêm project phát hiện `CS0118` tại `TemporaryPinDialog`: namespace gốc mới `PhotoBooth.Color` che tên ngắn `System.Windows.Media.Color` trong namespace `PhotoBooth.Admin.UI`. Đã đổi file này sang alias rõ ràng `MediaColor`; quét Admin/Customer không còn kiểu WPF `Color` chưa định danh. Solution build lại đạt 0 error và unit tests vẫn đạt 44/44. Đây là lỗi phân giải tên lúc compile, không phải lỗi truy cập DB/LUT runtime.

Để manual gate có dấu hiệu thị giác không thể nhầm với identity/fallback, đã thêm diagnostic LUT monochrome 2³. Chế độ chỉ hoạt động khi đồng thời đặt `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=1` và `PHOTOBOOTH_COLOR_GPU_MONOCHROME=1`; mặc định cả hai tắt và không sửa DB/LUT đã import. LUT dùng hệ số luminance Rec.709, upload thành Texture3D và đi qua đúng shader trilinear production. Unit tests đạt 44/44; Admin đang chạy khóa output Debug chuẩn nên build chuẩn không thể copy DLL, nhưng build tách biệt `bin/MonoValidate/net48` đạt 0 error. Phải đóng Admin cũ và rebuild output chuẩn trước manual test.

Manual evidence tiếp theo cho thấy diagnostic vẫn không xuất hiện trên màn “Ready when you are”. Điều tra xác nhận integration ban đầu đặt GPU surface trong `WaitingView`, trong khi màn live chính có nút Start là `CaptureView`; đây là nhầm điểm ghép UI. Đồng thời màn Presets trong ảnh không có preset/LUT, nên chưa có DB LUT để quan sát, dù diagnostic đáng lẽ không phụ thuộc DB.

Đã sửa bằng `LiveColorState` singleton dùng chung: feature/LUT chỉ resolve một lần cho trạng thái hiện hành và cả `WaitingView` lẫn `CaptureView` bind cùng dữ liệu. `CaptureView` giờ giữ JPEG Image làm nền fallback và đặt `LiveColorSurface` đúng trên live image chính; lỗi GPU từ cả hai view đều disable cùng state mà không chạm camera. Build tách biệt `bin/MonoCaptureValidate/net48` đạt 0 error, unit tests 44/44, diff check đạt. Manual monochrome gate phải chạy lại sau khi đóng binary cũ và rebuild output Debug chuẩn.

#### Kết quả manual production monochrome gate — 2026-08-23

Người kiểm thử xác nhận live view chính trong `CaptureView` đã chuyển rõ ràng sang trắng đen khi bật đồng thời `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=1` và `PHOTOBOOTH_COLOR_GPU_MONOCHROME=1`. Kết quả này chứng minh production executable đã dùng reusable GPU input texture, diagnostic Texture3D LUT và trilinear shader; không còn nhầm với JPEG fallback. FPS và Admin ↔ Customer handoff đã được xác nhận ổn định ở lượt kiểm thử trước.

Diagnostic gate không thay thế gate dữ liệu thật. Bước manual kế tiếp: tắt `PHOTOBOOTH_COLOR_GPU_MONOCHROME`, tạo/chọn default preset, import và attach một LUT `.cube` 3D ≤65³ có hiệu ứng dễ nhận biết, giữ `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=1`, rồi xác nhận cả Waiting/Capture dùng đúng LUT. Sau đó kiểm tra detach hoặc tắt flag trả về JPEG màu mà không reconnect camera.

#### Real-LUT/capture fail-safe checkpoint — 2026-08-23

Manual real-LUT gate phát hiện hai lỗi độc lập:

1. `.cube` 32³ import/attach ở trạng thái `Ready`, nhưng nút **Set Default** trước đây chỉ cập nhật `Preset.IsDefault`; production renderer lại resolve bằng `Settings.DefaultPresetId`. Hai bản ghi không được đồng bộ nên renderer hợp lệ nhưng không tìm thấy preset workflow và giữ JPEG fallback.
2. Lần chụp đầu làm process thoát với `System.AccessViolationException` trong `swscale-4.dll → sws_scale → Accord.Video.FFMPEG.VideoFileWriter.WriteVideoFrame → MotionPhotoService.EncodeVideo`. Camera đã tải ảnh staging thành công trước lỗi. Đây là lỗi native Motion Photo hậu kỳ, không phải lỗi `.cube`, D3D11 hay camera capture; exception loại này không thể được bảo vệ tin cậy bằng `try/catch` trong cùng process.

Đã sửa theo fail-safe:

- `PresetManagerViewModel.SetDefault` cập nhật cả cờ default của preset và `Settings.DefaultPresetId`. Live View chỉ đổi LUT sau khi người quản trị chủ động bấm **Set Default**, tránh việc attach một LUT bất kỳ tự đổi workflow đang chạy.
- Thông báo Attach không còn tuyên bố tetrahedral capture đã hoạt động; checkpoint này mới chứng minh trilinear Live View. Tetrahedral capture vẫn là phase riêng chưa được bật production.
- Native Accord/FFmpeg Motion Photo được feature-gate bằng `MotionPhotoNativeEncoder`, mặc định `false`. Khi tắt, service không giữ live frames và sao chép still JPEG sang destination hiện hành, nên capture/session DB vẫn commit đúng file mà không gọi `swscale`.
- Chỉ có thể opt-in encoder cũ bằng `PHOTOBOOTH_MOTION_PHOTO_NATIVE=1`; không bật trong production cho đến khi encoder được chuyển ra process cô lập hoặc thay thế bằng implementation an toàn. Không retry native encoder sau access violation.

Self-check sau sửa:

- `PhotoBooth.sln` Debug build chuẩn: đạt, 0 error.
- `PhotoBooth.UnitTests`: 44/44 đạt.
- Warning còn lại: legacy ruleset và hai test fake-event không dùng; không có warning mới từ thay đổi này.

**Manual stop gate:** giữ `PHOTOBOOTH_MOTION_PHOTO_NATIVE` unset, giữ `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=1`, tắt `PHOTOBOOTH_COLOR_GPU_MONOCHROME`, mở Presets, chọn preset đã attach LUT 32³ và bấm **Set Default**. Chuyển sang Customer để xác nhận màu LUT xuất hiện, sau đó chụp đúng một lần. Đạt khi app không thoát, ảnh JPEG được lưu/đăng ký trong session và Live View tiếp tục chạy. Dừng triển khai tại đây vì cả hiệu ứng LUT thật và native crash regression đều cần camera/manual evidence.

#### Frame preview/file ownership regression — 2026-08-23

Manual gate tiếp theo chụp đủ ba ảnh nhưng frame preview/print thất bại và debugger dừng khi cleanup lúc trở về Admin. Log xác nhận không phải frame data corruption:

- `SessionWorkspace.Promote`: `File.Move` thất bại vì source JPEG đang được process sử dụng.
- `FrameSelectionViewModel.Compose`: `File.Delete(preview-*.png)` thất bại vì preview đang được process sử dụng.
- Nguyên nhân là WPF `Image.Source` bind trực tiếp tới path; decoder giữ file handle trong khi workflow cần move/delete file trong workspace. Retry còn có thể chạy preview compose song song với final compose.

Đã sửa ownership/lifecycle:

- Thêm `PathImageConverter` dùng `BitmapCacheOption.OnLoad`, đọc bằng stream cho phép read/write/delete rồi `Freeze`; WPF chỉ giữ bitmap memory, không giữ handle của capture/preview.
- Áp converter cho review image, review thumbnails, frame thumbnails và composed preview.
- Serialize preview/final composition bằng `SemaphoreSlim` để Retry/state-change không thao tác cùng workspace đồng thời.
- `SessionWorkspace.Cleanup/Prepare` dùng best-effort delete cho `IOException`/`UnauthorizedAccessException`; cleanup file tạm không còn được phép làm vỡ chuyển Customer → Admin. Prepare vẫn tạo workspace và tên output đều unique.

LUT thật `YELLOW WASH` trong shared data directory được kiểm tra là `.cube` 3D 32³ hợp lệ, domain mặc định. Lần chạy lỗi không có log khởi tạo GPU color vì App trước đây ghi đè feature thành `false` khi biến môi trường không tồn tại. Sau các hardware/diagnostic gate đã đạt, `ColorGpuLiveView` nay bật mặc định; `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=0` là kill switch, GPU failure vẫn trả về JPEG/WPF fallback. Diagnostic monochrome vẫn mặc định tắt.

Self-check:

- Admin build tách biệt: đạt, 0 error.
- Unit tests: 44/44 đạt.
- Output Debug chuẩn chưa thể copy DLL vì process PID 9016 còn đang dừng trong Visual Studio; đây là file lock của binary đang debug, không phải compile error.

**Manual stop gate:** Stop Debug hoàn toàn, rebuild solution chuẩn rồi chạy lại. Xác nhận (1) default preset `YELLOW WASH` đổi màu Live View mà không cần đặt env; (2) chụp đủ số ảnh; (3) frame preview hiển thị và Retry/Print không báo file lock; (4) trở về Admin không break và camera/live view phục hồi. Nếu cần fail-safe tức thời, đặt `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=0` để tách LUT khỏi regression.

#### Tetrahedral capture + Admin Live View checkpoint — 2026-08-23

Manual evidence xác nhận capture/review/frame workflow đã chạy, nhưng LUT chỉ hiện ở Customer GPU Live View; các JPEG chụp và Admin Home vẫn là màu nguồn. Đây là hai integration còn thiếu, không phải import `.cube` thất bại.

Đã triển khai:

- `IColorLutService.ApplyCaptureAsync` resolve DB theo `Session.PresetId`, fallback `Settings.DefaultPresetId`, rồi đọc đúng asset `Ready` đã attach.
- `CapturePipeline` gọi processor sau camera transfer/auto-flip và trước Motion Photo copy + session DB registration. Vì vậy review thumbnails, frame composition, final capture và print đều dùng cùng JPEG đã áp LUT.
- CPU processor dùng tetrahedral interpolation đủ sáu tetrahedra, hỗ trợ `DOMAIN_MIN/MAX`, strength 0–1 và cube 2³–128³. Pixel loop dùng `LockBits` BGRA thay vì `GetPixel`; cancellation được kiểm tra theo từng scanline.
- Output được ghi vào file tạm cùng volume, encode JPEG quality 100, cố giữ EXIF/property items và DPI, sau đó `File.Replace` nguyên tử. Nếu xử lý thất bại, capture không commit DB và file nguồn không bị ghi đè một phần.
- Admin Home overlay cùng production `LiveColorSurface`, lấy cùng singleton `LiveColorState`/default preset như Customer. Mỗi lần Home trở lại visible sẽ refresh settings/LUT; GPU failure collapse về JPEG source.
- Thêm unit test identity cube tại cả sáu nhánh tetrahedral để kiểm tra ordering B-major/G/R-fastest của `.cube` và công thức nội suy.

Self-check:

- Admin build tách biệt: đạt, 0 error.
- Unit tests: 50/50 đạt (6 tetrahedral branch cases mới).

**Manual image-quality gate bắt buộc:** đóng binary cũ, rebuild output Debug chuẩn, dùng LUT `YELLOW WASH` default và chụp một lượt. Xác nhận cùng hiệu ứng xuất hiện ở Admin Live View, Customer Live View, ba review JPEG và frame composite. Mở trực tiếp JPEG đã lưu để kiểm tra không sọc/sai kênh/đen ảnh, orientation đúng và metadata cơ bản còn đọc được. Dừng tại gate này trước tối ưu cache CPU hoặc bật capture LUT rộng rãi.

#### Uniform aspect-ratio correction — 2026-08-23

Manual Admin/Customer LUT evidence cho thấy GPU surface fill toàn bộ host rectangle, làm frame 16:9 bị kéo cao khi vùng Admin gần vuông. JPEG fallback dùng `Uniform`, nhưng `DrawingSurface` cũ dùng `Stretch.Fill` và render target được cấp theo kích thước host nên tỷ lệ nguồn bị mất.

Đã sửa tại shared production control thay vì vá từng view:

- `LiveColorSurface` ghi nhận `PixelWidth/PixelHeight` thật ngay khi decode frame.
- Child `DrawingSurface` được center và tính kích thước lớn nhất nằm trọn host theo tỷ lệ nguồn (`Uniform`/letterbox), không crop và không kéo giãn.
- Khi host resize, minimize/restore hoặc frame đổi resolution, aspect được tính lại; render target chỉ resize theo surface uniform mới.
- Trước frame đầu tiên surface vẫn stretch để có kích thước khởi tạo D3D; sau decode đầu tiên nó chuyển sang tỷ lệ thật. JPEG fallback phía dưới vẫn dùng `Uniform`.
- `DrawingSurface.Stretch` đổi từ `Fill` sang `Uniform` như guard bổ sung.

Self-check: Admin build tách biệt đạt 0 error; unit tests 50/50 đạt.

**Manual aspect gate:** đóng/rebuild binary chuẩn, kiểm tra Admin Home, Customer Capture và Waiting. Vật thể tròn/vuông phải giữ đúng hình dạng; vùng dư được letterbox/căn giữa, không zoom crop. Resize cửa sổ và chuyển Admin ↔ Customer một lần để xác nhận tỷ lệ không đổi.

#### GPU/JPEG overlap correction — 2026-08-23

Manual screenshots sau aspect correction cho thấy phần giữa là GPU LUT 16:9 đúng tỷ lệ, nhưng JPEG fallback vẫn hiện ở hai bên. Nguyên nhân: `LiveColorSurface` outer `Border` trong suốt; child D3D surface đã letterbox đúng nhưng vùng dư xuyên xuống `<Image>` fallback, tạo hình ghép ba dải tưởng như frame bị biến dạng.

Đã sửa shared control thành compositing độc quyền khi GPU active: outer surface có nền đen opaque và `ClipToBounds=true`; D3D frame uniform nằm giữa, vùng dư là letterbox đen. JPEG vẫn tồn tại phía dưới nhưng chỉ nhìn thấy khi GPU control `Collapsed` do feature off/failure. Không xóa fallback và không decode/upload frame lần hai.

**Manual overlap gate:** rebuild binary chuẩn, xác nhận Admin/Customer chỉ có một ảnh LUT ở giữa và dải letterbox đồng nhất; tuyệt đối không còn hai mảng JPEG màu nguồn ở hai cạnh. Sau đó tắt GPU bằng `PHOTOBOOTH_COLOR_GPU_LIVEVIEW=0` để kiểm tra JPEG fallback đơn vẫn hiển thị bình thường.

#### Camera-reported aspect and capture-history integrity — 2026-08-23

Manual gate cho thấy loại bỏ overlap chưa đủ: webcam báo live-view 16:9 qua `LiveViewFrame.Width/Height`, trong khi kích thước bitmap mà WPF/D3D decoder quan sát không phản ánh đúng tỷ lệ camera. GPU vì vậy letterbox theo tỷ lệ gần 1:1. `LiveColorSurface` nay nhận kích thước frame được camera báo làm nguồn ưu tiên; kích thước decode chỉ là fallback. Admin, Customer Capture và Waiting đều chuyển metadata này tới shared surface.

Lỗi chụp trong ảnh debugger là lỗi toàn vẹn DB độc lập với visibility của live view. `SqliteSessionRepository.SaveAsync` trước đây xóa toàn bộ `CapturedImages` rồi chèn lại. Khi ảnh đã được `CapturePhotos` loại MotionPhoto tham chiếu, `ON DELETE SET NULL` kích hoạt validation và bị từ chối đúng với thông báo `Motion Photo requires a captured image ID`. Repository nay upsert các ảnh hiện có và coi ảnh đã capture là lịch sử bất biến; không xóa hàng đang được asset tham chiếu. Thêm regression test cho chính chuỗi thao tác save → MotionPhoto reference → save session rỗng.

Self-check: Admin build tách biệt đạt, 0 error; unit tests 51/51 đạt, gồm regression test toàn vẹn Motion Photo mới.

**Manual gate:** rebuild binary chuẩn; xác nhận Customer và Admin hiển thị 16:9 không bóp hình, thực hiện một lượt chụp, vào preview rồi quay lại Admin. Không được có SQLite exception, process break hoặc mất camera. Dừng tại gate này trước mọi thay đổi tiếp theo.

#### Webcam live-view dimension source correction — 2026-08-23

Ảnh chụp/tetrahedral đã đạt nhưng manual live-view gate chưa đạt. Kiểm tra nguồn cho thấy `WebCameraDevice` encode JPEG nhưng không gán `LiveViewData.ImageWidth/ImageHeight`; lớp production vì vậy nhận `0 × 0` và GPU không thể dùng tỷ lệ camera-reported vừa bổ sung. Adapter nay gán cả image và live-view dimensions từ chính `eventargs.Frame`, đặt offset 0, dispose bitmap/stream mỗi frame. `VideoResolution` cũng được chọn trước khi start thiết bị để tránh frame đầu chạy với capability mặc định khác cấu hình.

Self-check: Admin build tách biệt đạt, 0 error; unit tests 51/51 đạt.

**Manual live-view-only gate:** rebuild và kết nối lại webcam để device được khởi tạo mới. Xác nhận Admin header không còn `0 × 0`, Customer live view giữ cùng tỷ lệ 16:9 như ảnh chụp, LUT vẫn hoạt động và FPS ổn định. Không cần chụp lại toàn pipeline nếu các điều kiện này đạt.

#### D3D viewport letterbox correction — 2026-08-23

Manual evidence tiếp theo xác nhận camera metadata đã đúng `1280 × 720`, nhưng child `DrawingSurface` vẫn bị WPF measure thành render target vuông ở cả Admin và Customer. Việc đặt `Width/Height` cho interop Image không đáng tin cậy qua nhiều template/layout host.

Shared surface nay luôn phủ host với render target ổn định. Sau khi clear toàn target thành đen, renderer tính D3D viewport lớn nhất theo tỷ lệ camera, căn giữa rồi mới draw fullscreen triangle vào viewport đó. Letterbox vì vậy do GPU quyết định, không còn phụ thuộc WPF layout; không crop, không bóp và không để JPEG fallback lộ qua.

Self-check: Admin build tách biệt đạt, 0 error; unit tests 51/51 đạt.

**Manual viewport gate:** rebuild, kiểm tra Admin và Customer. Với header `1280 × 720`, vùng hình phải là 16:9; dải đen chỉ nằm ở hai cạnh hoặc trên/dưới tùy tỷ lệ host. Vật thể không được kéo dài. Dừng tại đây trước bước tiếp theo.

#### Single-stage letterbox correction — 2026-08-23

Manual viewport evidence cho thấy nội dung không còn bị crop/bóp nhưng bị co nhỏ vào giữa. Nguyên nhân là letterbox kép: D3D viewport đã fit 16:9, sau đó WPF `DrawingSurface.Stretch=Uniform` tiếp tục fit cả render target theo source/layout vuông cũ. Khi aspect thuộc trách nhiệm của GPU viewport, interop Image phải `Stretch=Fill` để phủ host; viewport bên trong vẫn giữ nội dung đúng tỷ lệ và vùng clear đen.

**Manual single-letterbox gate:** rebuild và kiểm tra Customer Capture. Ảnh 16:9 phải dùng chiều rộng/chiều cao lớn nhất có thể trong host, chỉ còn đúng một cấp dải đen, không co nhỏ ở cả bốn phía và không méo vật thể.

#### Customer-selected print copies — 2026-08-24

Trang Customer Frame Selection có thêm `PrintCopies`, mặc định và tối thiểu 1. Bộ chọn bị disable khi phiên khởi đầu với tùy chọn không sử dụng máy in. Guard `context.PrintingEnabled` vẫn nằm trước mọi truy vấn profile/gọi pipeline, nên trường hợp này chỉ hoàn tất workflow và tuyệt đối không tạo lệnh in.

Khi printing được bật, số bản được truyền rõ ràng qua `IPrintPipeline` tới `PrintJob.Copies` và `PrintJobRecord.Copies`; Business tiếp tục clamp tối thiểu 1 để bảo vệ toàn vẹn kể cả khi UI binding nhận dữ liệu không hợp lệ. Giá trị profile `DefaultCopies` không còn ghi đè lựa chọn theo lượt của Customer.

Self-check: Admin/Customer production build tách biệt đạt, 0 error; unit tests 51/51 đạt.

**Manual print-copies gate:** chạy Customer một lượt với lựa chọn ban đầu không dùng máy in: bộ chọn số bản phải disabled và hoàn tất không xuất hiện job/spooler. Chạy lượt thứ hai với máy in đã kết nối, chọn 2 bản: Windows print job hoặc output vật lý phải nhận đúng 2 copies và bản ghi `PrintJobs.Copies` phải bằng 2. Dừng tại gate này trước thay đổi tiếp theo.
