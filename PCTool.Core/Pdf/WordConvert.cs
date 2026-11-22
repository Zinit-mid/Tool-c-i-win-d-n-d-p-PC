using System.Text;
using PCTool.Core.Windows;

namespace PCTool.Core.Pdf;

/// <summary>
/// Chuyển Word ↔ PDF bằng chính Microsoft Word trên máy (cần cài Word). Luôn tạo file MỚI, không sửa file gốc.
/// Chuyển PDF sang Word do Word tự dựng lại bố cục nên có thể lệch với PDF phức tạp hoặc PDF ảnh scan.
/// </summary>
public static class WordConvert
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    public static string BuildScript(string input, string output, bool toPdf) => $$"""
$ErrorActionPreference = 'Stop'
$in = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64(input)}}'))
$out = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64(output)}}'))
$w = New-Object -ComObject Word.Application
$w.Visible = $false
$w.DisplayAlerts = 0
try {
  $d = $w.Documents.Open($in, $false, $true)
  $d.SaveAs([ref]$out, [ref]{{(toPdf ? 17 : 16)}})
  $d.Close([ref]0)
} finally { $w.Quit() }
""";

    private static async Task<PdfResult> RunAsync(string input, string outDir, string[] allowedExt, string outExt, bool toPdf, ScriptRunner? run, CancellationToken ct)
    {
        if (PdfOps.CheckInput(input, allowedExt) is { } e) return new(false, e);
        if (PdfOps.CheckOutputDir(outDir) is { } de) return new(false, de);
        var full = Path.GetFullPath(input);
        var outPath = PdfOps.UniquePath(outDir, Path.GetFileNameWithoutExtension(full), outExt);
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c, 300);
        PowerShellResult r;
        try { r = await run(BuildScript(full, outPath, toPdf), ct); }
        catch (TimeoutException) { return new(false, "Word chạy quá lâu nên đã dừng."); }
        if (r.ExitCode != 0)
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            var msg = string.IsNullOrWhiteSpace(r.Error) ? "Không chuyển được." : r.Error.Trim();
            if (msg.Contains("Word.Application", StringComparison.OrdinalIgnoreCase) || msg.Contains("class not registered", StringComparison.OrdinalIgnoreCase))
                msg = "Máy chưa cài Microsoft Word nên không chuyển được.";
            return new(false, msg);
        }
        var fi = new FileInfo(outPath);
        if (!fi.Exists || fi.Length == 0) return new(false, "Word báo xong nhưng không thấy file kết quả. Đã bỏ qua.");
        return new(true, $"Xong: {Path.GetFileName(outPath)}.", new[] { outPath });
    }

    public static Task<PdfResult> WordToPdfAsync(string input, string outDir, ScriptRunner? run = null, CancellationToken ct = default) =>
        RunAsync(input, outDir, new[] { ".doc", ".docx" }, ".pdf", true, run, ct);

    public static Task<PdfResult> PdfToWordAsync(string input, string outDir, ScriptRunner? run = null, CancellationToken ct = default) =>
        RunAsync(input, outDir, new[] { ".pdf" }, ".docx", false, run, ct);
}
