# Quy ước làm việc – PCTool

Tài liệu thiết kế đầy đủ (PRD, kiến trúc, dữ liệu, giao diện) nằm trong **Project "Tool Cài Win & Dọn Dẹp PC"** trên Claude, đó là nguồn duy nhất để tránh có nhiều bản lệch nhau. Tệp này chỉ ghi quy ước cho code.

## 1. Cấu trúc thư mục
| Thư mục | Vai trò |
|---|---|
| `PCTool.App` | Giao diện WPF (màn hình, nút bấm). **Không chứa logic nghiệp vụ.** |
| `PCTool.Core` | Toàn bộ logic: khung chẩn đoán/sửa lỗi, nhật ký, từng module. Không có giao diện. |
| `PCTool.Tests` | Kiểm thử tự động cho `PCTool.Core`. |
| `docs/` | Quy ước kỹ thuật, checklist bảo mật, báo cáo kiểm thử. |

Mỗi tính năng là một thư mục riêng trong `PCTool.Core` (ví dụ `Optimize`, `Hardware`, `Defender`, `Printer`, `AppInstaller`, `Reinstall`). Không viết cả phần mềm trong một file lớn.

## 2. Quy tắc đặt tên (C#)
- Lớp, phương thức, thuộc tính: `PascalCase`. Tham số, biến: `camelCase`.
- Trường private: `_camelCase`. Interface bắt đầu bằng `I`.
- Tên tiếng Anh cho code; **nội dung hiển thị cho người dùng bằng tiếng Việt**.
- Mỗi file một lớp chính, tên file trùng tên lớp.

## 3. Nhánh và commit
- `main` luôn **build được và test đạt**.
- Việc mới làm trên nhánh riêng, tên dạng `buoc-5-khung-du-an`, `m1-doc-ram`, `fix-loi-spooler`.
- Commit tại các mốc: khung ổn định, **trước thay đổi có rủi ro**, và **sau khi kiểm thử đạt**.
- Nội dung commit viết tiếng Việt, một dòng tóm tắt việc đã làm.
- Chỉ merge vào `main` khi GitHub Actions báo **xanh** (build + test đạt).

## 4. Kiểm tra trước khi commit
1. Build không lỗi, test đạt.
2. Thao tác nguy hiểm mới có chế độ **chạy thử** và hộp **xác nhận**.
3. Không có thông tin bí mật trong code.
4. Đã cập nhật README / tài liệu nếu thay đổi cách dùng.

## 5. Bí mật và dữ liệu
- **Không** để API key, mật khẩu, key bản quyền trong source code. `.gitignore` đã chặn `.env`, `*.pfx`, `secrets.json`.
- Hiện tại app **không dùng khóa bí mật nào**, nên chưa cần file `.env.example`. Nếu sau này có, phải tạo `.env.example` chỉ chứa tên biến, không chứa giá trị thật.
- Nhật ký không ghi nội dung file cá nhân.

## 6. Lint và định dạng
- `.editorconfig` quy định thụt lề, đặt tên. `dotnet format` chạy trong CI ở chế độ **chỉ báo, chưa chặn**; sẽ chuyển sang chặn khi code đã được chuẩn hóa.
- Trình phân tích mã (.NET analyzers) bật sẵn; cảnh báo mới nên được xử lý, không bỏ qua.

## 7. Phân biệt trạng thái công việc (khi báo cáo)
Đã viết code → Đã build được → Đã chạy thử trên Windows thật → Đã kiểm thử đạt → Đã phát hành → Người dùng đã nghiệm thu. Không gộp các mức này làm một.
