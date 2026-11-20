using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Defender;

public sealed record DefenderOpResult(bool Ok, string Message);

public interface IDefenderService
{
    Task<DefenderStatus> GetStatusAsync(CancellationToken ct);
    DefenderTimerState? LoadTimer();

    /// <summary>Hẹn giờ bật lại TRƯỚC, rồi mới tắt. Nếu hẹn giờ lỗi thì không tắt.</summary>
    Task<DefenderOpResult> DisableWithTimerAsync(int minutes, DateTimeOffset now, CancellationToken ct);

    Task<DefenderOpResult> EnableAsync(CancellationToken ct);
}

/// <summary>
/// Bật/tắt bảo vệ thời gian thực của Windows Defender. Không cố vượt Tamper Protection:
/// nếu Windows không cho tắt thì báo lại, không làm cách nào khác.
/// Hẹn giờ bật lại do Task Scheduler của Windows (chạy bằng SYSTEM) nên vẫn bật dù app đã đóng,
/// và có thêm mốc "khi máy khởi động" để khởi động lại máy cũng tự bật lại.
/// </summary>
public sealed class DefenderService : IDefenderService
{
    public const string TaskName = "PCTool-ReEnableDefender";

    public const string StatusScript = """
$ErrorActionPreference = 'Stop'
try { Get-MpComputerStatus | Select-Object RealTimeProtectionEnabled,IsTamperProtected,AMServiceEnabled | ConvertTo-Json -Compress } catch { Write-Output '{}' }
""";

    public const string DisableScript = """
$ErrorActionPreference = 'Stop'
try { Set-MpPreference -DisableRealtimeMonitoring $true; Write-Output 'OK' } catch { Write-Output ('FAILED:' + $_.Exception.Message) }
""";

    public const string EnableScript = """
$ErrorActionPreference = 'Stop'
try {
  Set-MpPreference -DisableRealtimeMonitoring $false
  Unregister-ScheduledTask -TaskName 'PCTool-ReEnableDefender' -Confirm:$false -ErrorAction SilentlyContinue
  Write-Output 'OK'
} catch { Write-Output ('FAILED:' + $_.Exception.Message) }
""";

    public const string RemoveTaskScript = """
Unregister-ScheduledTask -TaskName 'PCTool-ReEnableDefender' -Confirm:$false -ErrorAction SilentlyContinue
Write-Output 'OK'
""";

    /// <summary>Chỉ nhúng số phút (số nguyên đã kiểm tra) và tên tác vụ cố định, không nhúng chuỗi người dùng.</summary>
    public static string BuildScheduleScript(int minutes) => $$"""
$ErrorActionPreference = 'Stop'
try {
  $when = (Get-Date).AddMinutes({{minutes}})
  $cmd = 'Set-MpPreference -DisableRealtimeMonitoring $false; Unregister-ScheduledTask -TaskName ''PCTool-ReEnableDefender'' -Confirm:$false'
  $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -Command "' + $cmd + '"')
  $t1 = New-ScheduledTaskTrigger -Once -At $when
  $t2 = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable
  Register-ScheduledTask -TaskName 'PCTool-ReEnableDefender' -Action $action -Trigger @($t1,$t2) -Principal $principal -Settings $settings -Force | Out-Null
  Write-Output 'OK'
} catch { Write-Output ('FAILED:' + $_.Exception.Message) }
""";

    private readonly Func<string, CancellationToken, Task<PowerShellResult>> _run;
    private readonly string _timerPath;

    public DefenderService()
        : this((s, ct) => PowerShellRunner.RunAsync(s, ct, 60), Path.Combine(AppPaths.DataDir, "defender-timer.json")) { }

    public DefenderService(Func<string, CancellationToken, Task<PowerShellResult>> run, string timerPath)
    {
        _run = run;
        _timerPath = timerPath;
    }

    public async Task<DefenderStatus> GetStatusAsync(CancellationToken ct)
    {
        var r = await _run(StatusScript, ct);
        return DefenderStatus.Parse(r.Output);
    }

    public async Task<DefenderOpResult> DisableWithTimerAsync(int minutes, DateTimeOffset now, CancellationToken ct)
    {
        if (minutes is not (15 or 30 or 60))
            return new DefenderOpResult(false, "Thời gian tắt không hợp lệ (chỉ 15, 30 hoặc 60 phút).");

        // Bước 1: hẹn giờ bật lại. Lỗi thì dừng, chưa tắt gì.
        var schedule = Parse(await Safe(BuildScheduleScript(minutes), ct));
        if (!schedule.Ok)
            return new DefenderOpResult(false, "Không hẹn được giờ tự bật lại nên chưa tắt Defender. " + schedule.Message);

        SaveTimer(new DefenderTimerState { OffAt = now, ReEnableAt = now.AddMinutes(minutes) });

        // Bước 2: tắt.
        var disable = Parse(await Safe(DisableScript, ct));
        if (!disable.Ok)
        {
            await CleanupTimerAsync(ct);
            return new DefenderOpResult(false, "Windows không cho tắt. " + disable.Message);
        }

        // Bước 3: Tamper Protection có thể làm lệnh "thành công" nhưng không có hiệu lực.
        var status = await GetStatusAsync(ct);
        if (status.RealTimeEnabled == true)
        {
            await CleanupTimerAsync(ct);
            return new DefenderOpResult(false,
                "Defender vẫn đang bật. Có thể Tamper Protection đang chặn: hãy tắt tay một lần trong Windows Security > " +
                "Virus & threat protection > Manage settings > Tamper Protection, rồi thử lại.");
        }

        return new DefenderOpResult(true, $"Đã tắt Defender, sẽ tự bật lại sau {minutes} phút.");
    }

    public async Task<DefenderOpResult> EnableAsync(CancellationToken ct)
    {
        var r = Parse(await Safe(EnableScript, ct));
        if (r.Ok) DeleteTimer();
        return r.Ok ? new DefenderOpResult(true, "Đã bật lại Defender.") : new DefenderOpResult(false, r.Message);
    }

    public DefenderTimerState? LoadTimer()
    {
        if (!File.Exists(_timerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<DefenderTimerState>(File.ReadAllText(_timerPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // File hỏng: đổi tên, không xóa âm thầm.
            try { File.Move(_timerPath, _timerPath + ".corrupt", true); } catch (IOException) { }
            Logger.Warn("defender-timer.json bị hỏng, đã đổi tên thành .corrupt.");
            return null;
        }
    }

    private void SaveTimer(DefenderTimerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_timerPath)!);
        var tmp = _timerPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, _timerPath, true);
    }

    private void DeleteTimer()
    {
        try { if (File.Exists(_timerPath)) File.Delete(_timerPath); } catch (IOException) { }
    }

    private async Task CleanupTimerAsync(CancellationToken ct)
    {
        try { await _run(RemoveTaskScript, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        DeleteTimer();
    }

    private async Task<PowerShellResult> Safe(string script, CancellationToken ct)
    {
        try
        {
            return await _run(script, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PowerShellResult(-1, "FAILED:" + ex.Message, "");
        }
    }

    public static DefenderOpResult Parse(PowerShellResult r)
    {
        var text = (r.Output ?? "").Trim();
        if (text == "OK") return new DefenderOpResult(true, "");
        if (text.StartsWith("FAILED:", StringComparison.Ordinal)) return new DefenderOpResult(false, text[7..].Trim());
        return new DefenderOpResult(false, string.IsNullOrWhiteSpace(r.Error) ? "Không có phản hồi từ PowerShell." : r.Error.Trim());
    }
}
