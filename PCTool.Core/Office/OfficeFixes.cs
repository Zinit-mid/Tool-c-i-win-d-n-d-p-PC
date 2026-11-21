using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Office;

public enum OfficeApp { Word, Excel }

/// <summary>Cách mở Word/Excel ở chế độ an toàn (tắt add-in và tùy biến). Chỉ có 2 lệnh cố định, không nhận chuỗi từ ngoài.</summary>
public static class OfficeSafeMode
{
    public static (string File, string Args) Command(OfficeApp app) => app switch
    {
        OfficeApp.Word => ("winword.exe", "/safe"),
        OfficeApp.Excel => ("excel.exe", "/safe"),
        _ => throw new ArgumentOutOfRangeException(nameof(app))
    };
}

/// <summary>
/// Đặt lại mẫu Normal.dotm của Word bằng cách ĐỔI TÊN (không xóa): Word tự tạo mẫu mới ở lần mở sau.
/// Hoàn tác được bằng cách đổi tên file .bak trở lại. Mất các tùy biến lưu trong mẫu (macro, kiểu chữ, phím tắt).
/// </summary>
public sealed class ResetWordTemplateFix : IFix
{
    public const string ExecuteScript = """
$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'WINWORD' -ErrorAction SilentlyContinue) { Write-Output 'RUNNING'; exit 0 }
$p = Join-Path $env:APPDATA 'Microsoft\Templates\Normal.dotm'
if (-not (Test-Path -LiteralPath $p)) { Write-Output 'NOFILE'; exit 0 }
$new = 'Normal.dotm.bak-' + (Get-Date -Format 'yyyyMMdd-HHmm')
Rename-Item -LiteralPath $p -NewName $new
Write-Output ('OK:' + $new)
""";

    public const string VerifyScript = """
-not (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Templates\Normal.dotm'))
""";

    private readonly ScriptRunner _run;

    public ResetWordTemplateFix(ScriptRunner? run = null) =>
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 60));

    public string Title => "Đặt lại mẫu Normal.dotm của Word";

    public IReadOnlyList<string> Actions => new[]
    {
        "Đổi tên file Normal.dotm (trong %APPDATA%\\Microsoft\\Templates) thành Normal.dotm.bak-ngày-giờ. KHÔNG xóa file nào",
        "Lần mở Word sau, Word tự tạo mẫu mới sạch. Có thể sửa lỗi Word treo, đơ, mở chậm hoặc định dạng lạ",
        "Các tùy biến lưu trong mẫu (macro, kiểu chữ, phím tắt) sẽ mất. Muốn quay lại, đóng Word và đổi tên file .bak về Normal.dotm",
        "Word phải được đóng hẳn trước"
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang đổi tên mẫu Normal.dotm...");
        var r = await _run(ExecuteScript, ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không đổi tên được Normal.dotm." : r.Error.Trim());

        var last = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (last == "RUNNING") return FixResult.Failed("Word đang chạy. Hãy đóng Word hoàn toàn rồi thử lại.");
        if (last == "NOFILE") return FixResult.Failed("Không thấy file Normal.dotm (Word chưa tạo hoặc đã được đặt lại), không cần làm gì.");
        if (last.StartsWith("OK:", StringComparison.Ordinal))
            return FixResult.Succeeded($"Đã đổi tên thành {last[3..]}. Mở Word lại để Word tạo mẫu mới.");
        return FixResult.Failed("Không đọc được kết quả từ PowerShell, hãy kiểm tra thư mục Templates.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        (await _run(VerifyScript, ct)).Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
}
