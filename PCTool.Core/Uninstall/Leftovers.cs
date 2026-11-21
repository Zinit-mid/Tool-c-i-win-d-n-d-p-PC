using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Uninstall;

/// <summary>Kiểm tra thư mục cài đặt trước khi đưa vào script PowerShell (đặt trong dấu nháy đơn): chỉ ký tự an toàn, không phải thư mục gốc/hệ thống.</summary>
public static class InstallPathGuard
{
    private static readonly Regex Safe = new(@"^[A-Za-z]:\\(?:[\p{L}\p{N} _.()\-&,]+(?:\\[\p{L}\p{N} _.()\-&,]+)*\\?)?$", RegexOptions.Compiled);
    private static readonly string[] BlockedExact = { "program files", "program files (x86)", "programdata", "users", "windows" };

    public static bool Check(string? path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "Không biết thư mục cài đặt của chương trình này."; return false; }
        if (path.Length > 200 || !Safe.IsMatch(path))
        { error = "Thư mục cài đặt có ký tự lạ hoặc quá dài, không xử lý tự động được."; return false; }
        var segs = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) { error = "Thư mục cài đặt là cả ổ đĩa, không xử lý."; return false; }
        if (segs.Any(s => s is "." or ".." || s.EndsWith('.') || s.EndsWith(' '))) { error = "Thư mục cài đặt không hợp lệ."; return false; }
        if (segs[0].Equals("windows", StringComparison.OrdinalIgnoreCase)) { error = "Không xử lý thư mục Windows."; return false; }
        if (segs.Length == 1 && BlockedExact.Contains(segs[0].ToLowerInvariant())) { error = "Thư mục cài đặt là thư mục chung của hệ thống, không xử lý."; return false; }
        return true;
    }
}

public sealed record AppActivity(IReadOnlyList<string> Processes, IReadOnlyList<string> Services)
{
    public bool Any => Processes.Count + Services.Count > 0;
}

/// <summary>Tìm tiến trình và dịch vụ đang chạy từ thư mục cài đặt của chương trình (CHỈ ĐỌC).</summary>
public static class AppActivityScanner
{
    private static string Header(string location) => $$"""
$ErrorActionPreference = 'SilentlyContinue'
$loc = '{{location.TrimEnd('\\')}}\'
""";

    public static string ScanScript(string location) => Header(location) + """

Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($loc, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { "P|$($_.ProcessName)" }
Get-CimInstance Win32_Service | Where-Object { $_.State -eq 'Running' -and $_.PathName -and $_.PathName.Replace('"','').StartsWith($loc, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { "S|$($_.Name)" }
""";

    /// <summary>Dừng dịch vụ rồi tiến trình đang chạy từ đúng thư mục này. Không xóa file, không đổi kiểu khởi động.</summary>
    public static string StopScript(string location) => Header(location) + """

$svc = @(Get-CimInstance Win32_Service | Where-Object { $_.State -eq 'Running' -and $_.PathName -and $_.PathName.Replace('"','').StartsWith($loc, [System.StringComparison]::OrdinalIgnoreCase) })
foreach ($s in $svc) { Stop-Service -Name $s.Name -Force -ErrorAction SilentlyContinue }
$proc = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($loc, [System.StringComparison]::OrdinalIgnoreCase) })
foreach ($p in $proc) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
'OK'
""";

    public static async Task<AppActivity> ScanAsync(string location, ScriptRunner? run, CancellationToken ct)
    {
        if (!InstallPathGuard.Check(location, out var err)) throw new ArgumentException(err, nameof(location));
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c);
        var r = await run(ScanScript(location), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("Không kiểm tra được tiến trình đang chạy: " + r.Error.Trim());
        return Parse(r.Output);
    }

    public static AppActivity Parse(string output)
    {
        var procs = new List<string>(); var svcs = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 3 || line[1] != '|') continue;
            var name = line[2..].Trim();
            if (name.Length == 0 || name.Length > 100 || name.Any(c => char.IsControl(c))) continue;
            if (line[0] == 'P' && !procs.Contains(name)) procs.Add(name);
            else if (line[0] == 'S' && !svcs.Contains(name)) svcs.Add(name);
        }
        return new AppActivity(procs, svcs);
    }
}

/// <summary>Dừng các tiến trình/dịch vụ đang chạy từ thư mục cài đặt của chương trình, để trình gỡ không bị "đang được sử dụng".</summary>
public sealed class StopAppFix : IFix
{
    private readonly InstalledProgram _p;
    private readonly AppActivity _seen;
    private readonly ScriptRunner _run;

    public StopAppFix(InstalledProgram program, AppActivity activity, ScriptRunner? run = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(activity);
        if (!InstallPathGuard.Check(program.InstallLocation, out var err)) throw new ArgumentException(err, nameof(program));
        if (!activity.Any) throw new ArgumentException("Không có tiến trình hay dịch vụ nào đang chạy để dừng.", nameof(activity));
        _p = program; _seen = activity;
        _run = run ?? ((s, c) => PowerShellRunner.RunAsync(s, c));
    }

    public string Title => $"Dừng {_p.Name} đang chạy";

    public IReadOnlyList<string> Actions
    {
        get
        {
            var list = new List<string>();
            if (_seen.Services.Count > 0) list.Add("Dừng dịch vụ: " + string.Join(", ", _seen.Services));
            if (_seen.Processes.Count > 0) list.Add("Kết thúc tiến trình: " + string.Join(", ", _seen.Processes));
            list.Add($"Chỉ nhắm các chương trình chạy từ thư mục {_p.InstallLocation}");
            list.Add("Dữ liệu chưa lưu trong chương trình đó có thể bị mất; không xóa file, không đổi kiểu khởi động");
            return list;
        }
    }

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang dừng dịch vụ và tiến trình...");
        var r = await _run(AppActivityScanner.StopScript(_p.InstallLocation), ct);
        if (r.ExitCode != 0) return FixResult.Failed("Không dừng được: " + r.Error.Trim());
        return FixResult.Succeeded("Đã gửi lệnh dừng; đang kiểm tra lại.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        !(await AppActivityScanner.ScanAsync(_p.InstallLocation, _run, ct)).Any;
}

public sealed record LeftoverFolder(string Path, long Bytes, int Files);

public static class LeftoverRoots
{
    public static IReadOnlyList<string> Default() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    }.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string DefaultQuarantine() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCTool", "leftovers");

    /// <summary>true nếu path nằm BÊN TRONG một trong các thư mục gốc (không phải chính thư mục gốc).</summary>
    public static bool InsideAny(IReadOnlyList<string> roots, string path)
    {
        var p = path.TrimEnd('\\');
        return roots.Any(r => p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Tìm thư mục còn sót sau khi gỡ (CHỈ ĐỌC): thư mục cài đặt ghi trong Registry và thư mục trùng đúng tên chương trình trong các nơi thường gặp.</summary>
public static class LeftoverScanner
{
    private static readonly string[] Generic =
    {
        "microsoft", "windows", "common files", "temp", "packages", "package cache", "programs", "system", "data", "cache", "logs", "apps", "app", "setup", "update", "updates"
    };

    public static IReadOnlyList<LeftoverFolder> Scan(InstalledProgram program, IReadOnlyList<InstalledProgram> installedNow,
        IReadOnlyList<string>? roots = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        roots ??= LeftoverRoots.Default();
        if (installedNow.Any(p => p.SameEntry(program)))
            throw new InvalidOperationException("Chương trình vẫn còn trong danh sách đã cài. Hãy gỡ xong rồi mới quét tàn dư.");

        var candidates = new List<string>();
        if (program.InstallLocation.Length > 0) candidates.Add(program.InstallLocation.TrimEnd('\\'));
        var name = Sanitize(program.Name);
        if (name.Length >= 4 && !Generic.Contains(name.ToLowerInvariant()))
            foreach (var r in roots) candidates.Add(System.IO.Path.Combine(r, name));

        var result = new List<LeftoverFolder>();
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!LeftoverRoots.InsideAny(roots, c)) continue;
            // Thư mục này đang được chương trình khác dùng chung (cùng InstallLocation hoặc nằm trong đó): bỏ qua.
            if (installedNow.Any(p => p.InstallLocation.Length > 0 &&
                (p.InstallLocation.TrimEnd('\\').Equals(c, StringComparison.OrdinalIgnoreCase) ||
                 p.InstallLocation.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)))) continue;
            var di = new DirectoryInfo(c);
            if (!di.Exists || (di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            result.Add(Measure(di, ct));
        }
        return result;
    }

    private static string Sanitize(string name) =>
        new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.' or '(' or ')').ToArray()).Trim().TrimEnd('.');

    private static LeftoverFolder Measure(DirectoryInfo di, CancellationToken ct)
    {
        long bytes = 0; int files = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(di);
        while (stack.Count > 0 && files < 200_000)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            try
            {
                foreach (var f in d.EnumerateFiles()) { bytes += f.Length; files++; }
                foreach (var s in d.EnumerateDirectories()) if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return new LeftoverFolder(di.FullName, bytes, files);
    }
}

/// <summary>
/// Chuyển thư mục tàn dư vào %ProgramData%\PCTool\leftovers\&lt;ngày giờ&gt; (cùng ổ, chỉ đổi chỗ, KHÔNG xóa, KHÔNG đụng Registry).
/// Muốn lấy lại thì chuyển về theo vi-tri-cu.csv.
/// </summary>
public sealed class LeftoverMoveFix : IFix
{
    private readonly IReadOnlyList<LeftoverFolder> _folders;
    private readonly IReadOnlyList<string> _roots;
    private readonly string _dest;
    private readonly List<string> _movedFrom = new();
    private readonly List<string> _movedTo = new();
    private readonly List<string> _failed = new();

    public LeftoverMoveFix(string programName, IReadOnlyList<LeftoverFolder> folders, DateTime? now = null,
        IReadOnlyList<string>? allowedRoots = null, string? quarantineRoot = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (folders.Count == 0) throw new ArgumentException("Không có thư mục tàn dư nào.", nameof(folders));
        _roots = allowedRoots ?? LeftoverRoots.Default();
        foreach (var f in folders)
            if (!LeftoverRoots.InsideAny(_roots, f.Path)) throw new ArgumentException("Có thư mục nằm ngoài các nơi được phép dọn: " + f.Path, nameof(folders));
        ProgramName = programName;
        _folders = folders;
        _dest = System.IO.Path.Combine(quarantineRoot ?? LeftoverRoots.DefaultQuarantine(),
            (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    }

    public string ProgramName { get; }
    public string QuarantineFolder => _dest;
    public string Title => $"Chuyển tàn dư của {ProgramName}";

    public IReadOnlyList<string> Actions => _folders
        .Select(f => $"Chuyển {f.Path} ({f.Files} file, {PCTool.Core.Folders.SizeText.Of(f.Bytes)}) sang {_dest}")
        .Concat(new[]
        {
            "Chỉ đổi chỗ trên cùng ổ đĩa, KHÔNG xóa file nào, KHÔNG đụng Registry; ghi vi-tri-cu.csv để chuyển về nếu cần",
            "Thư mục đang bị chương trình khác khóa sẽ báo lỗi và được bỏ qua"
        }).ToList();

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct) => Task.Run(() =>
    {
        Directory.CreateDirectory(_dest);
        var csv = new StringBuilder("vi tri cu,vi tri moi\r\n");
        int n = 0;
        foreach (var f in _folders)
        {
            ct.ThrowIfCancellationRequested();
            var src = f.Path.TrimEnd('\\');
            if (!Directory.Exists(src)) continue;                       // đã mất: coi như đã sạch
            if ((File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0) { _failed.Add(src + " (là liên kết)"); continue; }
            if (!string.Equals(System.IO.Path.GetPathRoot(src), System.IO.Path.GetPathRoot(_dest), StringComparison.OrdinalIgnoreCase))
            { _failed.Add(src + " (khác ổ đĩa với nơi chứa)"); continue; }
            var dst = System.IO.Path.Combine(_dest, $"{++n:00}_{System.IO.Path.GetFileName(src)}");
            try
            {
                Directory.Move(src, dst);
                _movedFrom.Add(src); _movedTo.Add(dst);
                csv.Append('"').Append(src).Append("\",\"").Append(dst).Append("\"\r\n");
                progress.Report($"Đã chuyển {_movedTo.Count}/{_folders.Count} thư mục...");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failed.Add($"{src} ({ex.Message})");
            }
        }
        File.WriteAllText(System.IO.Path.Combine(_dest, "vi-tri-cu.csv"), csv.ToString(), new UTF8Encoding(true));
        if (_movedTo.Count == 0 && _failed.Count > 0)
            return FixResult.Failed("Không chuyển được thư mục nào: " + string.Join("; ", _failed));
        return FixResult.Succeeded($"Đã chuyển {_movedTo.Count} thư mục sang {_dest}" +
            (_failed.Count > 0 ? $"; chưa chuyển được {_failed.Count}: {string.Join("; ", _failed)}" : "") + ".");
    }, ct);

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.Run(() =>
        _failed.Count == 0
        && _movedTo.All(Directory.Exists)
        && _movedFrom.All(p => !Directory.Exists(p))
        && _folders.All(f => !Directory.Exists(f.Path.TrimEnd('\\'))), ct);
}
