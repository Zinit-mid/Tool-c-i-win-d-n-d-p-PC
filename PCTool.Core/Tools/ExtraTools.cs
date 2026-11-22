using System.Text.Json;
using System.Text.RegularExpressions;
using PCTool.Core.Backup;
using PCTool.Core.Folders;
using PCTool.Core.Framework;
using PCTool.Core.Restore;
using PCTool.Core.Windows;

namespace PCTool.Core.Tools;

/// <summary>Kiểm tra đường dẫn trước khi đưa vào script PowerShell: chỉ ổ cục bộ đầy đủ, không ký tự đặc biệt, không "..".</summary>
public static class SafePath
{
    private static readonly Regex Bad = new(@"['""`$;&|<>%{}\[\]\r\n\t]", RegexOptions.Compiled);

    public static bool Check(string? path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || !Regex.IsMatch(path, @"^[A-Za-z]:\\"))
        { error = "Hãy chọn thư mục đầy đủ trên ổ cục bộ, ví dụ D:\\Thu muc."; return false; }
        if (Bad.IsMatch(path) || path.Split('\\').Any(s => s == ".." || s == "."))
        { error = "Đường dẫn có ký tự không cho phép. Hãy chọn thư mục khác."; return false; }
        return true;
    }
}

/// <summary>Bật/tắt hiện file ẩn của Explorer (chỉ giá trị Hidden của tài khoản đang dùng; KHÔNG đụng file hệ thống được bảo vệ).</summary>
public sealed class ShowHiddenFilesFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly bool _show;

    public ShowHiddenFilesFix(bool show, ScriptRunner? run = null)
    {
        _show = show;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 30));
    }

    private int Value => _show ? 1 : 2;

    public string Title => _show ? "Hiện file ẩn" : "Ẩn file ẩn";
    public IReadOnlyList<string> Actions => new[]
    {
        _show ? "Đặt Explorer hiện các file/thư mục có thuộc tính Ẩn (Hidden)" : "Đặt Explorer ẩn các file/thư mục có thuộc tính Ẩn (Hidden)",
        "Chỉ đổi một giá trị trong tài khoản đang dùng; không đổi thuộc tính của file nào, không hiện file hệ thống được bảo vệ",
        "Mở lại cửa sổ Explorer hoặc bấm F5 để thấy thay đổi. Hoàn tác bằng nút ngược lại"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public string SetScript() => $$"""
$ErrorActionPreference = 'Stop'
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
Set-ItemProperty -Path $k -Name Hidden -Value {{Value}} -Type DWord
""";

    public const string ReadScript = """
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
[string](Get-ItemProperty -Path $k -Name Hidden -ErrorAction Stop).Hidden
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var r = await _run(SetScript(), ct);
        return r.ExitCode == 0
            ? FixResult.Succeeded((_show ? "Đã bật hiện file ẩn." : "Đã tắt hiện file ẩn.") + " Mở lại Explorer hoặc bấm F5 để thấy.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không đổi được thiết lập." : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var r = await _run(ReadScript, ct);
        return r.ExitCode == 0 && r.Output.Trim() == Value.ToString();
    }
}

/// <summary>
/// Sao chép cả thư mục sang nơi khác. KHÔNG ghi đè, KHÔNG xóa, KHÔNG di chuyển: chỉ chép file chưa có ở nơi đến
/// (robocopy /XC /XN /XO). File chép vào thư mục con mang tên thư mục nguồn.
/// </summary>
public sealed class BulkCopyFix : IFix
{
    private readonly ScriptRunner _run;
    private bool _ok;

    public BulkCopyFix(string source, string destRoot, ScriptRunner? run = null)
    {
        if (!FolderGuard.Check(source, out var e1)) throw new ArgumentException(e1, nameof(source));
        if (!SafePath.Check(source, out var e2)) throw new ArgumentException(e2, nameof(source));
        if (!BackupPath.IsValid(destRoot, out var e3)) throw new ArgumentException(e3, nameof(destRoot));
        Source = source.TrimEnd('\\');
        var leaf = Source[(Source.LastIndexOf('\\') + 1)..];
        if (leaf.Length == 0 || leaf.EndsWith(':')) throw new ArgumentException("Không sao chép nguyên cả ổ đĩa.", nameof(source));
        Dest = destRoot.TrimEnd('\\') + "\\" + leaf;
        if (Same(Source, Dest) || Inside(Dest, Source) || Inside(Source, Dest))
            throw new ArgumentException("Nơi đến không được trùng hoặc nằm trong thư mục nguồn (và ngược lại).", nameof(destRoot));
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 7200));
    }

    public string Source { get; }
    public string Dest { get; }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string child, string parent) =>
        (child.TrimEnd('\\') + "\\").StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public string Title => "Sao chép thư mục hàng loạt";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Chép nội dung {Source} sang {Dest}",
        "Chỉ chép file CHƯA CÓ ở nơi đến; file đã có được giữ nguyên, không ghi đè, không xóa file nào (cả nguồn lẫn đích)",
        "Có thể mất lâu nếu dữ liệu lớn"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$s = '{{Source}}'
$d = '{{Dest}}'
if (-not (Test-Path -LiteralPath $s -PathType Container)) { throw 'Không thấy thư mục nguồn.' }
New-Item -ItemType Directory -Force -Path $d | Out-Null
$src = (Get-ChildItem -LiteralPath $s -File -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object).Count
& robocopy $s $d /E /XC /XN /XO /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NP /NFL /NDL /NJH /NJS /MT:8 | Out-Null
$code = $LASTEXITCODE
$dst = (Get-ChildItem -LiteralPath $d -File -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object).Count
[ordered]@{ folders = @([ordered]@{ name = 'copy'; srcFiles = [long]$src; destFiles = [long]$dst; exit = [int]$code }) } | ConvertTo-Json -Compress -Depth 4
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang sao chép (không ghi đè file đã có)...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không sao chép được." : r.Error.Trim());
        var problems = RestoreVerifier.Problems(r.Output);
        if (problems.Count > 0) return FixResult.Failed("Sao chép chưa đạt: " + string.Join(" ", problems) + " Thư mục nguồn vẫn còn nguyên.");
        _ok = true;
        return FixResult.Succeeded($"Đã chép sang {Dest}. File đã có sẵn được giữ nguyên.");
    }

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_ok);
}

public sealed record DeviceReport(IReadOnlyList<string> Screens, IReadOnlyList<string> Keyboards,
    IReadOnlyList<string> Cameras, IReadOnlyList<string> Microphones)
{
    public const string Note = "Lưu ý: đây chỉ cho biết Windows có THẤY thiết bị, chưa thử thiết bị hoạt động thật (hình, tiếng, phím). Hãy tự thử bằng app Camera, Ghi âm, hoặc gõ phím.";

    public string ToText()
    {
        static string Sect(string title, IReadOnlyList<string> l) =>
            title + ":\n" + (l.Count == 0 ? "  (Windows không thấy thiết bị nào)" : string.Join("\n", l.Select(x => "  • " + x)));
        return string.Join("\n\n", Sect("Màn hình / card đồ họa (VGA)", Screens), Sect("Bàn phím", Keyboards),
            Sect("Webcam", Cameras), Sect("Micro", Microphones)) + "\n\n" + Note;
    }
}

/// <summary>Chỉ đọc: liệt kê màn hình/VGA, bàn phím, webcam, micro mà Windows nhìn thấy.</summary>
public static class DeviceProbe
{
    public const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$o = [ordered]@{}
$o.video = @(Get-CimInstance Win32_VideoController | Select-Object Name,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,DriverVersion,AdapterRAM,Status)
$o.keyboards = @(Get-CimInstance Win32_Keyboard | Select-Object Name,Status)
$o.cameras = @(Get-PnpDevice -Class Camera,Image -PresentOnly | Select-Object FriendlyName,Status)
$o.mics = @(Get-PnpDevice -Class AudioEndpoint -PresentOnly | Where-Object { $_.FriendlyName -match 'Mic' } | Select-Object FriendlyName,Status)
$o | ConvertTo-Json -Depth 4 -Compress
""";

    private static IEnumerable<JsonElement> Items(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array) { foreach (var e in v.EnumerateArray()) yield return e; }
        else if (v.ValueKind == JsonValueKind.Object) yield return v;
    }

    private static string S(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long N(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static DeviceReport Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("Kết quả không đúng định dạng.");

            var screens = Items(r, "video").Select(e =>
            {
                var res = N(e, "CurrentHorizontalResolution") > 0 ? $", {N(e, "CurrentHorizontalResolution")}x{N(e, "CurrentVerticalResolution")}" : "";
                var hz = N(e, "CurrentRefreshRate") > 0 ? $" @ {N(e, "CurrentRefreshRate")} Hz" : "";
                var ram = N(e, "AdapterRAM") > 0 ? $", VRAM {N(e, "AdapterRAM") / 1048576} MB (số WMI có thể sai với card >4 GB)" : "";
                var drv = S(e, "DriverVersion").Length > 0 ? $", driver {S(e, "DriverVersion")}" : "";
                return $"{S(e, "Name")}{res}{hz}{ram}{drv}";
            }).Where(x => x.Length > 0).ToList();
            IReadOnlyList<string> Names(string prop, string name) => Items(r, prop)
                .Select(e => (S(e, name) + (S(e, "Status") is { Length: > 0 } st && st != "OK" ? $" (trạng thái: {st})" : "")).Trim())
                .Where(x => x.Length > 0).ToList();

            return new DeviceReport(screens, Names("keyboards", "Name"), Names("cameras", "FriendlyName"), Names("mics", "FriendlyName"));
        }
        catch (JsonException) { throw new FormatException("Không đọc được kết quả kiểm tra thiết bị."); }
    }

    public static async Task<DeviceReport> ReadAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c, 60);
        var r = await run(Script, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được thiết bị." : r.Error.Trim());
        return Parse(r.Output);
    }
}
