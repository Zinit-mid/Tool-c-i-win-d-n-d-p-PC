using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;

namespace PCTool.Core.Ocr;

/// <summary>Tesseract OCR (mã nguồn mở): bản dựng cho Windows của UB-Mannheim, cài bằng winget. Hỗ trợ tiếng Việt và tiếng Anh.</summary>
public static class TesseractInfo
{
    public const string WingetId = "UB-Mannheim.TesseractOCR";

    /// <summary>Kiểu đọc: tên hiển thị + mã ngôn ngữ truyền cho Tesseract. Chỉ nhận các mã này, không nhận chuỗi người dùng nhập.</summary>
    public static IReadOnlyList<(string Title, string Spec)> Modes { get; } = new[]
    {
        ("Tiếng Việt + Tiếng Anh", "vie+eng"),
        ("Chỉ tiếng Việt", "vie"),
        ("Chỉ tiếng Anh", "eng")
    };

    public static IReadOnlyList<string> LangCodes { get; } = new[] { "vie", "eng" };

    public static bool IsSpecAllowed(string? spec) => Modes.Any(m => m.Spec == spec);

    public static string DataUrl(string code) =>
        LangCodes.Contains(code) ? $"https://github.com/tesseract-ocr/tessdata_fast/raw/main/{code}.traineddata"
                                 : throw new ArgumentException("Ngôn ngữ không có trong danh sách.", nameof(code));

    public static AppEntry InstallEntry { get; } = new()
    {
        Id = "tesseract-ocr", Name = "Tesseract OCR", Kind = AppKind.Winget, WingetId = WingetId, Group = "Tiện ích"
    };

    public static string? FindExe(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Tesseract-OCR", "tesseract.exe");
            if (exists(p)) return p;
        }

        return null;
    }

    public static string TessdataDir(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "tessdata");

    public static string DataFile(string exe, string code) => Path.Combine(TessdataDir(exe), code + ".traineddata");

    public static bool HasData(string exe, string code, Func<string, bool>? exists = null) => (exists ?? File.Exists)(DataFile(exe, code));

    /// <summary>Tham số tách riêng từng cái (không qua shell). Đường dẫn ảnh bắt buộc là đường dẫn đầy đủ để không thể thành tùy chọn.</summary>
    public static IReadOnlyList<string> BuildArgs(string image, string spec, string tessdataDir)
    {
        if (!IsSpecAllowed(spec)) throw new ArgumentException("Kiểu ngôn ngữ không hợp lệ.", nameof(spec));
        if (!Path.IsPathRooted(image)) throw new ArgumentException("Cần đường dẫn đầy đủ của ảnh.", nameof(image));
        return new[] { image, "stdout", "-l", spec, "--tessdata-dir", tessdataDir, "--psm", "3" };
    }

    public static async Task<(bool Ok, string Text, string Error)> RecognizeAsync(
        IProcessRunner runner, string exe, string image, string spec, CancellationToken ct, int timeoutSeconds = 300)
    {
        var args = BuildArgs(image, spec, TessdataDir(exe));
        var r = await runner.RunAsync(exe, args, timeoutSeconds, ct);
        return r.ExitCode == 0 ? (true, r.Output, "") : (false, "", string.IsNullOrWhiteSpace(r.Error) ? $"Tesseract thoát với mã {r.ExitCode}." : r.Error.Trim());
    }
}

/// <summary>
/// Tải file dữ liệu ngôn ngữ (tiếng Việt/Anh) vào thư mục tessdata của Tesseract. Chỉ tải từ GitHub chính thức của dự án Tesseract
/// (tessdata_fast); không có mã băm để đối chiếu nên chỉ kiểm tra kích thước và tên miền sau chuyển hướng.
/// </summary>
public sealed class TesseractDataFix : IFix
{
    public const long MinBytes = 100_000;
    public const long MaxBytes = 60_000_000;

    private readonly string _exe;
    private readonly IReadOnlyList<string> _codes;
    private readonly Func<string, string, CancellationToken, Task> _download;

    public TesseractDataFix(string exe, IEnumerable<string> codes, Func<string, string, CancellationToken, Task>? download = null)
    {
        _exe = exe;
        _codes = codes.Distinct().ToList();
        if (_codes.Count == 0 || _codes.Any(c => !TesseractInfo.LangCodes.Contains(c)))
            throw new ArgumentException("Ngôn ngữ không có trong danh sách.", nameof(codes));
        _download = download ?? DefaultDownloadAsync;
    }

    public string Title => "Tải dữ liệu ngôn ngữ cho Tesseract";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Tải {string.Join(", ", _codes.Select(c => c + ".traineddata"))} từ github.com/tesseract-ocr/tessdata_fast (dự án Tesseract chính thức)",
        $"Chép vào {TesseractInfo.TessdataDir(_exe)}; file đã có thì giữ nguyên, không ghi đè",
        "Không có mã băm để đối chiếu nên chỉ kiểm tra kích thước; cần Internet"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public static bool HostAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host == "github.com" || uri.Host == "githubusercontent.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));

    private static async Task DefaultDownloadAsync(string url, string destFile, CancellationToken ct)
    {
        if (!HostAllowed(new Uri(url))) throw new InvalidOperationException("Địa chỉ tải không được phép.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var final = resp.RequestMessage?.RequestUri;
        if (final == null || !HostAllowed(final)) throw new InvalidOperationException("Địa chỉ sau chuyển hướng không được phép.");
        if (resp.Content.Headers.ContentLength is { } len && len > MaxBytes) throw new InvalidOperationException("File quá lớn.");

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destFile, FileMode.CreateNew, FileAccess.Write);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > MaxBytes) throw new InvalidOperationException("File quá lớn.");
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
    }

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            var dir = TesseractInfo.TessdataDir(_exe);
            Directory.CreateDirectory(dir);
            foreach (var code in _codes)
            {
                var target = TesseractInfo.DataFile(_exe, code);
                if (File.Exists(target)) continue;
                progress.Report($"Đang tải {code}.traineddata...");
                var part = target + ".part";
                if (File.Exists(part)) File.Delete(part);
                try
                {
                    await _download(TesseractInfo.DataUrl(code), part, ct);
                    var size = new FileInfo(part).Length;
                    if (size < MinBytes || size > MaxBytes) return FixResult.Failed($"File {code}.traineddata tải về có kích thước lạ ({size} byte), đã bỏ.");
                    File.Move(part, target);
                }
                finally { if (File.Exists(part)) File.Delete(part); }
            }

            return FixResult.Succeeded("Đã có dữ liệu ngôn ngữ: " + string.Join(", ", _codes));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return FixResult.Failed("Không tải được dữ liệu ngôn ngữ: " + ex.Message);
        }
    }

    public Task<bool> VerifyAsync(CancellationToken ct) =>
        Task.FromResult(_codes.All(c => File.Exists(TesseractInfo.DataFile(_exe, c)) && new FileInfo(TesseractInfo.DataFile(_exe, c)).Length >= MinBytes));
}
