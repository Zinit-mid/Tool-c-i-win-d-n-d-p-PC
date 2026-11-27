using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Uninstall;

public sealed record InstalledProgram(
    string Hive, bool Wow64, string Key, string Name, string Version, string Publisher,
    string UninstallString, string QuietUninstallString, string InstallLocation,
    long SizeKb, bool IsSystemComponent, string ParentKey, bool IsMsi, string DisplayIcon = "", string InstallDate = "")
{
    public bool SameEntry(InstalledProgram o) => Hive == o.Hive && Wow64 == o.Wow64 && Key == o.Key;
    public string SizeText => SizeKb <= 0 ? "" : PCTool.Core.Folders.SizeText.Of(SizeKb * 1024);
}

/// <summary>Đọc danh sách chương trình đã cài từ Registry (CHỈ ĐỌC).</summary>
public static class InstalledProgramsScanner
{
    public const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$paths = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
$out = @()
foreach ($p in $paths) {
  Get-ChildItem $p | ForEach-Object {
    $v = Get-ItemProperty $_.PSPath
    if ($v.DisplayName) {
      $out += [pscustomobject]@{
        hive = $p.Substring(0, 4); wow = [bool]($p -like '*WOW6432Node*'); key = $_.PSChildName
        name = [string]$v.DisplayName; version = [string]$v.DisplayVersion; publisher = [string]$v.Publisher
        uninstall = [string]$v.UninstallString; quiet = [string]$v.QuietUninstallString; location = [string]$v.InstallLocation
        sizeKb = [long]$v.EstimatedSize; system = [bool]($v.SystemComponent -eq 1); parent = [string]$v.ParentKeyName
        msi = [bool]($v.WindowsInstaller -eq 1); icon = [string]$v.DisplayIcon; date = [string]$v.InstallDate
      }
    }
  }
}
@($out) | ConvertTo-Json -Compress -Depth 3
""";

    public static async Task<IReadOnlyList<InstalledProgram>> RunAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c);
        var r = await run(Script, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("Không đọc được danh sách chương trình: " + r.Error.Trim());
        return Parse(r.Output);
    }

    public static IReadOnlyList<InstalledProgram> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<InstalledProgram>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidOperationException("Không đọc được danh sách chương trình (kết quả lạ)."); }
        using (doc)
        {
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
                      : doc.RootElement.ValueKind == JsonValueKind.Object ? new List<JsonElement> { doc.RootElement }
                      : new List<JsonElement>();
            var list = new List<InstalledProgram>();
            foreach (var e in items)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var name = Str(e, "name").Trim();
                var key = Str(e, "key");
                if (name.Length == 0 || key.Length == 0) continue;
                list.Add(new InstalledProgram(Str(e, "hive"), Bool(e, "wow"), key, name, Str(e, "version"), Str(e, "publisher"),
                    Str(e, "uninstall").Trim(), Str(e, "quiet").Trim(), Str(e, "location").Trim().Trim('"'),
                    e.TryGetProperty("sizeKb", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var l) ? l : 0,
                    Bool(e, "system"), Str(e, "parent"), Bool(e, "msi"), Str(e, "icon").Trim(), Str(e, "date").Trim()));
            }
            return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}

public sealed record UninstallVerdict(bool Allowed, string Reason, string? Warning);

/// <summary>Quyết định chương trình nào KHÔNG cho gỡ từ PCTool (thành phần hệ thống, bản cập nhật, của Microsoft...) và khi nào cần cảnh báo.</summary>
public static class UninstallPolicy
{
    private static readonly Regex Updates = new(@"^(Update for|Security Update|Hotfix)|\bKB\d{6,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MicrosoftKeep = new(@"Office|Teams|Skype|OneDrive", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Drivers = new(@"driver|graphics|audio|chipset|firmware|bluetooth|wi-?fi|wireless", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Security = new(@"antivirus|anti-virus|defender|norton|kaspersky|bitdefender|avast|avg\b|eset|mcafee|\b360\b|malwarebytes|security", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static UninstallVerdict Evaluate(InstalledProgram p)
    {
        if (p.IsSystemComponent) return new(false, "Thành phần hệ thống, không cho gỡ.", null);
        if (p.ParentKey.Length > 0 || Updates.IsMatch(p.Name)) return new(false, "Đây là bản cập nhật/gói con của chương trình khác, không cho gỡ riêng.", null);
        if (p.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && !MicrosoftKeep.IsMatch(p.Name))
            return new(false, "Thành phần của Microsoft/Windows (VC++, .NET, WebView2, Edge...), gỡ có thể làm hỏng chương trình khác. Hãy gỡ bằng Settings của Windows nếu thật sự cần.", null);
        if (p.UninstallString.Length == 0) return new(false, "Chương trình không có trình gỡ riêng.", null);

        string? warn = null;
        if (Drivers.IsMatch(p.Name) || Drivers.IsMatch(p.Publisher))
            warn = "Có vẻ là driver/phần mềm thiết bị: gỡ có thể làm mất hình, âm thanh, Wi-Fi hoặc Bluetooth. Nên có điểm khôi phục.";
        else if (Security.IsMatch(p.Name))
            warn = "Phần mềm bảo mật thường tự bảo vệ và cần công cụ gỡ riêng của hãng; trình gỡ thường có thể không gỡ sạch.";
        return new(true, "", warn);
    }
}

public sealed record UninstallCommand(string Exe, IReadOnlyList<string> Args)
{
    private static readonly Regex Msi = new(@"^""?(?:[A-Za-z]:\\Windows\\System32\\)?msiexec(?:\.exe)?""?\s+/[IiXx]\s*(\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] BlockedExe =
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe",
        "regsvr32.exe", "msbuild.exe", "bash.exe", "wmic.exe", "cmstp.exe", "forfiles.exe", "wsl.exe"
    };

    /// <summary>
    /// Đọc UninstallString thành (chương trình + tham số riêng từng cái). Không qua shell. Chỉ nhận file .exe có thật;
    /// msiexec chỉ nhận dạng /I hoặc /X kèm mã {GUID} (đổi thành /x, chạy có giao diện); cấm cmd/powershell/rundll32/...
    /// </summary>
    public static UninstallCommand Parse(string? uninstallString, string windowsDir, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var s = (uninstallString ?? "").Trim();
        if (s.Length == 0) throw new ArgumentException("Không có lệnh gỡ.");
        if (s.Length > 600) throw new ArgumentException("Lệnh gỡ quá dài, không dùng.");
        if (s.Any(c => c < 0x20)) throw new ArgumentException("Lệnh gỡ có ký tự lạ.");

        var m = Msi.Match(s);
        if (m.Success)
            return new UninstallCommand(System.IO.Path.Combine(windowsDir, "System32", "msiexec.exe"), new[] { "/x", m.Groups[1].Value });

        string exe, rest;
        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            if (close < 2) throw new ArgumentException("Lệnh gỡ có dấu nháy sai.");
            exe = s[1..close]; rest = s[(close + 1)..];
        }
        else
        {
            int i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i < 0) throw new ArgumentException("Lệnh gỡ không phải file .exe.");
            exe = s[..(i + 4)]; rest = s[(i + 4)..];
        }

        if (!System.IO.Path.IsPathFullyQualified(exe) || exe.StartsWith(@"\\", StringComparison.Ordinal) || exe.Contains(".."))
            throw new ArgumentException("Đường dẫn trình gỡ không hợp lệ.");
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Trình gỡ không phải file .exe.");
        var fileName = System.IO.Path.GetFileName(exe);
        if (BlockedExe.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Không chạy {fileName} từ lệnh gỡ (có thể chạy lệnh tùy ý).");
        var winPrefix = windowsDir.TrimEnd('\\') + "\\";
        if (exe.StartsWith(winPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Không chạy chương trình trong thư mục Windows từ lệnh gỡ.");
        if (!fileExists(exe)) throw new ArgumentException("Không thấy file trình gỡ: " + exe);

        return new UninstallCommand(exe, Tokenize(rest));
    }

    internal static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool inQ = false, has = false;
        foreach (var c in s)
        {
            if (c == '"') { inQ = !inQ; has = true; }
            else if (char.IsWhiteSpace(c) && !inQ) { if (has) { list.Add(cur.ToString()); cur.Clear(); has = false; } }
            else { cur.Append(c); has = true; }
        }
        if (inQ) throw new ArgumentException("Tham số của lệnh gỡ có dấu nháy chưa đóng.");
        if (has) list.Add(cur.ToString());
        if (list.Count > 20) throw new ArgumentException("Lệnh gỡ có quá nhiều tham số.");
        return list;
    }
}

/// <summary>
/// Gỡ chương trình bằng CHÍNH TRÌNH GỠ của nó (có giao diện, bạn xác nhận trong cửa sổ của trình gỡ). Không dùng chế độ gỡ im lặng.
/// Sau đó đọc lại Registry để xem mục gỡ đã biến mất chưa.
/// </summary>
public sealed class UninstallFix : IFix
{
    private readonly InstalledProgram _p;
    private readonly UninstallCommand _cmd;
    private readonly IProcessRunner _proc;
    private readonly ScriptRunner? _run;
    private readonly TimeSpan _poll;
    private readonly int _polls;

    public UninstallFix(InstalledProgram program, string? windowsDir = null, IProcessRunner? proc = null, ScriptRunner? run = null,
        Func<string, bool>? fileExists = null, TimeSpan? pollEvery = null, int polls = 15)
    {
        ArgumentNullException.ThrowIfNull(program);
        var v = UninstallPolicy.Evaluate(program);
        if (!v.Allowed) throw new ArgumentException(v.Reason, nameof(program));
        _cmd = UninstallCommand.Parse(program.UninstallString, windowsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), fileExists);
        _p = program;
        _proc = proc ?? new ProcessRunner();
        _run = run;
        _poll = pollEvery ?? TimeSpan.FromSeconds(2);
        _polls = polls;
    }

    public UninstallCommand Command => _cmd;
    public string Title => $"Gỡ {_p.Name}";

    public IReadOnlyList<string> Actions
    {
        get
        {
            var list = new List<string>
            {
                $"Mở trình gỡ riêng của \"{_p.Name}\"{(_p.Version.Length > 0 ? " " + _p.Version : "")}: {_cmd.Exe}" + (_cmd.Args.Count > 0 ? " " + string.Join(" ", _cmd.Args) : ""),
                "Trình gỡ hiện cửa sổ riêng, bạn xác nhận ở đó; PCTool không gỡ im lặng",
                "Sau đó đọc lại danh sách chương trình để kiểm tra mục đã biến mất chưa",
                "Thư mục dữ liệu còn sót (nếu có) chưa bị đụng tới; dùng bước \"Quét tàn dư\" sau khi gỡ xong"
            };
            var w = UninstallPolicy.Evaluate(_p).Warning;
            if (w is not null) list.Insert(0, "CẢNH BÁO: " + w);
            return list;
        }
    }

    public bool RequiresRestorePoint => true;
    public bool IsUndoable => false;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Đang chạy trình gỡ của {_p.Name} (làm theo cửa sổ của trình gỡ)...");
        ProcessResult r;
        try { r = await _proc.RunAsync(_cmd.Exe, _cmd.Args, 1800, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return FixResult.Failed("Không mở được trình gỡ: " + ex.Message);
        }
        // Mã 0 và 3010 (cần khởi động lại) là ổn; mã khác vẫn để bước kiểm tra lại quyết định (nhiều trình gỡ trả mã lạ dù đã gỡ).
        if (r.ExitCode == -1 && r.Error.Contains("Quá thời gian")) return FixResult.Failed("Trình gỡ chạy quá lâu (30 phút) nên đã dừng.");
        Logger.Info($"Trình gỡ {_p.Name} kết thúc với mã {r.ExitCode}.");
        return FixResult.Succeeded($"Trình gỡ đã chạy xong (mã {r.ExitCode}); đang kiểm tra lại.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        for (int i = 0; i < _polls; i++)
        {
            var now = await InstalledProgramsScanner.RunAsync(_run, ct);
            if (!now.Any(x => x.SameEntry(_p))) return true;
            if (_poll > TimeSpan.Zero) await Task.Delay(_poll, ct);
        }
        return false;
    }
}
