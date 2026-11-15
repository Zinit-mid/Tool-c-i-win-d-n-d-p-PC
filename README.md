# PCTool – Cài Win & Dọn dẹp PC

App Windows (C# / .NET 8 / WPF) dùng cá nhân. Luôn chạy với quyền Administrator.

## Tính năng

| Mục | Trạng thái |
|---|---|
| Dọn dẹp rác (temp, cache, thùng rác) | Có bản đầu (có chế độ chạy thử) |
| Dọn thư mục thông minh (trùng lặp, gom thư mục, sắp xếp) | Dự kiến |
| Sao lưu / Khôi phục app (winget + AppData) | Dự kiến |
| Gỡ phần mềm cứng đầu | Dự kiến |
| Chẩn đoán máy in | Dự kiến |
| Bật/tắt Windows Defender (có hẹn giờ) | Dự kiến |
| Cài lại Windows (chọn ổ giữ/ổ cài, USB tự động) | Dự kiến, làm sau cùng |

## Nguyên tắc an toàn

- Mọi thao tác nguy hiểm đều có chế độ **chạy thử** và màn hình xác nhận.
- Ưu tiên chuyển vào thùng rác thay vì xóa thẳng khi xử lý file của người dùng.
- Mọi thao tác được ghi vào nhật ký: `%LOCALAPPDATA%\PCTool\logs`.

## Lấy file .exe

Mỗi lần push lên `main`, GitHub Actions tự build. Vào tab **Actions** → lần chạy mới nhất →
**Artifacts** → tải `PCTool-win-x64` (file `PCTool.exe`, chạy được ngay, không cần cài .NET).

## Build thủ công (trên Windows)

```
dotnet build PCTool/PCTool.csproj -c Release
dotnet publish PCTool/PCTool.csproj -c Release -p:PublishSingleFile=true -o publish
```
