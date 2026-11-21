using PCTool.Core.Framework;

namespace PCTool.Core.WindowsUpdate;

/// <summary>Tạm dừng Windows Update có hẹn giờ tự bật lại. Chỉ cho 1/6/24 giờ.</summary>
public sealed class PauseWindowsUpdateFix : IFix
{
    private readonly IWindowsUpdateService _service;
    private readonly Func<DateTimeOffset> _now;

    public int Minutes { get; }

    public PauseWindowsUpdateFix(IWindowsUpdateService service, int minutes, Func<DateTimeOffset>? now = null)
    {
        if (!WindowsUpdateService.AllowedMinutes.Contains(minutes))
            throw new ArgumentOutOfRangeException(nameof(minutes), "Chỉ cho phép 1 giờ, 6 giờ hoặc 24 giờ.");
        _service = service;
        Minutes = minutes;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Title => $"Tạm dừng Windows Update {WindowsUpdateService.Describe(Minutes)}";

    public IReadOnlyList<string> Actions => new[]
    {
        $"Hẹn giờ để Windows tự bật lại Windows Update sau {WindowsUpdateService.Describe(Minutes)} (kể cả khi tắt app; khởi động lại máy cũng tự bật lại)",
        "Tắt dịch vụ Windows Update (wuauserv) và Update Orchestrator (UsoSvc)",
        "Trong thời gian này máy KHÔNG nhận bản cập nhật và bản vá bảo mật. Chỉ nên dùng ngắn hạn, ví dụ khi cài phần mềm hoặc driver bị cập nhật chen ngang."
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang hẹn giờ bật lại và tạm dừng Windows Update...");
        var r = await _service.PauseWithTimerAsync(Minutes, _now(), ct);
        return r.Ok ? FixResult.Succeeded(r.Message) : FixResult.Failed(r.Message);
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        (await _service.GetStatusAsync(ct)).IsPaused == true;
}

/// <summary>Bật lại Windows Update đúng chế độ gốc và hủy hẹn giờ.</summary>
public sealed class ResumeWindowsUpdateFix : IFix
{
    private readonly IWindowsUpdateService _service;

    public ResumeWindowsUpdateFix(IWindowsUpdateService service) => _service = service;

    public string Title => "Bật lại Windows Update";

    public IReadOnlyList<string> Actions => new[]
    {
        "Đặt lại dịch vụ Windows Update về chế độ ban đầu và khởi động nó",
        "Hủy hẹn giờ tự bật lại (không còn cần nữa)"
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang bật lại Windows Update...");
        var r = await _service.ResumeAsync(ct);
        return r.Ok ? FixResult.Succeeded(r.Message) : FixResult.Failed(r.Message);
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        (await _service.GetStatusAsync(ct)).IsPaused == false;
}

/// <summary>Chỉ ĐỌC: nhìn trạng thái dịch vụ và hẹn giờ rồi đưa ra vấn đề.</summary>
public static class UpdatePauseAnalyzer
{
    public static IReadOnlyList<Finding> Analyze(UpdateServiceStatus status, UpdatePauseState? state, DateTimeOffset now, IFix? resumeFix)
    {
        if (status.IsPaused != true) return Array.Empty<Finding>();

        var fixes = resumeFix == null ? Array.Empty<IFix>() : new[] { resumeFix };
        bool overdue = state != null && now >= state.ReEnableAt;
        bool waiting = state != null && !overdue;
        return new[]
        {
            new Finding
            {
                Id = overdue ? "winupdate.off-overdue" : waiting ? "winupdate.paused" : "winupdate.off",
                Severity = waiting ? Severity.Info : Severity.Warning,
                Title = waiting ? "Windows Update đang tạm dừng" : overdue ? "Windows Update vẫn tắt dù đã quá giờ hẹn" : "Windows Update đang bị tắt",
                Cause = waiting
                    ? $"Sẽ tự bật lại lúc {state!.ReEnableAt.ToLocalTime():HH:mm}. Bạn có thể bật lại ngay."
                    : "Máy không nhận bản vá bảo mật. Nên bật lại, trừ khi đây là chính sách của công ty.",
                Fixes = fixes
            }
        };
    }
}
