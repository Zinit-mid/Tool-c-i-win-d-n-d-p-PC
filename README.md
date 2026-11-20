# PCTool – Cài Win & Dọn dẹp PC

App Windows (C# / .NET 8 / WPF) dùng cá nhân để tối ưu, sửa lỗi, cài lại Windows và cài nhanh ứng dụng. Giao diện tiếng Việt, luôn chạy với quyền Administrator, hỏi xác nhận trước khi sửa.

> Trạng thái: **đang phát triển.** Có bản đầu của mục Dọn dẹp rác, Chẩn đoán máy in và tự cập nhật. Build và test tự động đã đạt; **chưa kiểm thử đầy đủ trên nhiều máy Windows thật**.

## Tính năng (theo giai đoạn)

| Giai đoạn | Module | Trạng thái |
|---|---|---|
| 1 | Tối ưu và dọn dẹp (dọn rác, máy chậm, mạng, Windows Update) | Có bản đầu mục Dọn rác (có chế độ chạy thử) |
| 1 | Thông số và sức khỏe phần cứng | Dự kiến |
| 1 | Bật/tắt Windows Defender (có hẹn giờ tự bật lại) | Dự kiến |
| 2 | Chẩn đoán và sửa máy in (spooler, driver, kết nối mạng/USB, hàng đợi, lỗi PrintService; sửa nhanh: khởi động lại Spooler, xóa hàng đợi) | Có bản đầu |
| 2 | Cài nhanh ứng dụng (Zalo, Chrome, Facebook, tự thêm) | Dự kiến |
| 3 | Cài lại Windows + khôi phục app | Dự kiến, làm sau cùng |
| 4 | PDF (đọc, xuất, chuyển PDF ↔ Word) | Để sau |
| Chung | Chế độ an toàn cho máy công ty (chỉ xem, khóa sửa) | Dự kiến |
| Sau | Dọn thư mục thông minh, gỡ phần mềm cứng đầu | Để sau |

## Cấu trúc

```
PCTool.sln
├─ PCTool.App/     giao diện WPF + tự cập nhật (Services/UpdateService.cs)
├─ PCTool.Core/    logic (Framework, Optimize, Printer, Windows, ...)
├─ PCTool.Tests/   kiểm thử tự động
└─ docs/           quy ước, checklist bảo mật, báo cáo kiểm thử
```

Quy ước làm việc: xem [docs/QUY-UOC.md](docs/QUY-UOC.md).

## Nguyên tắc an toàn

- Mọi thao tác nguy hiểm đều có **chế độ chạy thử** và **hộp xác nhận**.
- Tạo điểm khôi phục trước thay đổi lớn, có hoàn tác khi làm được.
- Toàn bộ dữ liệu xử lý trên máy, không gửi đi đâu (trừ việc kiểm tra bản cập nhật trên GitHub).
- Dữ liệu app và nhật ký lưu tại `%ProgramData%\PCTool\`.

## Tự cập nhật

Mỗi lần push lên `main` (và build, test đạt), GitHub Actions tạo một **Release** (`v0.1.<số lần build>`) kèm `PCTool.exe`.
App tự kiểm tra Release mới khi mở (có nút **Kiểm tra cập nhật** ở đầu cửa sổ). Có bản mới thì bấm **Cập nhật ngay**:
app tải bản mới (kiểm tra SHA256), thay file, tự mở lại. Chỉ hoạt động khi chạy file `PCTool.exe` (không phải `dotnet run`).
Mã tự cập nhật nằm trong `PCTool.App/Services/UpdateService.cs` vì nó thay thế chính file .exe đang chạy.

## Lấy file .exe

Cách dễ nhất: vào mục **Releases** của kho, tải `PCTool.exe` của bản mới nhất.
Cách khác: tab **Actions** → lần chạy mới nhất (dấu xanh) → **Artifacts** → `PCTool-win-x64`.
File chạy được ngay, không cần cài .NET. Lần đầu mở, Windows SmartScreen có thể cảnh báo vì app chưa có chữ ký số: chọn "More info" → "Run anyway".

## Build thủ công (trên Windows, cần .NET 8 SDK)

```
dotnet build PCTool.sln -c Release
dotnet test PCTool.sln -c Release
dotnet publish PCTool.App/PCTool.App.csproj -c Release -p:PublishSingleFile=true -o publish
```
