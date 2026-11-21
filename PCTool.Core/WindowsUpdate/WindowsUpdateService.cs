using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.WindowsUpdate;

public sealed record UpdateOpResult(bool Ok, string Message);

/// <summary>Chế độ khởi động của dịch vụ Windows Update. Mỗi trường null nghĩa là không đọc được.</summary>
public sealed record UpdateServiceStatus(string? WuMode, string? WuState, string? UsoMode)
{
    public static UpdateServiceStatus Unknown { get; } = new(null, null, null);

    public bool? IsPaused => WuMode == null ? null : WuMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

    public static UpdateServiceStatus Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Unknown;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Unknown;
            return new UpdateServiceStatus(Str(r, "wuMode"), Str(r, "wuState"), Str(r, "usoMode"));
        }
        catch (JsonException)
        {
            return Unknown;
        }
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;
}

/// <summary>Ghi lại chế độ gốc của dịch vụ để bật lại đúng như cũ. Lưu ở update-timer.json.</summary>
public sealed class UpdatePauseState
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset OffAt { get; set; }
    public DateTimeOffset ReEnableAt { get; set; }
    public string WuType { get; set; } = "Manual";
    public string UsoType { get; set; } = "Manual";
}

public interface IWindowsUpdateService
{
    Task<UpdateServiceStatus> GetStatusAsync(CancellationToken ct);
    UpdatePauseState? LoadState();

    /// <summary>Hẹn giờ bật lại TRƯỚC, rồi mới tạm dừng. Hẹn giờ lỗi thì không dừng gì.</summary>
    Task<UpdateOpResult> PauseWithTimerAsync(int minutes, DateTimeOffset now, CancellationToken ct);

    Task<UpdateOpResult> ResumeAsync(CancellationToken ct);
}

/// <summary>
/// Tạm dừng Windows Update (đặt dịch vụ wuauserv và UsoSvc ở chế độ Disabled) và luôn hẹn giờ tự bật lại bằng Task Scheduler
/// (chạy bằng SYSTEM, có thêm mốc "khi máy khởi động"). Bật lại đặt đúng chế độ gốc đã ghi lại.
/// Nếu Windows hay chính sách công ty không cho đổi thì báo lại, không tìm cách vượt.
/// </summary>
public sealed class WindowsUpdateService : IWindowsUpdateService
{
    public const string TaskName = "PCTool-ReEnableWindowsUpdate";
    public static readonly int[] AllowedMinutes = { 60, 360, 1440 };

    public const string StatusScript = """
$ErrorActionPreference = 'SilentlyContinue'
$w = Get-CimInstance Win32_Service -Filter "Name='wuauserv'"
$u = Get-CimInstance Win32_Service -Filter "Name='UsoSvc'"
[ordered]@{ wuMode = "$($w.StartMode)"; wuState = "$($w.State)"; usoMode = "$($u.StartMode)" } | ConvertTo-Json -Compress
""";

    public const string PauseScript = """
$ErrorActionPreference = 'Stop'
try {
  foreach ($n in 'wuauserv','UsoSvc') { Set-Service -Name $n -StartupType Disabled; Stop-Service -Name $n -Force -ErrorAction SilentlyContinue }
  Write-Output 'OK'
} catch { Write-Output ('FAILED:' + $_.Exception.Message) }
""";

    public const string RemoveTaskScript = """
Unregister-ScheduledTask -TaskName 'PCTool-ReEnableWindowsUpdate' -Confirm:$false -ErrorAction SilentlyContinue
Write-Output 'OK'
""";

    /// <summary>Chỉ trả "Automatic" hoặc "Manual" (danh sách trắng) để nhúng vào script.</summary>
    public static string ToStartupType(string? mode) =>
        mode != null && mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "Automatic" : "Manual";

    public static string BuildResumeScript(string wuType, string usoType) => $$"""
$ErrorActionPreference = 'Stop'
try {
  Set-Service -Name wuauserv -StartupType {{Safe(wuType)}}
  Set-Service -Name UsoSvc -StartupType {{Safe(usoType)}}
  Start-Service -Name wuauserv -ErrorAction SilentlyContinue
  Unregister-ScheduledTask -TaskName 'PCTool-ReEnableWindowsUpdate' -Confirm:$false -ErrorAction SilentlyContinue
  Write-Output 'OK'
} catch { Write-Output ('FAILED:' + $_.Exception.Message) }
""";

    /// <summary>Chỉ nhúng số phút (đã kiểm tra), kiểu khởi động (danh sách trắng) và tên tác vụ cố định.</summary>
    public static string BuildScheduleScript(int minutes, string wuType, string usoType)
    {
        if (!AllowedMinutes.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        return $$"""
$ErrorActionPreference = 'Stop'
try {
  $when = (Get-Date).AddMinutes({{minutes}})
  $cmd = 'Set-Service -Name wuauserv -StartupType {{Safe(wuType)}}; Set-Service -Name UsoSvc -StartupType {{Safe(usoType)}}; Start-Service -Name wuauserv -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName ''PCTool-ReEnableWindowsUpdate'' -Confirm:$false'
  $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -Command "' + $cmd + '"')
  $t1 = New-ScheduledTaskTrigger -Once -At $when
  $t2 = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable
  Register-ScheduledTask -TaskName 'PCTool-ReEnableWindowsUpdate' -Action $action -Trigger @($t1,$t2) -Principal $principal -Settings $settings -Force | Out-Null
  Write-Output 'OK'
} catch { Write-Output ('FAILED:' + $_.Exception.Message) }
""";
    }

    private static string Safe(string type) => type is "Automatic" or "Manual" ? type : "Manual";

    private readonly ScriptRunner _run;
    private readonly string _statePath;

    public WindowsUpdateService()
        : this((s, ct) => PowerShellRunner.RunAsync(s, ct, 60), Path.Combine(AppPaths.DataDir, "update-timer.json")) { }

    public WindowsUpdateService(ScriptRunner run, string statePath)
    {
        _run = run;
        _statePath = statePath;
    }

    public async Task<UpdateServiceStatus> GetStatusAsync(CancellationToken ct) =>
        UpdateServiceStatus.Parse((await _run(StatusScript, ct)).Output);

    public async Task<UpdateOpResult> PauseWithTimerAsync(int minutes, DateTimeOffset now, CancellationToken ct)
    {
        if (!AllowedMinutes.Contains(minutes))
            return new UpdateOpResult(false, "Thời gian không hợp lệ (chỉ 1 giờ, 6 giờ hoặc 24 giờ).");

        var before = await GetStatusAsync(ct);
        if (before.WuMode == null)
            return new UpdateOpResult(false, "Không đọc được trạng thái Windows Update nên chưa thay đổi gì.");
        if (before.IsPaused == true)
            return new UpdateOpResult(false, "Windows Update đã đang bị tắt sẵn. Hãy bấm Bật lại trước.");

        string wu = ToStartupType(before.WuMode), uso = ToStartupType(before.UsoMode);

        // Bước 1: hẹn giờ bật lại. Lỗi thì dừng, chưa tắt gì.
        var schedule = Parse(await Run(BuildScheduleScript(minutes, wu, uso), ct));
        if (!schedule.Ok)
            return new UpdateOpResult(false, "Không hẹn được giờ tự bật lại nên chưa tạm dừng Windows Update. " + schedule.Message);

        SaveState(new UpdatePauseState { OffAt = now, ReEnableAt = now.AddMinutes(minutes), WuType = wu, UsoType = uso });

        // Bước 2: tạm dừng.
        var pause = Parse(await Run(PauseScript, ct));
        if (!pause.Ok)
        {
            await RollbackAsync(wu, uso, ct);
            return new UpdateOpResult(false, "Windows không cho tạm dừng (máy công ty có thể đang chặn bằng chính sách). " + pause.Message);
        }

        // Bước 3: kiểm tra có hiệu lực thật không.
        if ((await GetStatusAsync(ct)).IsPaused != true)
        {
            await RollbackAsync(wu, uso, ct);
            return new UpdateOpResult(false, "Lệnh chạy xong nhưng Windows Update vẫn chưa bị dừng, đã trả lại như cũ.");
        }

        return new UpdateOpResult(true, $"Đã tạm dừng Windows Update, sẽ tự bật lại sau {Describe(minutes)}.");
    }

    public async Task<UpdateOpResult> ResumeAsync(CancellationToken ct)
    {
        var st = LoadState();
        var r = Parse(await Run(BuildResumeScript(st?.WuType ?? "Manual", st?.UsoType ?? "Manual"), ct));
        if (r.Ok) DeleteState();
        return r.Ok ? new UpdateOpResult(true, "Đã bật lại Windows Update.") : r;
    }

    public static string Describe(int minutes) => minutes >= 1440 ? "24 giờ" : minutes >= 60 ? $"{minutes / 60} giờ" : $"{minutes} phút";

    public UpdatePauseState? LoadState()
    {
        if (!File.Exists(_statePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<UpdatePauseState>(File.ReadAllText(_statePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try { File.Move(_statePath, _statePath + ".corrupt", true); } catch (IOException) { }
            Logger.Warn("update-timer.json bị hỏng, đã đổi tên thành .corrupt.");
            return null;
        }
    }

    private void SaveState(UpdatePauseState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, _statePath, true);
    }

    private void DeleteState()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch (IOException) { }
    }

    private async Task RollbackAsync(string wu, string uso, CancellationToken ct)
    {
        try { await Run(BuildResumeScript(wu, uso), ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        DeleteState();
    }

    private async Task<PowerShellResult> Run(string script, CancellationToken ct)
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

    public static UpdateOpResult Parse(PowerShellResult r)
    {
        var text = (r.Output ?? "").Trim();
        if (text == "OK") return new UpdateOpResult(true, "");
        if (text.StartsWith("FAILED:", StringComparison.Ordinal)) return new UpdateOpResult(false, text[7..].Trim());
        return new UpdateOpResult(false, string.IsNullOrWhiteSpace(r.Error) ? "Không có phản hồi từ PowerShell." : r.Error.Trim());
    }
}
