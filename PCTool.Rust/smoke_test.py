import ctypes as c
import pathlib
import shutil
import subprocess
import time
import tempfile
from ctypes import wintypes as w

u = c.WinDLL("user32", use_last_error=True)
u.FindWindowW.argtypes = [w.LPCWSTR, w.LPCWSTR]
u.FindWindowW.restype = w.HWND
u.FindWindowExW.argtypes = [w.HWND, w.HWND, w.LPCWSTR, w.LPCWSTR]
u.FindWindowExW.restype = w.HWND
u.GetWindowRect.argtypes = [w.HWND, c.POINTER(w.RECT)]
u.GetWindowRect.restype = w.BOOL
u.GetDlgItem.argtypes = [w.HWND, c.c_int]
u.GetDlgItem.restype = w.HWND
u.SendMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
u.SendMessageW.restype = w.LPARAM
u.IsWindowEnabled.argtypes = [w.HWND]
u.PostMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]

exe = pathlib.Path(__file__).with_name("PCTool-Rust.exe")
startup = subprocess.STARTUPINFO()
startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
startup.wShowWindow = 0
p = subprocess.Popen([str(exe)], startupinfo=startup)

def wait(check, seconds=20):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        value = check()
        if value:
            return value
        if p.poll() is not None:
            raise RuntimeError(f"Process exited {p.returncode}")
        time.sleep(0.05)
    raise TimeoutError

def output(edit):
    buffer = c.create_unicode_buffer(32768)
    u.SendMessageW(edit, 0xd, len(buffer), c.addressof(buffer))
    return buffer.value

try:
    hwnd = wait(lambda: u.FindWindowW("PCToolRustMain", "PCTool Rust – Bảo trì Windows"))
    pages = u.FindWindowExW(hwnd, None, "LISTBOX", None)
    edit = u.FindWindowExW(hwnd, None, "EDIT", None)
    button = u.GetDlgItem(hwnd, 101)
    edit = u.FindWindowExW(hwnd, edit, "EDIT", None)
    assert pages and edit and button
    time.sleep(0.5); print("List count", u.SendMessageW(pages, 0x18b, 0, 0), c.get_last_error(), flush=True); assert u.SendMessageW(pages, 0x18b, 0, 0) == 27
    assert all(u.GetDlgItem(hwnd, 200 + index) for index in range(27))
    card = u.GetDlgItem(hwnd, 202)
    u.SendMessageW(card, 0xf5, 0, 0)
    assert "←" in output(u.GetDlgItem(hwnd, 105))
    wait(lambda: "Tổng (GiB)" in output(edit))
    def bounds(control):
        box = w.RECT()
        assert u.GetWindowRect(control, c.byref(box))
        return box
    assert bounds(button).bottom < bounds(edit).top
    u.SendMessageW(u.GetDlgItem(hwnd, 105), 0xf5, 0, 0)
    u.SendMessageW(hwnd, 0x115, 7, 0)
    u.SendMessageW(hwnd, 0x115, 6, 0)
    for index in range(12):
        u.SendMessageW(pages, 0x186, index, 0)
        u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
        u.SendMessageW(button, 0xf5, 0, 0)
        wait(lambda: u.IsWindowEnabled(button) and "Đang xử lý" not in output(edit), 45)
        result = output(edit)
        assert result and result != "Chọn một mục bên trái để xem thông tin."
        if index == 4:
            assert "ứng dụng desktop" in result and "Tìm thấy 0 ứng dụng" not in result, result[:300]
            print(result.splitlines()[0], flush=True)
        if index == 2:
            assert "Tổng (GiB)" in result, result[:300]
        lines = result.strip().splitlines()
        failure = next((line for line in lines if "Không hoàn thành" in line or "Không xử lý được thư mục" in line), None)
        if failure:
            print(f"Page {index}: blocked — {' '.join(lines[-2:])}", flush=True)
        else:
            print(f"Page {index}: completed", flush=True)
    u.SendMessageW(pages, 0x186, 11, 0)
    u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
    first_action = u.GetDlgItem(hwnd, 102)
    assert "Xóa tệp tạm cũ" in output(first_action), output(first_action)
    u.PostMessageW(hwnd, 0x111, 102, first_action)
    confirmation = wait(lambda: u.FindWindowW("#32770", "Xóa tệp tạm cũ"))
    u.PostMessageW(confirmation, 0x111, 7, 0)
    wait(lambda: not u.FindWindowW("#32770", "Xóa tệp tạm cũ"))
    u.SendMessageW(pages, 0x186, 5, 0)
    u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
    assert "Khởi động lại Spooler" in output(first_action), output(first_action)
    assert "Xóa hàng đợi in" in output(u.GetDlgItem(hwnd, 103))
    for index, expected in enumerate([
        "Cài bằng WinGet", "Xuất danh sách ứng dụng", "Xuất driver",
        "Gộp PDF", "Mở PDF", "Nhận dạng chữ", "Cắt video", "Sao lưu thư mục", "Mở trình gỡ MSI",
    ], start=12):
        u.SendMessageW(pages, 0x186, index, 0)
        u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
        assert expected in output(first_action), output(first_action)
        assert not u.IsWindowEnabled(button)
        u.PostMessageW(hwnd, 0x111, 102, first_action)
        missing_input = wait(lambda: u.FindWindowW("#32770", "Nhập dữ liệu"))
        u.PostMessageW(missing_input, 0x10, 0, 0)
        wait(lambda: not u.FindWindowW("#32770", "Nhập dữ liệu"))
    u.SendMessageW(pages, 0x186, 21, 0)
    u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
    assert "Mở Word /safe" in output(first_action)
    assert "Mở Excel /safe" in output(u.GetDlgItem(hwnd, 103))
    for index, expected in [(22, "Xuất hồ sơ Wi-Fi"), (23, "Ghép MP4"), (24, "Sao lưu Zalo"), (25, "Xoay trang")]:
        u.SendMessageW(pages, 0x186, index, 0)
        u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
        assert expected in output(first_action), output(first_action)
    u.SendMessageW(pages, 0x186, 22, 0)
    u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
    assert "Nhập hồ sơ Wi-Fi" in output(u.GetDlgItem(hwnd, 103))
    u.SendMessageW(pages, 0x186, 19, 0)
    u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
    with tempfile.TemporaryDirectory(prefix="pctool-ui-") as folder:
        root = pathlib.Path(folder)
        source = root / "nguồn"
        source.mkdir()
        data = source / "dữ liệu.txt"
        data.write_bytes(b"sample")
        backup = root / "sao lưu"
        restored = root / "khôi phục"
        field = u.FindWindowExW(hwnd, None, "EDIT", None)
        second_action = u.GetDlgItem(hwnd, 103)
        for before, after, action, name in [
            (source, backup, first_action, "Sao lưu thư mục"),
            (backup, restored, second_action, "Khôi phục thư mục"),
        ]:
            value = c.create_unicode_buffer(f"{before}\r\n{after}")
            assert u.SendMessageW(field, 0x000c, 0, c.addressof(value))
            u.PostMessageW(hwnd, 0x111, 102 if action == first_action else 103, action)
            prompt = wait(lambda: u.FindWindowW("#32770", name))
            u.PostMessageW(prompt, 0x111, 6, 0)
            wait(lambda: after.joinpath(data.name).is_file() and "Đã sao chép" in output(edit))
            assert after.joinpath(data.name).read_bytes() == b"sample"
    u.SendMessageW(pages, 0x186, 26, 0)
    u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
    assert "Xuất ảnh PNG" in output(first_action)
    assert "OCR PDF ra văn bản" in output(u.GetDlgItem(hwnd, 103))
    assert bounds(u.GetDlgItem(hwnd, 104)).bottom < bounds(first_action).top
    assert bounds(first_action).bottom < bounds(edit).top
    if shutil.which("pdftoppm.exe"):
        with tempfile.TemporaryDirectory(prefix="pctool-pdf-ui-") as folder:
            root = pathlib.Path(folder)
            pdf = root / "source.pdf"
            objects = [
                b"<< /Type /Catalog /Pages 2 0 R >>",
                b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 144] /Contents 4 0 R >>",
                b"<< /Length 11 >>\nstream\n0 0 m 1 1 l\nendstream",
            ]
            data = bytearray(b"%PDF-1.4\n")
            offsets = []
            for index, obj in enumerate(objects, 1):
                offsets.append(len(data))
                data.extend(f"{index} 0 obj\n".encode() + obj + b"\nendobj\n")
            xref = len(data)
            data.extend(b"xref\n0 5\n0000000000 65535 f \n")
            for offset in offsets:
                data.extend(f"{offset:010} 00000 n \n".encode())
            data.extend(f"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode())
            pdf.write_bytes(data)
            target = root / "output"
            field = u.FindWindowExW(hwnd, None, "EDIT", None)
            value = c.create_unicode_buffer(f"{pdf}\r\n{target}\r\n1")
            assert u.SendMessageW(field, 0x000c, 0, c.addressof(value))
            u.PostMessageW(hwnd, 0x111, 102, first_action)
            prompt = wait(lambda: u.FindWindowW("#32770", "Xuất ảnh PNG"))
            u.PostMessageW(prompt, 0x111, 6, 0)
            wait(lambda: "Đã xuất" in output(edit) or "Không hoàn thành xuất ảnh" in output(edit))
            assert "Đã xuất 1 ảnh PNG" in output(edit), output(edit)
            files = list(target.glob("trang-*.png"))
            assert len(files) == 1 and files[0].read_bytes().startswith(b"\x89PNG\r\n\x1a\n")
            print("PDF PNG export: passed", flush=True)
    u.SendMessageW(pages, 0x186, 12, 0)
    u.SendMessageW(hwnd, 0x111, (1 << 16) | 100, pages)
    catalog = u.GetDlgItem(hwnd, 107)
    assert u.SendMessageW(catalog, 0x18b, 0, 0) == 25
    assert u.SendMessageW(catalog, 0x185, 1, 0) == 0
    assert u.SendMessageW(catalog, 0x185, 1, 2) == 0
    u.PostMessageW(hwnd, 0x111, 102, first_action)
    prompt = wait(lambda: u.FindWindowW("#32770", "Cài bằng WinGet"))
    caption = output(u.FindWindowExW(prompt, None, "STATIC", None))
    if "Zalo" not in caption:
        first_static = u.FindWindowExW(prompt, None, "STATIC", None)
        caption = output(u.FindWindowExW(prompt, first_static, "STATIC", None))
    assert "Zalo" in caption and "Facebook" in caption, caption
    u.PostMessageW(prompt, 0x111, 7, 0)
    wait(lambda: not u.FindWindowW("#32770", "Cài bằng WinGet"))
    print("App catalog: 25 entries, multi-selection and canceled confirmation passed", flush=True)
    u.PostMessageW(hwnd, 0x10, 0, 0)
    assert p.wait(timeout=5) == 0
    print("PASS: native window, 27 pages, disk diagnostic and CIM error handling, action buttons, canceled deletion, backup and restore through GUI, clean exit")
finally:
    if p.poll() is None:
        p.terminate()







