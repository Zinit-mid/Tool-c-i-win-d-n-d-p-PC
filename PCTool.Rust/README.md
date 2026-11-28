# PCTool Rust

Giải nén rồi mở PCTool-Rust.exe trên Windows 10/11 x64. Chọn thẻ công cụ trên trang chủ. Bấm Trang chủ để quay lại. Với chức năng yêu cầu đường dẫn, nhập từng đường dẫn một dòng vào ô trống. Nút thao tác có hộp xác nhận. Công việc nặng chạy trên luồng nền.

- Các thao tác driver và Print Spooler có thể cần quyền quản trị.
- Cài và sao lưu danh sách ứng dụng cần WinGet; gộp/tách PDF cần qpdf.exe; OCR cần tesseract.exe và dữ liệu vie, eng; cắt video cần ffmpeg.exe. Với qpdf, Tesseract và FFmpeg, chương trình phải có trong PATH. Mở PDF dùng trình mặc định của Windows.
- Sao lưu/khôi phục dữ liệu sao chép thư mục vào đích mới và bỏ qua các liên kết chuyển hướng. Lỗi trong lúc chép có thể để lại bản sao một phần ở đích.
- Biên dịch trong thư mục source bằng cargo build --release. Chạy kiểm thử Rust bằng cargo test. Chạy smoke_test.py với Python trên Windows trong phiên desktop tương tác để kiểm tra GUI, có dùng tệp tạm tự tạo.

Xem KIEM-THU.md để biết kết quả và giới hạn.

## Wi-Fi và ghép video

- Xuất hồ sơ Wi-Fi vào thư mục trống. Tệp XML chứa cấu hình mạng; Windows có thể yêu cầu nhập lại mật khẩu khi chuyển máy. Nhập từng hồ sơ XML cho tài khoản hiện tại.
- Ghép từ hai video MP4 có cùng codec và cấu hình luồng. Nhập mỗi video nguồn một dòng rồi nhập đường dẫn tệp MP4 đích mới ở dòng cuối. Cần ffmpeg.exe trong PATH.

## Zalo và xoay PDF

- Thoát hẳn Zalo trước khi sao lưu hoặc khôi phục. Khi sao lưu, chọn một thư mục trống riêng. Khi khôi phục, chọn thư mục đã lưu các tài khoản. Công cụ chỉ chép thư mục tài khoản dạng số và bỏ qua cả thư mục nếu tài khoản đó đã có ở đích. Zalo có thể không nhận dữ liệu tùy phiên bản hoặc tài khoản. Dùng Xuất/Nhập dữ liệu của Zalo nếu cần nhập vào tài khoản đã tồn tại.
- Xoay PDF bằng qpdf.exe trong PATH. Nhập mỗi mục một dòng theo thứ tự: đường dẫn PDF nguồn, đường dẫn PDF đích mới, góc 90/180/270, phạm vi nếu cần (ví dụ 1,3-5).

## Xuất ảnh và OCR PDF

Nhập ba dòng: PDF nguồn, đích mới, và số trang hoặc khoảng trang. Khi xuất ảnh, đích là thư mục mới. Khi OCR, đích là tệp TXT mới. Xuất ảnh cần pdftoppm.exe, OCR cần thêm tesseract.exe cùng dữ liệu vie và eng.


Các trang chẩn đoán tự tải khi mở từ Trang chủ. Các nút thao tác nằm phía trên khung kết quả. Trang Phân vùng ổ cứng hiện xem danh sách và tải lại. Nếu Windows từ chối truy vấn, khung kết quả hiển thị lỗi.

## Cài nhanh ứng dụng

Trang Cài nhanh ứng dụng hiển thị 25 mục mặc định và gợi ý lấy từ danh mục của bản C#. Bấm từng ứng dụng để chọn hoặc bỏ chọn. Có thể nhập thêm ID WinGet, mỗi ID một dòng. Facebook tạo lối tắt trên Desktop. PCTool hỏi xác nhận trước khi chạy.

## Cập nhật PCTool Rust

Nút Kiểm tra cập nhật trên thanh đầu so sánh phiên bản hiện tại với download/version.txt trên nhánh pctool-rust. Khi có bản mới hơn, nút cho phép mở trang tải ZIP trong trình duyệt. Giải nén bản mới để chạy.
