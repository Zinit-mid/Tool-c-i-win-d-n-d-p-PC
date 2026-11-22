using System.Text.RegularExpressions;
using PCTool.Core.Framework;
using PCTool.Core.Tools;
using PCTool.Core.Windows;

namespace PCTool.Core.IsoBuild;

/// <summary>Một app dựng sẵn của Windows có thể gỡ khỏi ảnh cài (chỉ danh sách cố định này, tên chính xác).</summary>
public sealed record IsoApp(string Id, string Title, bool Suggested);

/// <summary>Một chỉnh sửa Windows (chỉ tài khoản mặc định, giao diện/Explorer). Không có mục hạ bảo mật.</summary>
public sealed record IsoTweak(string Id, string Title, IReadOnlyList<(string Key, string Name, int Value)> Values);

public static class IsoBuildCatalog
{
    public static IReadOnlyList<IsoApp> Apps { get; } = new IsoApp[]
    {
        new("Microsoft.BingNews", "Tin tức (News)", true),
        new("Microsoft.BingWeather", "Thời tiết", true),
        new("Microsoft.GamingApp", "Ứng dụng Xbox", true),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", true),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox chuyển giọng nói", true),
        new("Microsoft.Xbox.TCUI", "Xbox TCUI", true),
        new("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", true),
        new("Microsoft.MicrosoftSolitaireCollection", "Trò chơi Solitaire", true),
        new("Microsoft.GetHelp", "Nhận trợ giúp", true),
        new("Microsoft.Getstarted", "Mẹo (Tips)", true),
        new("Microsoft.MicrosoftOfficeHub", "Office Hub", true),
        new("Microsoft.WindowsFeedbackHub", "Trung tâm phản hồi", true),
        new("Microsoft.YourPhone", "Phone Link", false),
        new("Microsoft.ZuneMusic", "Media Player cũ / Groove", false),
        new("Microsoft.ZuneVideo", "Phim & TV", false),
        new("Microsoft.People", "Danh bạ (People)", false),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", false),
        new("Microsoft.Todos", "Microsoft To Do", false),
        new("Clipchamp.Clipchamp", "Clipchamp", false),
        new("MicrosoftTeams", "Teams (bản cài sẵn)", false),
        new("microsoft.windowscommunicationsapps", "Thư & Lịch (Mail)", false),
        new("Microsoft.OutlookForWindows", "Outlook mới", false),
    };

    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Pers = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static IReadOnlyList<IsoTweak> Tweaks { get; } = new IsoTweak[]
    {
        new("fileext", "Hiện đuôi file (.docx, .jpg...)", new[] { (Adv, "HideFileExt", 0) }),
        new("hidden", "Hiện file ẩn", new[] { (Adv, "Hidden", 1) }),
        new("thispc", "Explorer mở This PC", new[] { (Adv, "LaunchTo", 1) }),
        new("startleft", "Nút Start sang trái (Win 11)", new[] { (Adv, "TaskbarAl", 0) }),
        new("taskview", "Ẩn nút Task View", new[] { (Adv, "ShowTaskViewButton", 0) }),
        new("dark", "Chế độ tối cho ứng dụng và hệ thống", new[] { (Pers, "AppsUseLightTheme", 0), (Pers, "SystemUsesLightTheme", 0) }),
    };
}

public sealed record IsoBuildPlan(
    string SourceIso, string OutputIso, string WorkDir, int EditionIndex,
    IReadOnlyList<string> AppIds, IReadOnlyList<string> TweakIds, string? DriverDir, string OscdimgPath)
{
    public const string DefaultOscdimg = @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe";

    /// <summary>Trả về lý do nếu kế hoạch không hợp lệ, null nếu ổn. Không chạy gì.</summary>
    public string? Validate(Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null)
    {
        fileExists ??= File.Exists; dirExists ??= Directory.Exists;
        if (!IsoInspector_IsoPath(SourceIso, out var e)) return "File ISO gốc: " + e;
        if (!fileExists(SourceIso)) return "Không thấy file ISO gốc.";
        if (!IsoInspector_IsoPath(OutputIso, out e)) return "File ISO đầu ra: " + e;
        if (string.Equals(SourceIso, OutputIso, StringComparison.OrdinalIgnoreCase)) return "ISO đầu ra không được trùng ISO gốc.";
        if (fileExists(OutputIso)) return "File ISO đầu ra đã tồn tại. PCTool không ghi đè, hãy chọn tên khác.";
        var outDir = Path.GetDirectoryName(OutputIso);
        if (string.IsNullOrEmpty(outDir) || !dirExists(outDir)) return "Thư mục chứa ISO đầu ra không tồn tại.";
        if (!SafePath.Check(WorkDir, out e)) return "Thư mục làm việc: " + e;
        if (!dirExists(WorkDir)) return "Thư mục làm việc không tồn tại.";
        if (WorkDir.TrimEnd('\\').Length <= 3) return "Hãy chọn một thư mục trong ổ, không chọn gốc ổ.";
        if (EditionIndex < 1 || EditionIndex > 50) return "Chưa chọn phiên bản Windows (edition).";
        if (AppIds.Any(a => IsoBuildCatalog.Apps.All(x => x.Id != a))) return "Có app không nằm trong danh sách cho phép.";
        if (TweakIds.Any(t => IsoBuildCatalog.Tweaks.All(x => x.Id != t))) return "Có chỉnh sửa không nằm trong danh sách cho phép.";
        if (!string.IsNullOrWhiteSpace(DriverDir))
        {
            if (!SafePath.Check(DriverDir, out e)) return "Thư mục driver: " + e;
            if (!dirExists(DriverDir)) return "Thư mục driver không tồn tại.";
        }
        if (!SafePath.Check(OscdimgPath, out e) || !OscdimgPath.EndsWith("oscdimg.exe", StringComparison.OrdinalIgnoreCase))
            return "Đường dẫn oscdimg.exe không hợp lệ.";
        if (!fileExists(OscdimgPath))
            return "Chưa có oscdimg.exe. Cài \"Windows ADK\" (chỉ chọn Deployment Tools) từ Microsoft rồi thử lại.";
        return null;
    }

    private static bool IsoInspector_IsoPath(string p, out string err)
    {
        if (!SafePath.Check(p, out err)) return false;
        if (!p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) { err = "Phải có đuôi .iso."; return false; }
        return true;
    }
}

/// <summary>Dựng script cho từng công đoạn. Mọi giá trị nhúng đã qua kiểm tra danh sách trắng hoặc SafePath.</summary>
public static class IsoBuildScripts
{
    public static string Copy(string iso, string isoDir) => $$"""
$ErrorActionPreference = 'Stop'
$iso = '{{iso}}'
$was = (Get-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue).Attached
$img = Mount-DiskImage -ImagePath $iso -StorageType ISO -Access ReadOnly -PassThru
try {
  $v = $img | Get-Volume
  $d = $v.DriveLetter
  if (-not $d) { throw 'ISO được gắn nhưng không có ký tự ổ.' }
  New-Item -ItemType Directory -Path '{{isoDir}}' -Force | Out-Null
  & robocopy.exe "${d}:\" '{{isoDir}}' /E /COPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy lỗi mã $LASTEXITCODE" }
} finally { if (-not $was) { Dismount-DiskImage -ImagePath $iso | Out-Null } }
& attrib.exe -R '{{isoDir}}\*' /S /D | Out-Null
"OK"
""";

    public static string Export(string isoDir, string work, int index) => $$"""
$ErrorActionPreference = 'Stop'
$s = '{{isoDir}}\sources'
$src = if (Test-Path "$s\install.wim") { "$s\install.wim" } elseif (Test-Path "$s\install.esd") { "$s\install.esd" } else { throw 'ISO không có install.wim/esd.' }
$new = '{{work}}\install-new.wim'
& dism.exe /Export-Image /SourceImageFile:"$src" /SourceIndex:{{index}} /DestinationImageFile:"$new" /Compress:max
if ($LASTEXITCODE -ne 0) { throw "DISM export lỗi mã $LASTEXITCODE" }
if (-not (Test-Path $new) -or (Get-Item $new).Length -le 0) { throw 'Không tạo được install.wim mới.' }
Remove-Item -LiteralPath $src -Force
Move-Item -LiteralPath $new -Destination "$s\install.wim"
"OK"
""";

    public static string Customize(string isoDir, string mount, IReadOnlyList<string> apps, IReadOnlyList<string> tweaks, string? drivers)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$mnt = '{mount}'");
        sb.AppendLine("New-Item -ItemType Directory -Path $mnt -Force | Out-Null");
        sb.AppendLine($"Mount-WindowsImage -ImagePath '{isoDir}\\sources\\install.wim' -Index 1 -Path $mnt | Out-Null");
        sb.AppendLine("$ok = $false; $loaded = $false");
        sb.AppendLine("try {");
        if (apps.Count > 0)
        {
            sb.AppendLine($"  $names = @({string.Join(",", apps.Select(a => $"'{a}'"))})");
            sb.AppendLine("  $removed = 0");
            sb.AppendLine("  foreach ($p in (Get-AppxProvisionedPackage -Path $mnt)) { if ($names -contains $p.DisplayName) { Remove-AppxProvisionedPackage -Path $mnt -PackageName $p.PackageName | Out-Null; $removed++ } }");
            sb.AppendLine("  Write-Output \"Đã gỡ $removed app\"");
        }
        if (!string.IsNullOrWhiteSpace(drivers))
        {
            sb.AppendLine($"  Add-WindowsDriver -Path $mnt -Driver '{drivers}' -Recurse | Out-Null");
            sb.AppendLine("  Write-Output 'Đã nhúng driver'");
        }
        if (tweaks.Count > 0)
        {
            sb.AppendLine("  & reg.exe load HKLM\\PCT_DEF \"$mnt\\Users\\Default\\NTUSER.DAT\" | Out-Null");
            sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw 'Không nạp được registry tài khoản mặc định.' }");
            sb.AppendLine("  $loaded = $true");
            foreach (var id in tweaks)
                foreach (var (key, name, value) in IsoBuildCatalog.Tweaks.First(t => t.Id == id).Values)
                {
                    sb.AppendLine($"  & reg.exe add 'HKLM\\PCT_DEF\\{key}' /v {name} /t REG_DWORD /d {value} /f | Out-Null");
                    sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw 'reg add lỗi' }");
                }
            sb.AppendLine("  [gc]::Collect(); [gc]::WaitForPendingFinalizers()");
            sb.AppendLine("  & reg.exe unload HKLM\\PCT_DEF | Out-Null; $loaded = $false");
            sb.AppendLine("  Write-Output 'Đã áp dụng chỉnh sửa'");
        }
        sb.AppendLine("  $ok = $true");
        sb.AppendLine("} finally {");
        sb.AppendLine("  if ($loaded) { [gc]::Collect(); & reg.exe unload HKLM\\PCT_DEF | Out-Null }");
        sb.AppendLine("  if ($ok) { Dismount-WindowsImage -Path $mnt -Save | Out-Null } else { Dismount-WindowsImage -Path $mnt -Discard | Out-Null }");
        sb.AppendLine("}");
        sb.AppendLine("\"OK\"");
        return sb.ToString();
    }

    public static string Build(string isoDir, string oscdimg, string output) => $$"""
$ErrorActionPreference = 'Stop'
$etfs = '{{isoDir}}\boot\etfsboot.com'
$efi = '{{isoDir}}\efi\microsoft\boot\efisys.bin'
if (-not (Test-Path $efi)) { throw 'Thiếu efi\microsoft\boot\efisys.bin trong ISO.' }
$bd = if (Test-Path $etfs) { "2#p0,e,b$etfs#pEF,e,b$efi" } else { "1#pEF,e,b$efi" }
& '{{oscdimg}}' -m -o -u2 -udfver102 "-bootdata:$bd" '{{isoDir}}' '{{output}}'
if ($LASTEXITCODE -ne 0) { throw "oscdimg lỗi mã $LASTEXITCODE" }
"OK"
""";

    /// <summary>Gỡ ảnh còn gắn (nếu có) rồi xóa thư mục làm việc do PCTool tạo (tên bắt đầu PCTool-ISO-).</summary>
    public static string Cleanup(string stage, string mount) => $$"""
$ErrorActionPreference = 'Continue'
try { if (Get-WindowsImage -Mounted | Where-Object { $_.Path -eq '{{mount}}' }) { Dismount-WindowsImage -Path '{{mount}}' -Discard | Out-Null } } catch { }
try { if (Get-WindowsImage -Mounted | Where-Object { $_.Path -eq '{{mount}}' }) { Write-Output 'STILL'; exit 0 } } catch { }
Remove-Item -LiteralPath '{{stage}}' -Recurse -Force -ErrorAction SilentlyContinue
"OK"
""";
}

public sealed class BuildIsoFix : IFix
{
    private readonly IsoBuildPlan _p;
    private readonly ScriptRunner _run;
    private readonly Func<string, long> _fileLen;
    private readonly Func<string, long> _free;
    private readonly string _stamp;
    private readonly Func<string, bool>? _fileExists, _dirExists;

    public BuildIsoFix(IsoBuildPlan plan, ScriptRunner? run = null, Func<string, long>? fileLength = null, Func<string, long>? freeBytes = null, string? stamp = null,
        Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null)
    {
        _p = plan;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 7200));
        _fileLen = fileLength ?? (f => File.Exists(f) ? new FileInfo(f).Length : 0);
        _free = freeBytes ?? (d => new DriveInfo(d).AvailableFreeSpace);
        _fileExists = fileExists; _dirExists = dirExists;
        _stamp = stamp ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");
    }

    public const long MinOutputBytes = 100L * 1024 * 1024;

    public string Stage => _p.WorkDir.TrimEnd('\\') + "\\PCTool-ISO-" + _stamp;
    public string IsoDir => Stage + "\\iso";
    public string Mount => Stage + "\\mount";

    public string Title => "Tạo file ISO Windows tùy chỉnh";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Chép nội dung ISO gốc sang thư mục làm việc (ISO gốc chỉ đọc, không bị sửa): {Stage}",
        $"Xuất phiên bản số {_p.EditionIndex} thành install.wim mới (nén tối đa, rất lâu)",
        $"Gỡ {_p.AppIds.Count} app dựng sẵn, nhúng driver: {(string.IsNullOrWhiteSpace(_p.DriverDir) ? "không" : "có")}, áp dụng {_p.TweakIds.Count} chỉnh sửa giao diện cho tài khoản mới",
        $"Đóng gói thành file ISO mới (không ghi đè): {_p.OutputIso}",
        "Xóa thư mục làm việc sau khi xong. Cần ổ trống khoảng 3 lần dung lượng ISO. Có thể mất 30-60 phút.",
        "Không tắt Defender, UAC hay Windows Update; không kích hoạt bản quyền."
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (_p.Validate(_fileExists, _dirExists) is { } bad) return FixResult.Failed(bad);
        var root = Path.GetPathRoot(_p.WorkDir) ?? "";
        long need = _fileLen(_p.SourceIso) * 3 + 2L * 1024 * 1024 * 1024;
        if (_free(root) < need)
            return FixResult.Failed($"Ổ làm việc không đủ chỗ trống: cần khoảng {need / (1024 * 1024 * 1024)} GB.");

        async Task<string?> Step(string name, string script)
        {
            progress.Report(name);
            var r = await _run(script, ct);
            if (r.ExitCode != 0 || !r.Output.Contains("OK"))
                return name + " lỗi: " + (string.IsNullOrWhiteSpace(r.Error) ? r.Output.Trim() : r.Error.Trim());
            return null;
        }

        string? err = null;
        try
        {
            err = await Step("Bước 1/4: chép nội dung ISO gốc...", IsoBuildScripts.Copy(_p.SourceIso, IsoDir))
               ?? await Step("Bước 2/4: xuất install.wim (lâu)...", IsoBuildScripts.Export(IsoDir, Stage, _p.EditionIndex))
               ?? await Step("Bước 3/4: gỡ app, nhúng driver, chỉnh Windows...", IsoBuildScripts.Customize(IsoDir, Mount, _p.AppIds, _p.TweakIds, _p.DriverDir))
               ?? await Step("Bước 4/4: đóng gói ISO...", IsoBuildScripts.Build(IsoDir, _p.OscdimgPath, _p.OutputIso));
        }
        catch (OperationCanceledException) { err = "Đã dừng."; }
        finally
        {
            progress.Report("Dọn thư mục làm việc...");
            try { await _run(IsoBuildScripts.Cleanup(Stage, Mount), CancellationToken.None); } catch { }
        }
        return err == null ? FixResult.Succeeded("Đã tạo ISO: " + _p.OutputIso) : FixResult.Failed(err);
    }

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_fileLen(_p.OutputIso) >= MinOutputBytes);
}
