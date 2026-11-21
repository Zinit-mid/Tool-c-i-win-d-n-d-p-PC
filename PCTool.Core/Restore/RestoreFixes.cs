using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using PCTool.Core.AppInstaller;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Restore;

/// <summary>GĐ4: khôi phục sau khi cài Windows. Mỗi bước là một Fix riêng, lỗi bước này không làm hỏng bước khác.</summary>
public static class RestoreLimits
{
    public const int MaxWifiFiles = 200;
    public const long MaxWifiXmlBytes = 65_536;
    public const long MaxPlanBytes = 5_000_000;
    public const int MaxApps = 500;
    public const int PnpTimeoutSeconds = 30 * 60;
    public const int PnpRebootRequired = 3010;
}

/// <summary>Nạp lại driver đã sao lưu bằng pnputil (chỉ thêm driver; không gỡ driver nào).</summary>
public sealed class DriverRestoreFix : IFix
{
    private readonly IProcessRunner _runner;
    private readonly Func<string, int> _countInf;
    private bool _ok;

    public DriverRestoreFix(string folder, IProcessRunner? runner = null, Func<string, int>? countInf = null)
    {
        if (!BackupPath.IsValid(folder, out var err)) throw new ArgumentException(err, nameof(folder));
        Folder = folder.TrimEnd('\\');
        _runner = runner ?? new ProcessRunner();
        _countInf = countInf ?? DefaultCount;
    }

    public string Folder { get; }
    public string Title => "Cài lại driver từ bản sao lưu";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Nạp các driver (.inf) trong {Folder} và thư mục con vào Windows bằng pnputil",
        "Chỉ thêm driver, không gỡ hay đổi driver đang dùng",
        "Có thể cần khởi động lại máy sau khi xong"
    };
    public bool RequiresRestorePoint => true;
    public bool IsUndoable => false;

    public IReadOnlyList<string> Args() => new[] { "/add-driver", Folder + @"\*.inf", "/subdirs", "/install" };

    private static int DefaultCount(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.inf", SearchOption.AllDirectories).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
    }

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var n = _countInf(Folder);
        if (n < 0) return FixResult.Failed("Không đọc được thư mục driver: " + Folder);
        if (n == 0) return FixResult.Failed("Không thấy file driver (.inf) nào trong " + Folder);

        progress.Report($"Đang nạp {n} file driver (có thể mất vài phút)...");
        ProcessResult r;
        try { r = await _runner.RunAsync("pnputil.exe", Args(), RestoreLimits.PnpTimeoutSeconds, ct); }
        catch (Win32Exception) { return FixResult.Failed("Không chạy được pnputil trên máy này."); }

        if (r.ExitCode == 0 || r.ExitCode == RestoreLimits.PnpRebootRequired)
        {
            _ok = true;
            return FixResult.Succeeded($"pnputil đã nạp driver ({n} file .inf)." +
                (r.ExitCode == RestoreLimits.PnpRebootRequired ? " Cần KHỞI ĐỘNG LẠI máy." : "") +
                " Hãy mở Device Manager xem còn thiết bị dấu chấm than không.");
        }
        var tail = (string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error).Trim();
        return FixResult.Failed($"pnputil báo lỗi (mã {r.ExitCode}). " + (tail.Length <= 300 ? tail : tail[^300..]));
    }

    // Chưa kiểm tra được từng driver đã vào máy; chỉ xác nhận pnputil báo thành công.
    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_ok);
}

public static class WifiProfileName
{
    /// <summary>Tên mạng trong file xuất của netsh (WLANProfile/name). null nếu không phải file hợp lệ.</summary>
    public static string? Parse(string xml)
    {
        try
        {
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var doc = System.Xml.Linq.XDocument.Load(xr);
            if (doc.Root?.Name.LocalName != "WLANProfile") return null;
            var name = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (XmlException) { return null; }
    }

    public static readonly Regex SafeFileName = new(@"^[\p{L}\p{N} _.()-]{1,150}\.xml$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
}

/// <summary>Nạp lại các mạng Wi-Fi đã xuất (netsh wlan add profile). File chứa mật khẩu nên không ghi nội dung vào nhật ký.</summary>
public sealed class WifiRestoreFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly Func<string, IReadOnlyList<string>> _list;
    private readonly Func<string, string?> _read;
    private readonly List<string> _added = new();

    public WifiRestoreFix(string folder, ScriptRunner? run = null, Func<string, IReadOnlyList<string>>? list = null, Func<string, string?>? read = null)
    {
        if (!BackupPath.IsValid(folder, out var err)) throw new ArgumentException(err, nameof(folder));
        Folder = folder.TrimEnd('\\');
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 60));
        _list = list ?? (d => Directory.EnumerateFiles(d, "*.xml", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
        _read = read ?? DefaultRead;
    }

    public string Folder { get; }
    public string Title => "Nạp lại Wi-Fi đã sao lưu";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Thêm các mạng Wi-Fi từ file .xml trong {Folder} vào máy (cho mọi tài khoản)",
        "Không xóa hay sửa mạng Wi-Fi nào đang có",
        "Các file này chứa mật khẩu Wi-Fi dạng chữ thường"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    private static string? DefaultRead(string path)
    {
        try
        {
            if (new FileInfo(path).Length > RestoreLimits.MaxWifiXmlBytes) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string BuildAddScript(string folder, string file) => $$"""
$ErrorActionPreference = 'Stop'
netsh wlan add profile filename="{{folder}}\{{file}}" user=all | Out-Null
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine('netsh thất bại'); exit 1 }
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        _added.Clear();
        var files = _list(Folder);
        if (files.Count == 0) return FixResult.Failed("Không thấy file .xml nào trong " + Folder);
        if (files.Count > RestoreLimits.MaxWifiFiles) return FixResult.Failed($"Quá nhiều file ({files.Count}), tối đa {RestoreLimits.MaxWifiFiles}.");

        var failed = new List<string>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!WifiProfileName.SafeFileName.IsMatch(f)) { failed.Add($"{f} (tên file có ký tự lạ, bỏ qua)"); continue; }
            var xml = _read(Folder + "\\" + f);
            var name = xml == null ? null : WifiProfileName.Parse(xml);
            if (name == null) { failed.Add($"{f} (không phải file Wi-Fi hợp lệ)"); continue; }
            progress.Report($"Đang nạp Wi-Fi: {name}");
            var r = await _run(BuildAddScript(Folder, f), ct);
            if (r.ExitCode == 0) _added.Add(name); else failed.Add($"{name} (netsh lỗi)");
        }

        if (_added.Count == 0) return FixResult.Failed("Không nạp được mạng Wi-Fi nào: " + string.Join("; ", failed));
        if (failed.Count > 0) return FixResult.Failed($"Nạp được {_added.Count}/{files.Count} mạng. Lỗi: " + string.Join("; ", failed));
        return FixResult.Succeeded($"Đã nạp {_added.Count} mạng Wi-Fi. Mật khẩu lấy từ file nên máy tự kết nối khi mạng trong tầm.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (_added.Count == 0) return false;
        var r = await _run("netsh wlan show profiles", ct);
        if (r.ExitCode != 0) return false;
        return _added.All(n => r.Output.Contains(n, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record RestorePlan(IReadOnlyList<string> WingetIds, int InstalledCount, string ComputerName);

public static class RestorePlanReader
{
    private static readonly Regex IdRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

    public static RestorePlan Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("restore-plan.json không đúng định dạng.");
            if (root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.GetInt32() > 1)
                throw new FormatException("restore-plan.json do bản PCTool mới hơn tạo ra; hãy cập nhật PCTool.");

            var ids = new List<string>();
            if (root.TryGetProperty("wingetPackages", out var wp) && wp.ValueKind == JsonValueKind.Array)
                foreach (var e in wp.EnumerateArray())
                {
                    var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (id != null && IdRx.IsMatch(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
                }
            if (ids.Count > RestoreLimits.MaxApps) throw new FormatException($"Danh sách quá dài (hơn {RestoreLimits.MaxApps} app).");

            var installed = root.TryGetProperty("installedApps", out var ia) && ia.ValueKind == JsonValueKind.Array ? ia.GetArrayLength() : 0;
            var pc = root.TryGetProperty("computerName", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            return new RestorePlan(ids, installed, pc);
        }
        catch (JsonException) { throw new FormatException("restore-plan.json không đọc được (file hỏng)."); }
    }

    public static RestorePlan Load(string path)
    {
        if (!BackupPath.IsValid(path, out var err)) throw new ArgumentException(err, nameof(path));
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("Không thấy file: " + path);
        if (fi.Length > RestoreLimits.MaxPlanBytes) throw new FormatException("File quá lớn.");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Mỗi gói winget thành một mục cài nhanh (ID đã kiểm tra), để chạy qua InstallAppFix như cài thường.</summary>
    public static IReadOnlyList<AppEntry> ToEntries(RestorePlan plan) =>
        plan.WingetIds.Select((id, i) => new AppEntry
        {
            Id = $"restore-{i + 1}",
            Name = id.Length <= 80 ? id : id[..80],
            Kind = AppKind.Winget,
            WingetId = id
        }).ToList();
}

public static class RestoreVerifier
{
    public static IReadOnlyList<string> Problems(string json)
    {
        var problems = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json.Trim().Split('\n').Last().Trim());
            if (!doc.RootElement.TryGetProperty("folders", out var f)) return new[] { "Kết quả thiếu danh sách thư mục." };
            var items = f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToList() : new List<JsonElement> { f };
            if (items.Count == 0) return new[] { "Không có thư mục nào trong bản sao lưu để khôi phục." };
            foreach (var e in items)
            {
                var name = e.TryGetProperty("name", out var n) ? n.GetString() : "?";
                long Get(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;
                if (Get("exit") is < 0 or >= 8) problems.Add($"{name}: robocopy báo lỗi (mã {Get("exit")}).");
                else if (Get("destFiles") < Get("srcFiles")) problems.Add($"{name}: máy chỉ có {Get("destFiles")} file, bản sao lưu có {Get("srcFiles")}.");
            }
        }
        catch (JsonException) { return new[] { "Không đọc được kết quả khôi phục." }; }
        return problems;
    }
}

/// <summary>
/// Chép dữ liệu từ bản sao lưu về thư mục người dùng. KHÔNG BAO GIỜ ghi đè: chỉ chép file chưa có trên máy
/// (robocopy /XC /XN /XO), không xóa, không dùng /MIR /PURGE. File đã có trên máy được giữ nguyên.
/// </summary>
public sealed class UserDataRestoreFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly IReadOnlyList<UserFolder> _folders;
    private bool _ok;

    public UserDataRestoreFix(string userdataDir, IEnumerable<UserFolder> folders, ScriptRunner? run = null)
    {
        if (!BackupPath.IsValid(userdataDir, out var err)) throw new ArgumentException(err, nameof(userdataDir));
        _folders = folders.Distinct().ToList();
        if (_folders.Count == 0) throw new ArgumentException("Chưa chọn thư mục nào.", nameof(folders));
        Source = userdataDir.TrimEnd('\\');
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 7200));
    }

    public string Source { get; }
    public string Title => "Khôi phục dữ liệu cá nhân";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Chép từ {Source} về các thư mục: {string.Join(", ", _folders.Select(UserFolders.Vietnamese))} của tài khoản đang dùng",
        "Chỉ chép file CHƯA CÓ trên máy; file đã có được giữ nguyên, không ghi đè, không xóa gì",
        "Có thể mất lâu nếu dữ liệu lớn"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public string BuildScript() => UserDataShared.Functions + $$"""

$ErrorActionPreference = 'Stop'
{{UserDataShared.RegistryMapScript()}}
$src = '{{Source}}'
$res = @()
foreach ($n in {{UserDataShared.NamesArray(_folders)}}) {
  $s = Join-Path $src $n
  if (-not (Test-Path -LiteralPath $s -PathType Container)) { continue }
  $d = Get-UserFolder $map[$n]
  if (-not $d) { throw "Không xác định được thư mục $n trên máy này." }
  New-Item -ItemType Directory -Force -Path $d | Out-Null
  $pre = Scan-Tree $s
  & robocopy $s $d /E /XC /XN /XO /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NP /NFL /NDL /NJH /NJS /MT:8 | Out-Null
  $code = $LASTEXITCODE
  $post = Scan-Tree $d
  $res += [ordered]@{ name = $n; srcFiles = [long]$pre.Files; destFiles = [long]($post.Files + $post.CloudFiles); exit = [int]$code }
}
[ordered]@{ folders = @($res) } | ConvertTo-Json -Compress -Depth 4
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang chép dữ liệu về máy (không ghi đè file đã có)...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không khôi phục được dữ liệu." : r.Error.Trim());
        var problems = RestoreVerifier.Problems(r.Output);
        if (problems.Count > 0)
            return FixResult.Failed("Khôi phục chưa đạt: " + string.Join(" ", problems) + " Bản sao lưu vẫn còn nguyên.");
        _ok = true;
        return FixResult.Succeeded("Đã chép dữ liệu về máy. File đã có sẵn trên máy được giữ nguyên.");
    }

    // Việc đếm lại số file đã nằm trong script (sau khi chép); không chạy thêm lần nữa.
    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_ok);
}
