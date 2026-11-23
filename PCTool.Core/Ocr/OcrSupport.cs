using System.Text;
using PCTool.Core.Framework;
using PCTool.Core.Pdf;
using PCTool.Core.Windows;

namespace PCTool.Core.Ocr;

/// <summary>Ngôn ngữ đọc chữ (OCR) của Windows. Tên gói (Capability) cố định, không nhận từ người dùng.</summary>
public sealed record OcrLanguage(string Tag, string Title, string Capability);

/// <summary>Quy tắc chung cho chức năng đọc chữ từ ảnh/PDF (phần chạy OCR thật nằm ở PCTool.App vì dùng Windows OCR).</summary>
public static class OcrInfo
{
    public static IReadOnlyList<OcrLanguage> Languages { get; } = new[]
    {
        new OcrLanguage("vi", "Tiếng Việt", "Language.OCR~~~vi-VN~0.0.1.0"),
        new OcrLanguage("en", "Tiếng Anh", "Language.OCR~~~en-US~0.0.1.0")
    };

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
    public static string[] InputExtensions { get; } = ImageExtensions.Append(".pdf").ToArray();

    /// <summary>File PDF nhiều hơn số trang này thì từ chối (OCR chậm); bạn có thể tách PDF trước.</summary>
    public const int MaxPdfPages = 100;

    public static OcrLanguage? Find(string? tag) => Languages.FirstOrDefault(l => l.Tag == tag);

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static string? CheckInput(string? path) => PdfOps.CheckInput(path, InputExtensions);

    /// <summary>File chữ nằm cạnh file gốc, không ghi đè file nào: "ten-chu.txt", "ten-chu (2).txt"...</summary>
    public static string OutputPath(string input)
    {
        var full = Path.GetFullPath(input);
        return PdfOps.UniquePath(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + "-chu", ".txt");
    }

    /// <summary>Ghép chữ của từng trang. Một trang thì giữ nguyên; nhiều trang thì có dòng phân cách.</summary>
    public static string JoinPages(IReadOnlyList<string> pages)
    {
        if (pages.Count == 0) return "";
        if (pages.Count == 1) return pages[0].Trim();
        var sb = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) sb.Append("\r\n\r\n");
            sb.Append($"--- Trang {i + 1} ---\r\n").Append(pages[i].Trim());
        }

        return sb.ToString();
    }
}

/// <summary>
/// Cài gói ngôn ngữ đọc chữ của Windows (cần Internet/Windows Update, có thể mất vài phút). Chỉ nhận ngôn ngữ trong danh sách cố định.
/// </summary>
public sealed class OcrLanguageInstallFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly OcrLanguage _lang;

    public OcrLanguageInstallFix(string tag, ScriptRunner? run = null)
    {
        _lang = OcrInfo.Find(tag) ?? throw new ArgumentException("Ngôn ngữ không có trong danh sách.", nameof(tag));
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 1800));
    }

    public string Title => "Cài gói đọc chữ " + _lang.Title;
    public IReadOnlyList<string> Actions => new[]
    {
        $"Cài gói ngôn ngữ đọc chữ (OCR) của Windows cho {_lang.Title}",
        "Cần Internet; Windows tự tải từ Microsoft, có thể mất vài phút",
        "Chỉ thêm một tính năng ngôn ngữ của Windows, không đổi dữ liệu cá nhân. Gỡ bằng Cài đặt > Ngôn ngữ nếu không cần"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public string InstallScript() => $$"""
$ErrorActionPreference = 'Stop'
$r = Add-WindowsCapability -Online -Name '{{_lang.Capability}}'
$s = (Get-WindowsCapability -Online -Name '{{_lang.Capability}}').State
"STATE=$s;RESTART=$($r.RestartNeeded)"
""";

    public string CheckScript() => $$"""
[string](Get-WindowsCapability -Online -Name '{{_lang.Capability}}' -ErrorAction Stop).State
""";

    /// <summary>Đọc dòng "STATE=...;RESTART=..." do InstallScript in ra.</summary>
    public static (string State, bool Restart) ParseInstallOutput(string output)
    {
        string state = "", restart = "";
        foreach (var part in (output ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.StartsWith("STATE=", StringComparison.Ordinal)) state = t[6..].Trim();
            else if (t.StartsWith("RESTART=", StringComparison.Ordinal)) restart = t[8..].Trim();
        }

        return (state, string.Equals(restart, "True", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang cài gói đọc chữ, có thể mất vài phút...");
        var r = await _run(InstallScript(), ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error)
                ? "Windows không cài được gói ngôn ngữ (kiểm tra Internet/Windows Update)."
                : r.Error.Trim());

        var (state, restart) = ParseInstallOutput(r.Output);
        if (state == "Installed" && !restart) return FixResult.Succeeded($"Đã cài gói đọc chữ {_lang.Title}.");
        if (state is "Installed" or "InstallPending" || restart)
            return FixResult.Succeeded($"Đã cài gói đọc chữ {_lang.Title} nhưng Windows cần khởi động lại máy rồi mới dùng được.");
        return FixResult.Failed($"Windows báo trạng thái gói là \"{(state.Length == 0 ? "không rõ" : state)}\" (chưa cài). Kiểm tra Internet, Windows Update đang bật, rồi thử lại.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var r = await _run(CheckScript(), ct);
        return r.ExitCode == 0 && r.Output.Trim() is "Installed" or "InstallPending";
    }
}
