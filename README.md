# PCTool – Cài Win & Dọn dẹp PC

App Windows (C# / .NET 8 / WPF) dùng cá nhân để tối ưu, sửa lỗi, cài lại Windows và cài nhanh ứng dụng. Giao diện tiếng Việt, luôn chạy với quyền Administrator, hỏi xác nhận trước khi sửa.

> Trạng thái: **đang phát triển, chưa phát hành.** Mới có khung dự án và bản đầu của mục Dọn dẹp rác. Chưa được chạy thử trên Windows thật.

## Tính năng (theo giai đoạn)

| Giai đoạn | Module | Trạng thái |
|---|---|---|
| 1 | Tối ưu và dọn dẹp (dọn rác, máy chậm, mạng, Windows Update) | Có bản đầu mục Dọn rác (có chế độ chạy thử) |
| 1 | Thông số và sức khỏe phần cứng | Dự kiến |
| 1 | Bật/tắt Windows Defender (có hẹn giờ tự bật lại) | Dự kiến |
| 2 | Chẩn đoán và sửa máy in | Dự kiến |
| 2 | Cài nhanh ứng dụng (Zalo, Chrome, Facebook, tự thêm) | Dự kiến |
| 3 | Cài lại Windows + khôi phục app | Dự kiến, làm sau cùng |
| 4 | PDF (đọc, xuất, chuyển PDF ↔ Word) | Để sau |
| Chung | Chế độ an toàn cho máy công ty (chỉ xem, khóa sửa) | Dự kiến |

## Cấu trúc

```
PCTool.sln
├─ PCTool.App/     giao diện WPF
├─ PCTool.Core/    logic (khung chẩn đoán, nhật ký, từng module)
├─ PCTool.Tests/   kiểm thử tự động
└─ docs/           quy ước, checklist bảo mật, báo cáo kiểm thử
```

Quy ước làm việc: xem [docs/QUY-UOC.md](docs/QUY-UOC.md).

## Nguyên tắc an toàn

- Mọi thao tác nguy hiểm đều có **chế độ chạy thử** (bật sẵn) và **hộp xác nhận**.
- Tạo điểm khôi phục trước thay đổi lớn, có hoàn tác khi làm được.
- Toàn bộ dữ liệu xử lý trên máy, không gửi đi đâu.
- Dữ liệu app và nhật ký lưu tại `%ProgramData%\PCTool\`.

## Lấy file .exe

Mỗi lần có thay đổi, GitHub Actions tự build và chạy test. Vào tab **Actions** → lần chạy mới nhất (dấu xanh) → mục **Artifacts** → tải `PCTool-win-x64` (file `PCTool.exe`, chạy được ngay, không cần cài .NET).

Lần đầu mở, Windows SmartScreen có thể cảnh báo vì app chưa có chữ ký số: chọn "More info" → "Run anyway".

## Build thủ công (trên Windows, cần .NET 8 SDK)

```
dotnet build PCTool.sln -c Release
dotnet test PCTool.sln -c Release
dotnet publish PCTool.App/PCTool.App.csproj -c Release -p:PublishSingleFile=true -o publish
```
