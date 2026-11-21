using System.Text;
using System.Text.Json;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Reinstall;

/// <summary>GĐ5: cài lại Windows trực tiếp trên máy. PCTool chỉ kiểm tra, chốt, rồi MỞ công cụ của Microsoft (có giao diện). PCTool không tự bấm gì, không tự xóa, không tự khởi động lại máy.</summary>
public enum DirectMode { RepairKeepAll, KeepFilesOnly, CleanAll, ResetPc }

public static class DirectModes
{
    public const string CleanPhrase = "XOA SACH";
    public const long MinFreeBytes = 20L * 1024 * 1024 * 1024;
    public const long WarnFreeBytes = 30L * 1024 * 1024 * 1024;

    public static string Vietnamese(DirectMode m) => m switch
    {
        DirectMode.RepairKeepAll => "C1. Sửa Windows, giữ app + file",
        DirectMode.KeepFilesOnly => "C2. Giữ file cá nhân, bỏ app",
        DirectMode.CleanAll => "C3. Xóa sạch (không giữ gì)",
        _ => "C4. Đặt lại PC (Reset this PC)"
    };

    public static bool NeedsIso(DirectMode m) => m != DirectMode.ResetPc;
    public static bool NeedsBackup(DirectMode m) => m != DirectMode.RepairKeepAll;

    /// <summary>Hướng dẫn ở màn hình của Microsoft (PCTool không bấm thay bạn).</summary>
    public static IReadOnlyList<string> Guide(DirectMode m) => m switch
    {
        DirectMode.RepairKeepAll => new[]
        {
            "Bộ cài Windows sẽ mở. Ở màn hình đầu bạn có thể chọn không tải cập nhật.",
            "Tới màn hình \"Ready to install\" (Sẵn sàng cài đặt): đảm bảo ghi \"Keep personal files and apps\" (Giữ file cá nhân và ứng dụng).",
            "Bấm Install khi bạn chắc chắn. Máy sẽ khởi động lại nhiều lần, đừng tắt nguồn."
        },
        DirectMode.KeepFilesOnly => new[]
        {
            "Bộ cài Windows sẽ mở.",
            "Ở màn hình \"Ready to install\": bấm \"Change what to keep\" (Thay đổi nội dung cần giữ lại) và chọn \"Keep personal files only\" (Chỉ giữ file cá nhân). Nếu chỉ thấy \"Nothing\" thì DỪNG, đừng bấm Install, và xem lại ISO.",
            "Sau khi xong, app phải cài lại: mở PCTool từ thư mục sao lưu, tab Khôi phục sau cài Win."
        },
        DirectMode.CleanAll => new[]
        {
            "Bộ cài Windows sẽ mở.",
            "Ở màn hình \"Ready to install\": bấm \"Change what to keep\" và chọn \"Nothing\" (Không giữ gì).",
            "Windows cũ sẽ chuyển vào thư mục Windows.old (Windows tự xóa sau một thời gian, KHÔNG phải bản sao lưu).",
            "Bạn tự kiểm tra kỹ rồi mới bấm Install. Sau khi xong: mở PCTool từ thư mục sao lưu, tab Khôi phục sau cài Win."
        },
        _ => new[]
        {
            "Cửa sổ Cài đặt (Settings) mở ra trang Recovery (Khôi phục). Ở mục \"Reset this PC\" bấm \"Reset PC\" (Win 11) hoặc \"Get started\" (Win 10).",
            "Chọn \"Keep my files\" (giữ file) hoặc \"Remove everything\" (xóa hết), rồi chọn tải lại từ đám mây (\"Cloud download\") hoặc cài lại từ máy (\"Local reinstall\").",
            "Đọc kỹ màn hình tóm tắt của Microsoft rồi mới xác nhận. PCTool không bấm thay bạn."
        }
    };
}

public sealed record SystemFacts(
    int Build, string Caption, string EditionId, string Arch, string UiCulture,
    long SystemFreeBytes, int SystemDisk, bool BatteryPresent, bool OnBattery, int BatteryPercent,
    bool BitLockerKnown, bool BitLockerOn, bool NetworkUp);

public sealed record BackupFacts(
    bool Exists, int BackupDisk, int SystemDisk, bool HasUserData, bool HasManifest, bool HasUserDataJson, bool HasAppPlan, bool HasKit);

public static class DirectFactsParser
{
    private static long L(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;
    private static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static JsonElement Root(string json, JsonDocument[] holder)
    {
        try
        {
            holder[0] = JsonDocument.Parse(json.Trim().Split('\n').Last().Trim());
            if (holder[0].RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Kết quả không đúng định dạng.");
            return holder[0].RootElement;
        }
        catch (JsonException) { throw new FormatException("Không đọc được kết quả kiểm tra."); }
    }

    public static SystemFacts ParseSystem(string json)
    {
        var h = new JsonDocument[1];
        try
        {
            var r = Root(json, h);
            return new SystemFacts((int)L(r, "build"), S(r, "caption"), S(r, "edition"), S(r, "arch"), S(r, "ui"),
                L(r, "free"), (int)L(r, "systemDisk"), B(r, "battery"), B(r, "onBattery"), (int)L(r, "percent"),
                B(r, "blKnown"), B(r, "blOn"), B(r, "net"));
        }
        finally { h[0]?.Dispose(); }
    }

    public static BackupFacts ParseBackup(string json)
    {
        var h = new JsonDocument[1];
        try
        {
            var r = Root(json, h);
            return new BackupFacts(B(r, "exists"), (int)L(r, "backupDisk"), (int)L(r, "systemDisk"),
                B(r, "userdata"), B(r, "manifest"), B(r, "userdataJson"), B(r, "plan"), B(r, "kit"));
        }
        finally { h[0]?.Dispose(); }
    }
}

/// <summary>Đọc thông tin máy và bản sao lưu. CHỈ ĐỌC.</summary>
public sealed class DirectFactsScanner
{
    private readonly ScriptRunner _run;
    public DirectFactsScanner() : this((s, ct) => PowerShellRunner.RunAsync(s, ct, 120)) { }
    public DirectFactsScanner(ScriptRunner run) => _run = run;

    public const string SystemScript = """
$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$sysL = ($env:SystemDrive -replace ':', '')
$free = [long]-1; try { $free = [long](Get-Volume -DriveLetter $sysL -ErrorAction Stop).SizeRemaining } catch { }
$sd = -1; try { $sd = [int](Get-Partition -DriveLetter $sysL -ErrorAction Stop).DiskNumber } catch { }
$b = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
$bp = 0; $onb = $false
if ($b.Count -gt 0) { $bp = [int]$b[0].EstimatedChargeRemaining; $onb = ($b[0].BatteryStatus -eq 1) }
$blKnown = $false; $blOn = $false
try { $v = Get-BitLockerVolume -MountPoint ($env:SystemDrive) -ErrorAction Stop; $blKnown = $true; $blOn = ("$($v.ProtectionStatus)" -eq 'On' -or "$($v.ProtectionStatus)" -eq '1') } catch { }
$net = [System.Net.NetworkInformation.NetworkInterface]::GetIsNetworkAvailable()
[ordered]@{ build = [int]$os.BuildNumber; caption = "$($os.Caption)"; edition = "$($cv.EditionID)"; arch = "$($os.OSArchitecture)"; ui = "$((Get-UICulture).Name)"; free = $free; systemDisk = $sd; battery = ($b.Count -gt 0); onBattery = $onb; percent = $bp; blKnown = $blKnown; blOn = $blOn; net = $net } | ConvertTo-Json -Compress
""";

    public static string BackupScript(string stampFolder)
    {
        if (!BackupPath.IsValid(stampFolder, out var err)) throw new ArgumentException(err, nameof(stampFolder));
        var f = stampFolder.TrimEnd('\\');
        return $$"""
$ErrorActionPreference = 'Stop'
$root = '{{f}}'
$dl = '{{UserDataShared.DriveLetter(f)}}'
$sysL = ($env:SystemDrive -replace ':', '')
$bd = -1; $sd = -1
try { $bd = [int](Get-Partition -DriveLetter $dl -ErrorAction Stop).DiskNumber } catch { }
try { $sd = [int](Get-Partition -DriveLetter $sysL -ErrorAction Stop).DiskNumber } catch { }
function Has([string]$p) { $i = Get-Item -LiteralPath $p -ErrorAction SilentlyContinue; return [bool]($i -and ($i.PSIsContainer -or $i.Length -gt 0)) }
[ordered]@{
  exists = (Test-Path -LiteralPath $root -PathType Container); backupDisk = $bd; systemDisk = $sd
  userdata = (Test-Path -LiteralPath "$root\userdata" -PathType Container)
  manifest = (Has "$root\userdata\manifest.csv"); userdataJson = (Has "$root\userdata\userdata.json")
  plan = (Has "$root\apps\restore-plan.json"); kit = (Has "$root\PCTool.exe")
} | ConvertTo-Json -Compress
""";
    }

    public async Task<SystemFacts> ScanSystemAsync(CancellationToken ct)
    {
        var r = await _run(SystemScript, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được thông tin máy." : r.Error.Trim());
        return DirectFactsParser.ParseSystem(r.Output);
    }

    public async Task<BackupFacts> ScanBackupAsync(string stampFolder, CancellationToken ct)
    {
        var r = await _run(BackupScript(stampFolder), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được thư mục sao lưu." : r.Error.Trim());
        return DirectFactsParser.ParseBackup(r.Output);
    }
}

public sealed record DirectPlan(DirectMode Mode, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes, bool NeedsBitLockerAck)
{
    public bool CanRun => Blockers.Count == 0;
}

public static class DirectPlanner
{
    public static DirectPlan Evaluate(DirectMode mode, SystemFacts sys, IsoInfo? iso, BackupFacts? backup)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var notes = new List<string>();
        bool needIso = DirectModes.NeedsIso(mode);

        if (sys.OnBattery) blockers.Add("Laptop đang chạy bằng pin. Hãy cắm sạc trước khi cài lại Windows.");
        else if (sys.BatteryPresent && sys.BatteryPercent is > 0 and < 50) warnings.Add($"Pin còn {sys.BatteryPercent}%: nên để sạc đầy hơn.");

        if (sys.SystemFreeBytes < 0) warnings.Add("Không đọc được dung lượng trống ổ Windows.");
        else if (needIso && sys.SystemFreeBytes < DirectModes.MinFreeBytes)
            blockers.Add($"Ổ Windows chỉ còn {UsbReport.Size(sys.SystemFreeBytes)}; cần ít nhất {UsbReport.Size(DirectModes.MinFreeBytes)} (ngưỡng thận trọng của PCTool, bộ cài của Microsoft có thể đòi hơn).");
        else if (needIso && sys.SystemFreeBytes < DirectModes.WarnFreeBytes)
            warnings.Add($"Ổ Windows còn {UsbReport.Size(sys.SystemFreeBytes)}, hơi ít; nên dọn thêm trước.");
        else if (!needIso && sys.SystemFreeBytes >= 0 && sys.SystemFreeBytes < 10L * 1024 * 1024 * 1024)
            warnings.Add($"Ổ Windows còn {UsbReport.Size(sys.SystemFreeBytes)}: Đặt lại PC có thể báo thiếu chỗ.");

        if (sys.BitLockerKnown && sys.BitLockerOn)
            warnings.Add("Ổ Windows đang bật BitLocker. Hãy lưu KHÓA KHÔI PHỤC (tài khoản Microsoft hoặc file/giấy) trước khi cài lại; mất khóa là mất dữ liệu.");
        else if (!sys.BitLockerKnown)
            warnings.Add("Không đọc được tình trạng BitLocker (máy Home hoặc thiếu quyền). Nếu bạn từng bật mã hóa thiết bị, hãy kiểm tra khóa khôi phục.");

        if (needIso)
        {
            if (!string.Equals(sys.Arch, "64-bit", StringComparison.OrdinalIgnoreCase) && !sys.Arch.Contains("64"))
                blockers.Add($"Windows đang chạy là bản \"{sys.Arch}\"; chế độ này chỉ hỗ trợ bản 64-bit.");
            if (iso is null) blockers.Add("Chưa kiểm tra file ISO. Hãy chọn ISO và bấm Kiểm tra ISO.");
            else EvaluateIso(mode, sys, iso, blockers, warnings, notes);
        }
        else
        {
            if (!sys.NetworkUp) warnings.Add("Máy không có mạng: chọn \"Cloud download\" sẽ không chạy được, chỉ dùng \"Local reinstall\".");
            notes.Add("Đặt lại PC dùng công cụ có sẵn của Windows, không cần ISO. Tùy phiên bản Windows, một số máy bị giới hạn lựa chọn.");
        }

        EvaluateBackup(mode, backup, blockers, warnings);

        if (mode == DirectMode.CleanAll)
        {
            notes.Add($"Chế độ xóa sạch: bạn phải gõ đúng cụm \"{DirectModes.CleanPhrase}\" trước khi PCTool mở bộ cài.");
            notes.Add("Windows.old giữ bản cũ một thời gian rồi Windows tự xóa, KHÔNG coi đó là bản sao lưu.");
            notes.Add("Mọi app (kể cả Office) phải cài lại; PCTool không xử lý bản quyền hay tài khoản Microsoft.");
        }
        notes.Add("PCTool KHÔNG tự bấm trong bộ cài, KHÔNG tự khởi động lại máy, KHÔNG tự xóa Windows.old.");

        return new DirectPlan(mode, blockers, warnings, notes, sys.BitLockerKnown && sys.BitLockerOn);
    }

    private static void EvaluateIso(DirectMode mode, SystemFacts sys, IsoInfo iso, List<string> blockers, List<string> warnings, List<string> notes)
    {
        if (!iso.HasInstallWim && !iso.HasInstallEsd) blockers.Add("ISO không có install.wim hoặc install.esd: không phải ISO Windows hợp lệ.");
        if (iso.Images.Count == 0) { blockers.Add("ISO không đọc được danh sách phiên bản Windows."); return; }
        if (!iso.Images.Any(i => i.Arch == "x64")) blockers.Add("ISO không có bản 64-bit (x64).");

        bool keep = mode is DirectMode.RepairKeepAll or DirectMode.KeepFilesOnly;
        var x64 = iso.Images.Where(i => i.Arch == "x64").ToList();
        if (x64.Count == 0) return;

        if (x64.All(i => i.Build > 0 && i.Build < sys.Build))
        {
            var msg = $"ISO (build {x64.Max(i => i.Build)}) cũ hơn Windows đang chạy (build {sys.Build}).";
            if (keep) blockers.Add(msg + " Bộ cài sẽ không cho giữ app/file khi hạ phiên bản. Hãy dùng ISO mới hơn.");
            else warnings.Add(msg + " Nên dùng ISO mới hơn hoặc bằng.");
        }

        bool langOk = x64.Any(i => i.Languages.Any(l => string.Equals(l, sys.UiCulture, StringComparison.OrdinalIgnoreCase)));
        if (!langOk)
        {
            var msg = $"Ngôn ngữ ISO ({string.Join(", ", x64.SelectMany(i => i.Languages).Distinct())}) không có ngôn ngữ Windows đang dùng ({sys.UiCulture}).";
            if (keep) blockers.Add(msg + " Bộ cài sẽ không cho giữ app/file.");
            else warnings.Add(msg);
        }

        if (sys.EditionId.Length > 0 && !x64.Any(i => string.Equals(i.EditionId, sys.EditionId, StringComparison.OrdinalIgnoreCase)))
        {
            var msg = $"ISO không có phiên bản trùng với Windows đang chạy ({sys.EditionId}).";
            if (keep) blockers.Add(msg + " Bộ cài sẽ không cho giữ app/file.");
            else warnings.Add(msg);
        }

        notes.Add("Cho lần đầu, nên thử trong máy ảo trước.");
    }

    private static void EvaluateBackup(DirectMode mode, BackupFacts? b, List<string> blockers, List<string> warnings)
    {
        bool required = DirectModes.NeedsBackup(mode);
        void Problem(string text) { if (required) blockers.Add(text); else warnings.Add(text + " (Chế độ này không bắt buộc nhưng bạn nên có.)"); }

        if (b is null) { Problem("Chưa chọn và kiểm tra thư mục sao lưu (thư mục theo ngày giờ do tab Sao lưu tạo)."); return; }
        if (!b.Exists) { Problem("Không thấy thư mục sao lưu đã chọn."); return; }
        if (b.BackupDisk < 0 || b.SystemDisk < 0) Problem("Không xác định được thư mục sao lưu nằm trên ổ vật lý nào, nên không thể bảo đảm nó nằm ngoài ổ Windows.");
        else if (b.BackupDisk == b.SystemDisk) Problem("Thư mục sao lưu nằm CÙNG ổ vật lý với Windows: cài lại có thể làm mất luôn bản sao lưu. Hãy chép sang ổ/USB khác.");
        if (!b.HasUserData || !b.HasManifest || !b.HasUserDataJson) Problem("Bản sao lưu dữ liệu chưa đủ (thiếu thư mục userdata, manifest.csv hoặc userdata.json). Hãy chạy Sao lưu dữ liệu.");
        if (!b.HasAppPlan) Problem("Thiếu danh sách app (apps\\restore-plan.json). Hãy bấm Lưu danh sách app.");
        if (!b.HasKit) Problem("Chưa có bộ khôi phục (PCTool.exe) trong thư mục sao lưu. Hãy bấm Chép bộ khôi phục, để sau khi cài lại bạn có PCTool dùng ngay.");
    }

    public static string ToText(DirectPlan p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Chế độ: {DirectModes.Vietnamese(p.Mode)}");
        sb.AppendLine(p.CanRun ? "KẾT LUẬN: đủ điều kiện mở công cụ cài lại của Microsoft. (Chưa mở gì, chưa thay đổi gì.)" : "KẾT LUẬN: CHƯA ĐƯỢC, cần xử lý các điểm dưới đây. (Chưa thay đổi gì.)");
        foreach (var x in p.Blockers) sb.AppendLine("✖ " + x);
        foreach (var x in p.Warnings) sb.AppendLine("⚠ " + x);
        foreach (var x in p.Notes) sb.AppendLine("• " + x);
        sb.AppendLine();
        sb.AppendLine("Khi mở, bạn làm như sau ở màn hình của Microsoft:");
        foreach (var g in DirectModes.Guide(p.Mode)) sb.AppendLine("  - " + g);
        return sb.ToString().TrimEnd();
    }
}

public sealed record DirectAcks(bool BackupChecked, bool BitLockerKeySaved, string TypedPhrase);

/// <summary>
/// Mở công cụ của Microsoft có giao diện: C1-C3 gắn ISO (chỉ đọc) rồi mở setup.exe KHÔNG tham số; C4 mở trang Recovery của Cài đặt.
/// Không dùng /auto, không dùng systemreset.exe (Microsoft không công bố tham số). Không chờ, không bấm, không khởi động lại máy.
/// </summary>
public sealed class LaunchReinstallFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly Action<string> _openUri;
    private readonly string? _isoPath;
    private bool _launched;

    public LaunchReinstallFix(DirectPlan plan, DirectAcks acks, string? isoPath, ScriptRunner? run = null, Action<string>? openUri = null)
    {
        if (!plan.CanRun) throw new ArgumentException("Chưa đủ điều kiện: " + string.Join(" ", plan.Blockers), nameof(plan));
        if (DirectModes.NeedsBackup(plan.Mode) && !acks.BackupChecked) throw new ArgumentException("Bạn phải xác nhận đã kiểm tra bản sao lưu.", nameof(acks));
        if (plan.NeedsBitLockerAck && !acks.BitLockerKeySaved) throw new ArgumentException("Bạn phải xác nhận đã lưu khóa khôi phục BitLocker.", nameof(acks));
        if (plan.Mode == DirectMode.CleanAll && acks.TypedPhrase != DirectModes.CleanPhrase)
            throw new ArgumentException($"Chế độ xóa sạch cần gõ đúng cụm \"{DirectModes.CleanPhrase}\".", nameof(acks));
        if (DirectModes.NeedsIso(plan.Mode))
        {
            if (!IsoInspector.IsValidIsoPath(isoPath, out var err)) throw new ArgumentException(err, nameof(isoPath));
            _isoPath = isoPath;
        }
        Mode = plan.Mode;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 120));
        _openUri = openUri ?? (u => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }));
    }

    public DirectMode Mode { get; }
    public string Title => "Mở công cụ cài lại: " + DirectModes.Vietnamese(Mode);
    public IReadOnlyList<string> Actions => DirectModes.NeedsIso(Mode)
        ? new[]
        {
            $"Gắn file ISO {_isoPath} (chỉ đọc) thành ổ ảo",
            "Mở setup.exe của Microsoft có giao diện, KHÔNG dùng tham số tự động",
            "Bạn tự chọn và xác nhận ở màn hình của Microsoft; PCTool không bấm thay, không khởi động lại máy"
        }
        : new[]
        {
            "Mở trang Recovery trong Cài đặt của Windows",
            "Bạn tự bấm Reset PC và xác nhận ở màn hình của Microsoft; PCTool không bấm thay"
        };
    public bool RequiresRestorePoint => false;   // cài lại sẽ thay Windows; điểm khôi phục không có ý nghĩa
    public bool IsUndoable => false;

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$path = '{{_isoPath}}'
$img = Get-DiskImage -ImagePath $path
if (-not $img.Attached) { Mount-DiskImage -ImagePath $path -Access ReadOnly | Out-Null }
$l = (Get-DiskImage -ImagePath $path | Get-Volume | Where-Object { $_.DriveLetter } | Select-Object -First 1).DriveLetter
if (-not $l) { throw 'Không thấy ổ ảo của ISO sau khi gắn.' }
$setup = "${l}:\setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw 'Không thấy setup.exe trong ISO.' }
$p = Start-Process -FilePath $setup -WorkingDirectory "${l}:\" -PassThru
"OK|$l|$($p.Id)"
""";

    public const string RunningScript = "@(Get-Process -Name setup, SetupHost, SetupPrep, Windows10UpgraderApp -ErrorAction SilentlyContinue).Count";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!DirectModes.NeedsIso(Mode))
        {
            try { _openUri("ms-settings:recovery"); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { return FixResult.Failed("Không mở được Cài đặt: " + ex.Message + " Bạn có thể tự mở Settings > System > Recovery."); }
            _launched = true;
            return FixResult.Succeeded("Đã mở trang Recovery. Làm theo hướng dẫn ở màn hình của Microsoft. PCTool không bấm thay bạn.");
        }

        progress.Report("Đang gắn ISO và mở bộ cài Windows...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0) return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không mở được bộ cài." : r.Error.Trim());
        var last = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        var parts = last.Split('|');
        if (parts.Length != 3 || parts[0] != "OK" || parts[1].Length != 1 || !char.IsLetter(parts[1][0]))
            return FixResult.Failed("Không đọc được kết quả khi mở bộ cài.");
        _launched = true;
        return FixResult.Succeeded($"Đã mở bộ cài Windows từ ổ ảo {parts[1]}:. ISO vẫn đang gắn (bộ cài cần nó). Làm theo hướng dẫn ở màn hình của Microsoft.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (!_launched) return false;
        if (!DirectModes.NeedsIso(Mode)) return true;   // chỉ mở trang Cài đặt, không có tiến trình riêng để kiểm tra
        var r = await _run(RunningScript, ct);
        return int.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var n) && n > 0;
    }
}

/// <summary>Chép PCTool.exe vào thư mục sao lưu cùng file hướng dẫn, để sau khi cài lại có ngay công cụ khôi phục. Chỉ chép, không xóa.</summary>
public sealed class RestoreKitFix : IFix
{
    public const string ReadmeName = "HUONG-DAN-KHOI-PHUC.txt";
    private readonly string _exe;
    private readonly Func<string, bool> _exists;
    private readonly Action<string, string> _copy;
    private readonly Func<string, string> _hash;
    private readonly Action<string, string> _writeText;

    public RestoreKitFix(string stampFolder, string exePath,
        Func<string, bool>? exists = null, Action<string, string>? copy = null, Func<string, string>? hash = null, Action<string, string>? writeText = null)
    {
        if (!BackupPath.IsValid(stampFolder, out var err)) throw new ArgumentException(err, nameof(stampFolder));
        if (string.IsNullOrWhiteSpace(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Không xác định được file PCTool.exe đang chạy.", nameof(exePath));
        Folder = stampFolder.TrimEnd('\\');
        _exe = exePath;
        _exists = exists ?? File.Exists;
        _copy = copy ?? ((a, b) => File.Copy(a, b, overwrite: false));
        _hash = hash ?? (p => { using var s = File.OpenRead(p); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)); });
        _writeText = writeText ?? ((p, t) => File.WriteAllText(p, t, new UTF8Encoding(true)));
    }

    public string Folder { get; }
    public string KitPath => Folder + @"\PCTool.exe";
    public string ReadmePath => Folder + "\\" + ReadmeName;
    public string Title => "Chép bộ khôi phục (PCTool.exe) vào thư mục sao lưu";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Chép PCTool.exe đang chạy vào {Folder}",
        $"Tạo file {ReadmeName} hướng dẫn các bước sau khi cài lại",
        "Chỉ chép file mới, không ghi đè, không xóa gì"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public static string Readme => """
HƯỚNG DẪN SAU KHI CÀI LẠI WINDOWS (do PCTool tạo)

1. Cài xong Windows, cắm lại ổ/USB chứa thư mục này.
2. Mở PCTool.exe trong thư mục này (chuột phải > Run as administrator). Lần đầu Windows có thể cảnh báo SmartScreen.
3. Vào tab "Khôi phục sau cài Win" và làm từng bước:
   - Wi-Fi: chọn thư mục Wi-Fi đã sao lưu (để có mạng).
   - Driver: chọn thư mục driver đã sao lưu.
   - Ứng dụng: chọn apps\restore-plan.json, bấm Đọc danh sách rồi Cài các app. App không có trong winget thì cài tay.
   - Dữ liệu: chọn thư mục theo ngày giờ này, tick các thư mục cần lấy lại. File đã có trên máy không bị ghi đè.
4. Luôn để "Chạy thử" bật trước để xem sẽ làm gì.

Thư mục này chứa dữ liệu cá nhân và có thể cả mật khẩu Wi-Fi. Đừng gửi cho người khác.
""";

    public Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            if (!_exists(_exe)) return Task.FromResult(FixResult.Failed("Không thấy file PCTool.exe đang chạy."));
            progress.Report("Đang chép PCTool.exe...");
            if (_exists(KitPath))
            {
                if (_hash(KitPath) != _hash(_exe)) return Task.FromResult(FixResult.Failed("Đã có file PCTool.exe KHÁC trong thư mục sao lưu; PCTool không ghi đè. Hãy đổi tên/xóa file đó rồi thử lại."));
            }
            else _copy(_exe, KitPath);
            if (!_exists(ReadmePath)) _writeText(ReadmePath, Readme);
            return Task.FromResult(FixResult.Succeeded($"Đã chép bộ khôi phục vào {Folder}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(FixResult.Failed("Không chép được: " + ex.Message));
        }
    }

    public Task<bool> VerifyAsync(CancellationToken ct)
    {
        try { return Task.FromResult(_exists(KitPath) && _exists(ReadmePath) && _hash(KitPath) == _hash(_exe)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult(false); }
    }
}
