using System.Text.Json;
using PCTool.Core.Folders;
using PCTool.Core.Framework;
using PCTool.Core.Tools;
using PCTool.Core.Windows;

namespace PCTool.Core.Stubborn;

/// <summary>Kiểm tra file/thư mục "cứng đầu" trước khi đụng tới. Dùng lại luật của FolderGuard (không Windows/Program Files/ProgramData/AppData/gốc ổ/gốc hồ sơ/liên kết).</summary>
public static class StubbornGuard
{
    public static bool Check(string? path, out string error)
    {
        error = "";
        if (!SafePath.Check(path, out error)) return false;
        path = path!.TrimEnd('\\');
        bool isDir = Directory.Exists(path), isFile = File.Exists(path);
        if (!isDir && !isFile) { error = "Không thấy file/thư mục này."; return false; }
        try
        {
            if ((new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0) { error = "Đây là liên kết (shortcut thư mục), PCTool không đụng tới."; return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "Không đọc được thuộc tính (có thể bị khóa quyền)."; return false; }
        var dir = isDir ? path : Path.GetDirectoryName(path);
        return FolderGuard.Check(dir, out error);
    }

    public static string Leaf(string path)
    {
        var p = path.TrimEnd('\\');
        return p[(p.LastIndexOf('\\') + 1)..];
    }

    public static string MoveCode(string path) => "CHUYEN " + Leaf(path);
    public const string GrantCode = "CAP QUYEN";
    public const string EmptyTrashCode = "XOA THUNG";
}

/// <summary>
/// Cấp lại toàn quyền cho nhóm Administrators trên một thư mục/file (takeown + icacls), SAU KHI lưu bản sao quyền cũ
/// vào %ProgramData%\PCTool\acl để khôi phục được (icacls /restore). Không xóa, không di chuyển gì.
/// </summary>
public sealed class GrantFullAccessFix : IFix
{
    private readonly ScriptRunner _run;
    private bool _ok;

    public GrantFullAccessFix(string path, string confirm, ScriptRunner? run = null)
    {
        if (!StubbornGuard.Check(path, out var err)) throw new ArgumentException(err, nameof(path));
        if (confirm != StubbornGuard.GrantCode) throw new ArgumentException($"Chưa gõ đúng mã xác nhận ({StubbornGuard.GrantCode}).", nameof(confirm));
        Path = path.TrimEnd('\\');
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 1800));
    }

    public string Path { get; }
    public string Title => "Cấp lại toàn quyền: " + StubbornGuard.Leaf(Path);
    public IReadOnlyList<string> Actions => new[]
    {
        $"Lưu bản sao quyền hiện tại của {Path} vào %ProgramData%\\PCTool\\acl (để khôi phục được)",
        "Đổi chủ sở hữu sang nhóm Administrators và cấp toàn quyền cho Administrators, cho cả thư mục con và file bên trong",
        "KHÔNG xóa, KHÔNG di chuyển file nào. Chỉ làm với thư mục bạn chọn, không đụng thư mục hệ thống",
        "Cấp quyền rộng cho một thư mục là thay đổi bảo mật: chỉ làm với dữ liệu của bạn (ví dụ thư mục cũ từ ổ Windows cũ)"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$p = '{{Path}}'
$isDir = Test-Path -LiteralPath $p -PathType Container
$aclDir = Join-Path $env:ProgramData 'PCTool\acl'
New-Item -ItemType Directory -Force -Path $aclDir | Out-Null
$aclFile = Join-Path $aclDir ((Get-Date -Format 'yyyyMMdd-HHmmss') + '.acl')
$parent = Split-Path -Parent $p
Push-Location $parent
try {
  & icacls (Split-Path -Leaf $p) /save $aclFile /T /C | Out-Null
  if (-not (Test-Path -LiteralPath $aclFile)) { throw 'Không lưu được bản sao quyền cũ nên dừng lại, chưa đổi gì.' }
} finally { Pop-Location }
if ($isDir) { & takeown /F $p /A /R /D Y | Out-Null } else { & takeown /F $p /A | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'takeown báo lỗi.' }
$grant = if ($isDir) { '*S-1-5-32-544:(OI)(CI)F' } else { '*S-1-5-32-544:F' }
& icacls $p /grant $grant /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'icacls báo lỗi (một số file có thể đang bị khóa).' }
Write-Output ('OK:' + $aclFile)
""";

    public string VerifyScript() => $$"""
$acl = Get-Acl -LiteralPath '{{Path}}'
$full = $acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]) | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-544' -and $_.AccessControlType -eq 'Allow' -and (($_.FileSystemRights -band 2032127) -eq 2032127) }
[string]([bool]$full)
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang lưu quyền cũ rồi cấp quyền (thư mục lớn có thể mất lâu)...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0) return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không cấp được quyền." : r.Error.Trim());
        var last = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (!last.StartsWith("OK:", StringComparison.Ordinal)) return FixResult.Failed("Không đọc được kết quả cấp quyền.");
        _ok = true;
        return FixResult.Succeeded($"Đã cấp toàn quyền cho Administrators. Quyền cũ lưu ở {last[3..]} (khôi phục: mở Command Prompt quyền Admin, chạy icacls \"{System.IO.Path.GetDirectoryName(Path)}\" /restore \"{last[3..]}\").");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (!_ok) return false;
        var r = await _run(VerifyScript(), ct);
        return r.ExitCode == 0 && r.Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// "Xóa" file/thư mục cứng đầu theo cách AN TOÀN: lấy quyền rồi CHUYỂN vào thùng riêng của PCTool
/// (%ProgramData%\PCTool\trash\&lt;ngày giờ&gt;) kèm ghi vị trí cũ. Không xóa thật; muốn xóa hẳn dùng EmptyTrashFix.
/// File đang bị chương trình khác giữ thì báo lỗi (PCTool không tắt chương trình nào).
/// </summary>
public sealed class StubbornMoveToTrashFix : IFix
{
    private readonly ScriptRunner _run;
    private bool _ok;

    public StubbornMoveToTrashFix(string path, string confirm, ScriptRunner? run = null)
    {
        if (!StubbornGuard.Check(path, out var err)) throw new ArgumentException(err, nameof(path));
        if (confirm != StubbornGuard.MoveCode(path)) throw new ArgumentException($"Chưa gõ đúng mã xác nhận ({StubbornGuard.MoveCode(path)}).", nameof(confirm));
        Path = path.TrimEnd('\\');
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 1800));
    }

    public string Path { get; }
    public string Title => "Chuyển vào thùng PCTool: " + StubbornGuard.Leaf(Path);
    public IReadOnlyList<string> Actions => new[]
    {
        $"Lấy quyền sở hữu {Path} rồi CHUYỂN nó vào %ProgramData%\\PCTool\\trash\\<ngày giờ> (kèm file ghi vị trí cũ)",
        "Chưa xóa hẳn: bạn có thể chép lại từ thùng. Chỉ khi bấm \"Dọn thùng PCTool\" mới xóa thật",
        "Nếu file đang được chương trình khác dùng thì sẽ báo lỗi; hãy đóng chương trình đó (PCTool không tự tắt)",
        "Nếu thùng và file khác ổ đĩa thì phải chép sang rồi mới bỏ bản gốc, có thể lâu"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$p = '{{Path}}'
$name = Split-Path -Leaf $p
$isDir = Test-Path -LiteralPath $p -PathType Container
$trash = Join-Path $env:ProgramData ('PCTool\trash\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $trash | Out-Null
if ($isDir) { & takeown /F $p /A /R /D Y | Out-Null } else { & takeown /F $p /A | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'takeown báo lỗi.' }
$grant = if ($isDir) { '*S-1-5-32-544:(OI)(CI)F' } else { '*S-1-5-32-544:F' }
& icacls $p /grant $grant /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'icacls báo lỗi (một số file có thể đang bị khóa).' }
Set-Content -LiteralPath (Join-Path $trash 'vi-tri-cu.txt') -Value $p -Encoding UTF8
Move-Item -LiteralPath $p -Destination (Join-Path $trash $name)
[ordered]@{ name = $name; trash = $trash; originalGone = (-not (Test-Path -LiteralPath $p)); inTrash = (Test-Path -LiteralPath (Join-Path $trash $name)) } | ConvertTo-Json -Compress
""";

    public static IReadOnlyList<string> Problems(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim().Split('\n').Last().Trim());
            var r = doc.RootElement;
            var list = new List<string>();
            if (!(r.TryGetProperty("originalGone", out var g) && g.ValueKind == JsonValueKind.True)) list.Add("Bản gốc vẫn còn.");
            if (!(r.TryGetProperty("inTrash", out var t) && t.ValueKind == JsonValueKind.True)) list.Add("Không thấy bản đã chuyển trong thùng.");
            return list;
        }
        catch (JsonException) { return new[] { "Không đọc được kết quả." }; }
    }

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang lấy quyền và chuyển vào thùng PCTool...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0) return FixResult.Failed((string.IsNullOrWhiteSpace(r.Error) ? "Không chuyển được." : r.Error.Trim()) + " Chưa có gì bị xóa.");
        var problems = Problems(r.Output);
        if (problems.Count > 0) return FixResult.Failed("Chưa đạt: " + string.Join(" ", problems));
        _ok = true;
        return FixResult.Succeeded("Đã chuyển vào thùng PCTool (%ProgramData%\\PCTool\\trash). Chưa xóa hẳn; dọn thùng khi chắc chắn.");
    }

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_ok);
}

/// <summary>Xóa THẬT nội dung thùng của PCTool (%ProgramData%\PCTool\trash). Đường dẫn cố định, không nhận đường dẫn từ ngoài. Không thể hoàn tác.</summary>
public sealed class EmptyTrashFix : IFix
{
    private readonly ScriptRunner _run;

    public EmptyTrashFix(string confirm, ScriptRunner? run = null)
    {
        if (confirm != StubbornGuard.EmptyTrashCode) throw new ArgumentException($"Chưa gõ đúng mã xác nhận ({StubbornGuard.EmptyTrashCode}).", nameof(confirm));
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 3600));
    }

    public string Title => "Dọn thùng PCTool (xóa thật)";
    public IReadOnlyList<string> Actions => new[]
    {
        "XÓA HẲN mọi thứ trong %ProgramData%\\PCTool\\trash (những gì bạn đã chuyển vào bằng PCTool)",
        "KHÔNG THỂ hoàn tác. Hãy mở thư mục đó xem lại trước",
        "Không đụng gì ngoài thư mục thùng"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public const string Script = """
$ErrorActionPreference = 'Stop'
$t = Join-Path $env:ProgramData 'PCTool\trash'
if (-not (Test-Path -LiteralPath $t -PathType Container)) { Write-Output 'EMPTY'; exit 0 }
foreach ($c in Get-ChildItem -LiteralPath $t -Force) {
  if ($c.PSIsContainer) { & cmd.exe /c rd /s /q ('"' + $c.FullName + '"') } else { Remove-Item -LiteralPath $c.FullName -Force }
}
$left = @(Get-ChildItem -LiteralPath $t -Force).Count
Write-Output ('LEFT:' + $left)
""";

    private bool _ok;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var r = await _run(Script, ct);
        if (r.ExitCode != 0) return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không dọn được thùng." : r.Error.Trim());
        var last = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (last == "EMPTY") { _ok = true; return FixResult.Succeeded("Thùng đang trống, không có gì để dọn."); }
        if (last == "LEFT:0") { _ok = true; return FixResult.Succeeded("Đã dọn sạch thùng PCTool."); }
        if (last.StartsWith("LEFT:", StringComparison.Ordinal)) return FixResult.Failed($"Còn {last[5..]} mục chưa xóa được (có thể đang bị chương trình khác giữ).");
        return FixResult.Failed("Không đọc được kết quả.");
    }

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_ok);
}
