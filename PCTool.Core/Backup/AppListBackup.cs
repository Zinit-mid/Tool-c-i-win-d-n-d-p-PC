using System.Text.Json;
using System.Text.RegularExpressions;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Backup;

public sealed record InstalledApp(string Name, string Version, string Publisher);

public sealed record AppListResult(IReadOnlyList<InstalledApp> Installed, bool WingetOk, string WingetError, IReadOnlyList<string> WingetIds);

public static class AppListParser
{
    private static readonly Regex WingetId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

    public static AppListResult Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Danh sách app không đúng định dạng.", ex); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("Danh sách app không đúng định dạng.");

            var apps = new List<InstalledApp>();
            foreach (var a in Items(r, "installed"))
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = Str(a, "name").Trim();
                if (name.Length == 0 || name.Length > 300) continue;
                apps.Add(new InstalledApp(name, Cut(Str(a, "version")), Cut(Str(a, "publisher"))));
            }
            // Trùng tên+phiên bản (registry 32/64-bit lặp) chỉ giữ một.
            var uniq = apps.GroupBy(x => (x.Name.ToLowerInvariant(), x.Version)).Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            var wingetOk = false; var wingetErr = ""; var ids = new List<string>();
            if (r.TryGetProperty("winget", out var w) && w.ValueKind == JsonValueKind.Object)
            {
                wingetOk = w.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                wingetErr = Str(w, "error");
                foreach (var id in Items(w, "ids"))
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { } s && WingetId.IsMatch(s)) ids.Add(s);
                ids = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }
            return new AppListResult(uniq, wingetOk, wingetErr, ids);
        }
    }

    private static string Cut(string s) => s.Length > 100 ? s[..100] : s;
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array) { foreach (var e in v.EnumerateArray()) yield return e; }
        else if (v.ValueKind is JsonValueKind.Object or JsonValueKind.String) yield return v;
    }
    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Nội dung restore-plan.json (GĐ4 sẽ đọc file này để khôi phục).</summary>
    public static string ToRestorePlanJson(AppListResult r, DateTimeOffset now, string computerName)
    {
        var plan = new
        {
            schemaVersion = 1,
            createdAt = now.ToString("o"),
            computerName,
            note = "Danh sách app để cài lại sau khi cài Windows. wingetPackages: có thể cài tự động bằng winget. installedApps: tất cả app đang cài, để bạn đối chiếu và cài tay phần còn lại.",
            wingetOk = r.WingetOk,
            wingetError = r.WingetError,
            wingetPackages = r.WingetIds.Select(i => new { id = i }).ToArray(),
            installedApps = r.Installed.Select(a => new { name = a.Name, version = a.Version, publisher = a.Publisher }).ToArray()
        };
        return JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

/// <summary>
/// GĐ3: lưu danh sách app đang cài (từ registry Programs and Features) và các gói winget cài lại được (winget export) vào restore-plan.json.
/// CHỈ ĐỌC máy, chỉ ghi một file mới vào thư mục sao lưu. Không gỡ/cài app nào.
/// </summary>
public sealed class AppListBackupFix : IFix
{
    public const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
$apps = @()
foreach ($k in $keys) {
  foreach ($p in @(Get-ItemProperty -Path $k)) {
    if ($p.DisplayName -and -not $p.SystemComponent -and -not $p.ParentKeyName) {
      $apps += [ordered]@{ name = "$($p.DisplayName)"; version = "$($p.DisplayVersion)"; publisher = "$($p.Publisher)" }
    }
  }
}
$w = [ordered]@{ ok = $false; error = ''; ids = @() }
$wg = Get-Command winget -ErrorAction SilentlyContinue
if ($wg) {
  $tmp = Join-Path $env:TEMP ('pctool-winget-' + [guid]::NewGuid().ToString('N') + '.json')
  try {
    & winget export -o $tmp --accept-source-agreements | Out-Null
    if (Test-Path -LiteralPath $tmp) {
      $j = Get-Content -LiteralPath $tmp -Raw -Encoding UTF8 | ConvertFrom-Json
      $w.ids = @($j.Sources | ForEach-Object { $_.Packages } | ForEach-Object { "$($_.PackageIdentifier)" })
      $w.ok = $true
    } else { $w.error = 'winget không tạo được file xuất.' }
  } catch { $w.error = "$($_.Exception.Message)" }
  finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
} else { $w.error = 'Máy không có winget.' }
[ordered]@{ installed = $apps; winget = $w } | ConvertTo-Json -Depth 5 -Compress
""";

    private readonly string _dir;
    private readonly string _file;
    private readonly ScriptRunner _run;
    private readonly Func<string, string, CancellationToken, Task> _write;
    private readonly Func<string, string?> _read;
    private readonly Func<DateTimeOffset> _clock;
    private int _installedCount, _wingetCount;

    public AppListBackupFix(string destRoot, DateTime now, ScriptRunner? run = null,
        Func<string, string, CancellationToken, Task>? writeFile = null, Func<string, string?>? readFile = null, Func<DateTimeOffset>? clock = null)
    {
        _dir = BackupPath.Combine(destRoot, now, "apps");
        _file = _dir + "\\restore-plan.json";
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 600));
        _write = writeFile ?? WriteAtomicAsync;
        _read = readFile ?? (p => File.Exists(p) ? File.ReadAllText(p) : null);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string FilePath => _file;
    public string Title => "Lưu danh sách app đã cài";
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public IReadOnlyList<string> Actions => new[]
    {
        $"Tạo thư mục mới: {_dir}",
        "Đọc danh sách app trong Programs and Features và chạy `winget export` để biết app nào cài lại tự động được (chỉ đọc, không cài/gỡ gì)",
        $"Ghi một file: {_file}"
    };

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang đọc danh sách app (có thể mất 1-2 phút nếu có winget)...");
        var r = await _run(Script, ct);
        if (string.IsNullOrWhiteSpace(r.Output))
            return FixResult.Failed("Không đọc được danh sách app: " + (string.IsNullOrWhiteSpace(r.Error) ? "PowerShell không trả về dữ liệu." : r.Error.Trim()));

        AppListResult list;
        try { list = AppListParser.Parse(r.Output.Trim()); }
        catch (FormatException ex) { return FixResult.Failed(ex.Message); }

        if (list.Installed.Count == 0 && list.WingetIds.Count == 0)
            return FixResult.Failed("Không thấy app nào đã cài (lạ). Chưa ghi file.");

        await _write(_file, AppListParser.ToRestorePlanJson(list, _clock(), Environment.MachineName), ct);
        _installedCount = list.Installed.Count;
        _wingetCount = list.WingetIds.Count;
        var w = list.WingetOk ? $"{list.WingetIds.Count} gói cài lại tự động bằng winget" : "winget không dùng được (" + (list.WingetError.Length > 0 ? list.WingetError : "không rõ lý do") + "), chỉ có danh sách để cài tay";
        return FixResult.Succeeded($"Đã lưu {list.Installed.Count} app đang cài; {w}. File: {_file}");
    }

    public Task<bool> VerifyAsync(CancellationToken ct)
    {
        var text = _read(_file);
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("installedApps", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == _installedCount
                  && root.TryGetProperty("wingetPackages", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == _wingetCount;
            return Task.FromResult(ok);
        }
        catch (JsonException) { return Task.FromResult(false); }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new System.Text.UTF8Encoding(false), ct);
        File.Move(tmp, path, overwrite: true);
    }
}
