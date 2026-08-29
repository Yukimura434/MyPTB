# Local Share gallery

## Mục tiêu

Local Share cung cấp một trang thư viện chỉ trong mạng LAN. Một mã QR duy nhất mở
`/share/{token}`. Trang không hiển thị tên phiên, mã capture, đường dẫn máy hoặc
thông tin quản trị; nội dung nhìn thấy chỉ gồm tiêu đề **Ảnh của bạn**, thumbnail,
nút **Tải về** cho từng asset và nút **Tải tất cả** cố định ở đáy màn hình.

File JPG, PNG, GIF và MP4 gốc không bị đóng gói ZIP, không bị resize hay encode
lại. Thumbnail là file JPEG dẫn xuất riêng, chỉ dùng để hiển thị gallery.

## Kế hoạch và hợp đồng endpoint

1. `ILocalShareService.CreateAsync` nhận danh sách file đã qua kiểm tra integrity.
2. Service tạo token ngẫu nhiên, ánh xạ token tới danh sách asset cho phép và tạo
   thumbnail 480 px một lần trong `Data/LocalShare/<token>/`.
3. QR trỏ tới `GET /share/{token}` thay vì endpoint tải ZIP.
4. `GET /share/{token}` trả HTML/CSS/JavaScript responsive được nhúng trong app.
5. `GET /share/{token}/thumbnail/{assetId}` chỉ trả thumbnail JPEG; trang không
   nhúng URL file gốc vào thẻ `img` hoặc `video`.
6. `GET /share/{token}/download/{assetId}` stream nguyên byte file gốc với MIME và
   `Content-Disposition: attachment`.
7. JavaScript ghi asset đã yêu cầu tải vào `localStorage` theo token. **Tải tất
   cả** lọc các asset này rồi kích hoạt phần còn lại lần lượt, không tạo ZIP.
8. Nút **Tải tất cả** dùng `position: fixed` nên luôn hiện ở đáy khi cuộn.
9. Token hết hạn sau 30 phút. Request sai token/asset không được quyền đọc file.
10. Thumbnail và dữ liệu Local Share cũ được dọn khi service khởi động; file ảnh
    và video gốc vẫn do chính sách retention của capture quản lý.

## Thứ tự hiển thị

Asset được giữ theo thứ tự đầu vào từ capture. Nhãn chỉ mang tính trình bày và
được đánh số riêng theo loại: `Ảnh 1`, `Ảnh động 1`, `Video 1`. Không đưa tên file
gốc hoặc metadata phiên vào HTML.

## Tải tuần tự và giới hạn trình duyệt

Trình duyệt không cung cấp API đáng tin cậy để xác nhận người dùng đã lưu file
thành công xuống thiết bị. Vì vậy “đã tải” có nghĩa là trang đã kích hoạt endpoint
tải cho asset đó. Trạng thái được giữ trên cùng trình duyệt cho tới khi local
storage bị xóa. Nếu tải bị hủy hoặc lỗi mạng, khách vẫn có thể bấm lại nút tải đơn.

Nút **Tải tất cả** khởi tạo từng download theo hàng đợi với khoảng cách ngắn, thay
vì mở đồng thời. Một số trình duyệt có thể yêu cầu người dùng cho phép tải nhiều
file; trang hiển thị hướng dẫn nếu trình duyệt chặn.

## Tài nguyên và an toàn

- Không tạo `ShareArchive`, do đó không cần thêm dung lượng gần bằng toàn bộ phiên.
- Thumbnail được tạo trước, tái sử dụng và có kích thước giới hạn.
- Download stream trực tiếp từ ổ đĩa, không nạp toàn file vào RAM.
- Tên và đường dẫn file được giữ phía server; client chỉ biết asset ID ngẫu nhiên.
- HTML đặt `Cache-Control: no-store`, chống nhúng frame và không tải tài nguyên ngoài.
- Lỗi tạo thumbnail của một asset dùng thumbnail placeholder, không làm hỏng QR.

## Kiểm thử chấp nhận

- QR mở gallery, không tải file ngay.
- Source HTML chỉ tham chiếu endpoint thumbnail.
- Mỗi asset có đúng một nút tải và trả đúng byte file gốc.
- Nút tải tất cả luôn cố định ở đáy, tải theo thứ tự và bỏ qua ID đã lưu.
- Không có ZIP hoặc bản ghi `ShareArchive` mới.
- Token hết hạn và asset ID không thuộc token trả lỗi.
- Trang hoạt động trên trình duyệt Android/iOS trong cùng Wi-Fi.
