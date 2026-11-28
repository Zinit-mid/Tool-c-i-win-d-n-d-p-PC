# Kết quả kiểm thử

Đối chiếu mã nguồn C# trên main của PCTool tại f5159ec. Bản Rust hiện có 27 màn hình. Build Release, Clippy và 9 kiểm thử Rust đạt.

| Nhóm | Kết quả |
|---|---|
| Giao diện Win32 | Mở trang chủ dạng thẻ, vào trang chức năng, quay về trang chủ, cuộn, duyệt 27 màn hình, kiểm tra nút thao tác, kiểm tra nhắc khi thiếu dữ liệu và đóng cửa sổ |
| Dung lượng ổ và xem trước dọn rác | Chạy xong trên Windows của môi trường kiểm thử |
| Dọn tệp tạm | Xóa đúng tệp đủ tuổi trong thư mục thử; kiểm tra nút Hủy trên giao diện |
| Sao lưu và khôi phục dữ liệu | Sao chép và khôi phục tệp mẫu qua giao diện; kiểm tra byte đầu ra; test từ chối đích đã tồn tại hoặc nằm trong nguồn |
| Phần cứng, khởi động, driver thiếu, bản quyền, phân vùng, Defender | Đã chạy qua giao diện; Windows của môi trường kiểm thử trả lỗi truy cập CIM |
| Quét lỗi máy, máy in và Windows Update | Windows của môi trường kiểm thử thiếu dịch vụ Spooler hoặc wuauserv; chưa kiểm tra thao tác sửa trên hàng đợi thật |
| Danh sách ứng dụng đã cài | Đọc trực tiếp Registry, hiển thị 50 ứng dụng desktop. 189 mục Registry không mở được trong môi trường kiểm thử. |
| Cài app, nhập/xuất danh sách WinGet, nhập/xuất driver | Kiểm tra màn hình và kiểm tra thiếu đầu vào; chưa cài app hoặc driver thật |
| Gộp/tách PDF, OCR ảnh, cắt video | Kiểm tra màn hình và kiểm tra thiếu đầu vào; môi trường thiếu qpdf, Tesseract và FFmpeg |
| Gỡ MSI và Office /safe | Kiểm tra nút giao diện và kiểm tra từ chối mã MSI sai. Chưa gỡ ứng dụng hoặc mở Office thật. |
| Wi-Fi và ghép MP4 | Kiểm tra nút, đầu vào và xử lý đường dẫn video. Môi trường không hỗ trợ lệnh WLAN của netsh và thiếu FFmpeg; chưa chạy xuất/nhập hồ sơ hoặc ghép video thật. |
| Zalo và xoay PDF | Đã kiểm tra sao chép tài khoản mẫu, giữ nguyên thư mục đích đã có và kiểm tra phạm vi PDF. Chưa kiểm tra Zalo đọc dữ liệu khôi phục thật hoặc chạy qpdf trong môi trường kiểm thử. |
| Xuất ảnh và OCR PDF | Đã xác nhận xuất PNG từ PDF mẫu qua giao diện. OCR đã kiểm tra giao diện và phân tích phạm vi trang; chưa chạy với ảnh thật vì thiếu Tesseract. |
| Mở PDF | Kiểm tra màn hình và kiểm tra thiếu đầu vào; chưa kiểm tra mở tài liệu với trình mặc định |

## Phạm vi chuyển đổi

Chức năng chưa chuyển gồm tạo ISO, tạo USB cài Win, cài lại Windows trực tiếp, gỡ phần mềm ngoài MSI và dọn phần sót, sửa quyền tệp, dọn thư mục thông minh, các chế độ sau cài Win, ghép video có chuyển mã, chuyển PDF–Word, tự cập nhật và nhiều thao tác nâng cao khác. Các mục đã có như báo cáo, phân vùng, cập nhật Windows và Defender mới cung cấp phần đọc trạng thái. Chưa đo tốc độ so với bản C#.






Đã kiểm tra tự tải dung lượng ổ khi mở thẻ và vị trí thanh thao tác phía trên kết quả trên hai dạng trang. Kiểm tra xuất PNG từ PDF mẫu vẫn đạt.

Đã kiểm tra 25 mục, chọn nhiều ứng dụng và hủy hộp xác nhận. Build Release, Clippy và 10 bài kiểm thử Rust đều đạt. Chưa cài hàng loạt từng gói trên máy thật. Danh mục này gồm các mục có sẵn trong mã nguồn; trình quản lý danh mục riêng, hồ sơ cài và nhập cấu hình danh mục của bản C# chưa chuyển.
