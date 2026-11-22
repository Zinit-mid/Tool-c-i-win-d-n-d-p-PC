using System.Text;
using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Startup;

public enum StartupLocation { HkcuRun, HklmRun, HklmRun32 }

public sealed record StartupItem(StartupLocation Location, string Name, string Command, bool Enabled)
{
    public string Where => Location switch
    {
        StartupLocation.HkcuRun => "Tài khoản của bạn",
        StartupLocation.HklmRun => "Mọi tài khoản",
        _ => "Mọi tài khoản (32-bit)"
    };
}

public static class StartupKeys
{
    public static string RunKey(StartupLocation l) => l switch
    {
        StartupLocation.HkcuRun => @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
        StartupLocation.HklmRun => @"HKLM:\Software\Microsoft\Windows\CurrentVersion\Run",
        StartupLocation.HklmRun32 => @"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        _ => throw new ArgumentOutOfRangeException(nameof(l))
    };

    public static string ApprovedKey(StartupLocation l) => l switch
    {
        StartupLocation.HkcuRun => @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        StartupLocation.HklmRun => @"HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        StartupLocation.HklmRun32 => @"HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        _ => throw new ArgumentOutOfRangeException(nameof(l))
    };
}

public static class StartupScanner
{
    /// <summary>Chỉ đọc 3 khóa Run. Mục bị tắt = byte đầu của giá trị StartupApproved là số lẻ (cách Task Manager lưu).</summary>
    public static string Script { get; } = $$"""
$ErrorActionPreference = 'SilentlyContinue'
$out = @()
foreach ($def in @(
  @{ loc = 'HkcuRun'; run = '{{StartupKeys.RunKey(StartupLocation.HkcuRun)}}'; ap = '{{StartupKeys.ApprovedKey(StartupLocation.HkcuRun)}}' },
  @{ loc = 'HklmRun'; run = '{{StartupKeys.RunKey(StartupLocation.HklmRun)}}'; ap = '{{StartupKeys.ApprovedKey(StartupLocation.HklmRun)}}' },
  @{ loc = 'HklmRun32'; run = '{{StartupKeys.RunKey(StartupLocation.HklmRun32)}}'; ap = '{{StartupKeys.ApprovedKey(StartupLocation.HklmRun32)}}' })) {
  $rk = Get-Item -Path $def.run
  if (-not $rk) { continue }
  $ak = Get-Item -Path $def.ap
  foreach ($n in $rk.GetValueNames()) {
    if ($n -eq '') { continue }
    $enabled = $true
    if ($ak) { $b = $ak.GetValue($n); if ($b -is [byte[]] -and $b.Length -gt 0 -and ($b[0] -band 1) -eq 1) { $enabled = $false } }
    $out += [ordered]@{ loc = $def.loc; name = [string]$n; cmd = [string]$rk.GetValue($n); enabled = $enabled }
  }
}
ConvertTo-Json -InputObject @($out) -Compress -Depth 3
""";

    public static IReadOnlyList<StartupItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var res = new List<StartupItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return res;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!Enum.TryParse<StartupLocation>(e.TryGetProperty("loc", out var l) ? l.GetString() : "", out var loc)) continue;
            var name = Clean(e.TryGetProperty("name", out var n) ? n.GetString() : "");
            if (name.Length == 0) continue;
            var cmd = Clean(e.TryGetProperty("cmd", out var c) ? c.GetString() : "");
            bool enabled = !e.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
            res.Add(new StartupItem(loc, name, cmd, enabled));
        }
        return res;
    }

    private static string Clean(string? s) => new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();

    public static async Task<IReadOnlyList<StartupItem>> ScanAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c, 30);
        var r = await run(Script, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được danh sách khởi động." : r.Error.Trim());
        return Parse(string.IsNullOrWhiteSpace(r.Output) ? "[]" : r.Output);
    }
}

/// <summary>Tắt/bật một mục khởi động cùng Windows bằng đúng cách Task Manager làm (giá trị StartupApproved). Không xóa mục, không đụng chương trình; bật lại được.</summary>
public sealed class StartupToggleFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly StartupItem _item;
    private readonly bool _enable;

    public StartupToggleFix(StartupItem item, bool enable, ScriptRunner? run = null)
    {
        if (!Enum.IsDefined(typeof(StartupLocation), item.Location)) throw new ArgumentException("Vị trí không hợp lệ.", nameof(item));
        if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 || item.Name.Any(char.IsControl))
            throw new ArgumentException("Tên mục khởi động không hợp lệ.", nameof(item));
        _item = item; _enable = enable;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 30));
    }

    public string Title => _enable ? $"Bật khởi động cùng Windows: {_item.Name}" : $"Tắt khởi động cùng Windows: {_item.Name}";
    public IReadOnlyList<string> Actions => new[]
    {
        _enable ? $"Cho \"{_item.Name}\" tự chạy lại khi bật máy" : $"Ngăn \"{_item.Name}\" tự chạy khi bật máy",
        "Không xóa chương trình, không xóa mục; chỉ đổi cờ bật/tắt như Task Manager làm",
        "Hoàn tác bằng nút ngược lại"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    private string B64 => Convert.ToBase64String(Encoding.UTF8.GetBytes(_item.Name));

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$name = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64}}'))
$rk = Get-Item -Path '{{StartupKeys.RunKey(_item.Location)}}'
if ($null -eq $rk.GetValue($name)) { throw 'Mục này không còn trong danh sách khởi động.' }
$k = '{{StartupKeys.ApprovedKey(_item.Location)}}'
if (-not (Test-Path -Path $k)) { New-Item -Path $k -Force | Out-Null }
$flag = {{(_enable ? 2 : 3)}}
$bytes = [byte[]]($flag,0,0,0,0,0,0,0,0,0,0,0)
Set-ItemProperty -Path $k -Name $name -Value $bytes -Type Binary
""";

    public string VerifyScript() => $$"""
$ErrorActionPreference = 'Stop'
$name = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64}}'))
$ak = Get-Item -Path '{{StartupKeys.ApprovedKey(_item.Location)}}'
$b = $ak.GetValue($name)
if ($b -is [byte[]] -and $b.Length -gt 0 -and ($b[0] -band 1) -eq 1) { 'OFF' } else { 'ON' }
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var r = await _run(BuildScript(), ct);
        return r.ExitCode == 0
            ? FixResult.Succeeded(_enable ? "Đã bật lại. Có hiệu lực từ lần bật máy sau." : "Đã tắt. Có hiệu lực từ lần bật máy sau.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không đổi được." : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var r = await _run(VerifyScript(), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("Không đọc lại được trạng thái.");
        return r.Output.Trim() == (_enable ? "ON" : "OFF");
    }
}
