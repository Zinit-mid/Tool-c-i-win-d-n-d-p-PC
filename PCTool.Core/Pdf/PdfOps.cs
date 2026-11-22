using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PCTool.Core.Pdf;

public sealed record PdfResult(bool Ok, string Message, IReadOnlyList<string>? Outputs = null);

/// <summary>Chọn trang kiểu "1-3,5,8-" (đánh số từ 1). Để trống hoặc "tat ca" = mọi trang.</summary>
public static class PageSpec
{
    public static (IReadOnlyList<int>? Pages, string? Error) Parse(string? spec, int pageCount)
    {
        if (pageCount < 1) return (null, "File PDF không có trang nào.");
        var s = (spec ?? "").Trim();
        if (s.Length == 0 || s.Equals("all", StringComparison.OrdinalIgnoreCase) || s.Equals("tat ca", StringComparison.OrdinalIgnoreCase))
            return (Enumerable.Range(0, pageCount).ToList(), null);
        if (s.Length > 200) return (null, "Danh sách trang quá dài.");

        var result = new List<int>();
        foreach (var raw in s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int from, to;
            var parts = raw.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length == 1)
            {
                if (!int.TryParse(parts[0], out from)) return (null, $"\"{raw}\" không phải số trang.");
                to = from;
            }
            else if (parts.Length == 2)
            {
                if (!int.TryParse(parts[0], out from)) return (null, $"\"{raw}\" không hợp lệ.");
                if (parts[1].Length == 0) to = pageCount;
                else if (!int.TryParse(parts[1], out to)) return (null, $"\"{raw}\" không hợp lệ.");
            }
            else return (null, $"\"{raw}\" không hợp lệ.");

            if (from < 1 || to < from || to > pageCount) return (null, $"\"{raw}\" nằm ngoài 1-{pageCount}.");
            for (int i = from; i <= to; i++) result.Add(i - 1);
        }
        return result.Count == 0 ? (null, "Chưa chọn trang nào.") : (result, null);
    }
}

/// <summary>
/// Thao tác PDF an toàn: KHÔNG bao giờ sửa hay ghi đè file gốc; mọi kết quả là file MỚI (trùng tên thì thêm "(2)").
/// File có mật khẩu bị từ chối. Không chạy lệnh ngoài, không gửi dữ liệu đi đâu.
/// </summary>
public static class PdfOps
{
    public const long MaxFileBytes = 500L * 1024 * 1024;
    public const int MaxInputs = 200;
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp" };

    public static string? CheckInput(string? path, params string[] allowedExt)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Chưa chọn file.";
        string full;
        try { full = Path.GetFullPath(path); } catch { return "Đường dẫn không hợp lệ."; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "Chưa hỗ trợ đường dẫn mạng (UNC); hãy chép file về ổ máy.";
        if (!allowedExt.Contains(Path.GetExtension(full).ToLowerInvariant())) return $"Chỉ nhận file {string.Join(", ", allowedExt)}: {Path.GetFileName(full)}";
        var fi = new FileInfo(full);
        if (!fi.Exists) return "Không thấy file: " + Path.GetFileName(full);
        if (fi.Length > MaxFileBytes) return "File quá lớn (tối đa 500 MB): " + Path.GetFileName(full);
        return null;
    }

    public static string? CheckOutputDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "Chưa chọn thư mục lưu.";
        string full;
        try { full = Path.GetFullPath(dir); } catch { return "Thư mục lưu không hợp lệ."; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "Chưa hỗ trợ lưu vào đường dẫn mạng (UNC).";
        if (!Directory.Exists(full)) return "Thư mục lưu không tồn tại.";
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var bad in new[] { win, pf }.Where(b => !string.IsNullOrEmpty(b)))
            if (full.TrimEnd('\\').Equals(bad.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(bad.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return "Không lưu vào thư mục hệ thống của Windows/Program Files.";
        return null;
    }

    public static string UniquePath(string dir, string baseName, string ext)
    {
        var name = new string(baseName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (name.Length == 0) name = "PCTool";
        var p = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(p) && i < 10000; i++) p = Path.Combine(dir, $"{name} ({i}){ext}");
        return p;
    }

    private static (PdfSharp.Pdf.PdfDocument? Doc, string? Error) OpenImport(string path)
    {
        try { return (PdfReader.Open(path, PdfDocumentOpenMode.Import), null); }
        catch (Exception ex) when (ex is PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException or IOException or NotImplementedException or ArgumentException or UnauthorizedAccessException)
        {
            return (null, $"Không mở được \"{Path.GetFileName(path)}\" (có thể có mật khẩu hoặc file hỏng): {ex.Message}");
        }
    }

    public static int? PageCount(string path)
    {
        var (d, _) = OpenImport(path);
        if (d == null) return null;
        using (d) return d.PageCount;
    }

    private static PdfResult Save(PdfSharp.Pdf.PdfDocument doc, string outPath, int expectedPages)
    {
        try
        {
            doc.Save(outPath);
            var n = PageCount(outPath);
            if (n != expectedPages)
            {
                try { File.Delete(outPath); } catch { }
                return new(false, $"Kiểm tra lại thất bại: file kết quả có {n?.ToString() ?? "?"} trang, cần {expectedPages}. Đã bỏ file lỗi.");
            }
            return new(true, $"Xong: {Path.GetFileName(outPath)} ({expectedPages} trang).", new[] { outPath });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, "Không ghi được file kết quả: " + ex.Message);
        }
    }

    public static PdfResult Merge(IReadOnlyList<string> inputs, string outDir, string outName)
    {
        if (inputs.Count < 2) return new(false, "Cần ít nhất 2 file PDF để gộp.");
        if (inputs.Count > MaxInputs) return new(false, $"Tối đa {MaxInputs} file mỗi lần gộp.");
        foreach (var i in inputs) if (CheckInput(i, ".pdf") is { } e) return new(false, e);
        if (CheckOutputDir(outDir) is { } de) return new(false, de);

        using var outDoc = new PdfSharp.Pdf.PdfDocument();
        int total = 0;
        foreach (var f in inputs)
        {
            var (d, err) = OpenImport(Path.GetFullPath(f));
            if (d == null) return new(false, err!);
            using (d)
                for (int p = 0; p < d.PageCount; p++) { outDoc.AddPage(d.Pages[p]); total++; }
        }
        return Save(outDoc, UniquePath(outDir, outName, ".pdf"), total);
    }

    /// <summary>Lấy các trang chọn thành MỘT file PDF mới.</summary>
    public static PdfResult ExtractPages(string input, string spec, string outDir, string outName)
    {
        if (CheckInput(input, ".pdf") is { } e) return new(false, e);
        if (CheckOutputDir(outDir) is { } de) return new(false, de);
        var (d, err) = OpenImport(Path.GetFullPath(input));
        if (d == null) return new(false, err!);
        using (d)
        {
            var (pages, perr) = PageSpec.Parse(spec, d.PageCount);
            if (pages == null) return new(false, perr!);
            using var outDoc = new PdfSharp.Pdf.PdfDocument();
            foreach (var p in pages) outDoc.AddPage(d.Pages[p]);
            return Save(outDoc, UniquePath(outDir, outName, ".pdf"), pages.Count);
        }
    }

    /// <summary>Mỗi trang một file PDF.</summary>
    public static PdfResult SplitEach(string input, string outDir, string baseName)
    {
        if (CheckInput(input, ".pdf") is { } e) return new(false, e);
        if (CheckOutputDir(outDir) is { } de) return new(false, de);
        var (d, err) = OpenImport(Path.GetFullPath(input));
        if (d == null) return new(false, err!);
        using (d)
        {
            if (d.PageCount > 500) return new(false, "File có hơn 500 trang, hãy dùng \"lấy trang\" theo khoảng.");
            var outputs = new List<string>();
            for (int p = 0; p < d.PageCount; p++)
            {
                using var one = new PdfSharp.Pdf.PdfDocument();
                one.AddPage(d.Pages[p]);
                var r = Save(one, UniquePath(outDir, $"{baseName}-trang{p + 1}", ".pdf"), 1);
                if (!r.Ok) return new(false, $"Dừng ở trang {p + 1}: {r.Message}", outputs);
                outputs.Add(r.Outputs![0]);
            }
            return new(true, $"Xong: tách thành {outputs.Count} file.", outputs);
        }
    }

    public static PdfResult Rotate(string input, int degrees, string spec, string outDir, string outName)
    {
        if (degrees is not (90 or 180 or 270)) return new(false, "Chỉ xoay 90, 180 hoặc 270 độ.");
        if (CheckInput(input, ".pdf") is { } e) return new(false, e);
        if (CheckOutputDir(outDir) is { } de) return new(false, de);
        var (d, err) = OpenImport(Path.GetFullPath(input));
        if (d == null) return new(false, err!);
        using (d)
        {
            var (pages, perr) = PageSpec.Parse(spec, d.PageCount);
            if (pages == null) return new(false, perr!);
            var set = pages.ToHashSet();
            using var outDoc = new PdfSharp.Pdf.PdfDocument();
            for (int p = 0; p < d.PageCount; p++)
            {
                var pg = outDoc.AddPage(d.Pages[p]);
                if (set.Contains(p)) pg.Rotate = ((pg.Rotate % 360) + degrees) % 360;
            }
            return Save(outDoc, UniquePath(outDir, outName, ".pdf"), d.PageCount);
        }
    }

    /// <summary>Mỗi ảnh một trang A4 (dọc hoặc ngang theo ảnh), ảnh vừa khung, giữ tỉ lệ. Nhận JPG, PNG, BMP.</summary>
    public static PdfResult ImagesToPdf(IReadOnlyList<string> images, string outDir, string outName)
    {
        if (images.Count < 1) return new(false, "Chưa chọn ảnh.");
        if (images.Count > MaxInputs) return new(false, $"Tối đa {MaxInputs} ảnh mỗi lần.");
        foreach (var i in images) if (CheckInput(i, ImageExt) is { } e) return new(false, e);
        if (CheckOutputDir(outDir) is { } de) return new(false, de);

        try
        {
            using var doc = new PdfSharp.Pdf.PdfDocument();
            foreach (var f in images)
            {
                using var img = XImage.FromFile(Path.GetFullPath(f));
                double iw = img.PointWidth, ih = img.PointHeight;
                bool landscape = iw > ih;
                double pw = landscape ? 842 : 595, ph = landscape ? 595 : 842, margin = 20;
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(pw);
                page.Height = XUnit.FromPoint(ph);
                double scale = Math.Min((pw - 2 * margin) / iw, (ph - 2 * margin) / ih);
                double w = iw * scale, h = ih * scale;
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(img, (pw - w) / 2, (ph - h) / 2, w, h);
            }
            return Save(doc, UniquePath(outDir, outName, ".pdf"), images.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotImplementedException or ArgumentException or NotSupportedException)
        {
            return new(false, "Không đọc được ảnh (chỉ nhận JPG/PNG/BMP thường): " + ex.Message);
        }
    }
}
