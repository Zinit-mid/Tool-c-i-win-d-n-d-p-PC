using PCTool.Core.Framework;

namespace PCTool.Core.Defender;

/// <summary>Tắt Defender tạm thời, có hẹn giờ tự bật lại. Chỉ cho 15/30/60 phút.</summary>
public sealed class DisableDefenderFix : IFix
{
    private readonly IDefenderService _service;
    private readonly Func<DateTimeOffset> _now;

    public int Minutes { get; }

    public DisableDefenderFix(IDefenderService service, int minutes, Func<DateTimeOffset>? now = null)
    {
        if (minutes is not (15 or 30 or 60)) throw new ArgumentOutOfRangeException(nameof(minutes), "Chỉ cho phép 15, 30 hoặc 60 phút.");
        _service = service;
        Minutes = minutes;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Title => $"Tắt Windows Defender {Minutes} phút";

    public IReadOnlyList<string> Actions => new[]
    {
        $"Hẹn giờ để Windows tự bật lại Defender sau {Minutes} phút (kể cả khi tắt app hoặc khởi động lại máy)",
        "Tắt bảo vệ thời gian thực của Windows Defender",
        "Máy sẽ KHÔNG được bảo vệ khỏi virus trong thời gian này. Chỉ tắt khi bạn tin tưởng file định cài."
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang hẹn giờ bật lại và tắt Defender...");
        var r = await _service.DisableWithTimerAsync(Minutes, _now(), ct);
        return r.Ok ? FixResult.Succeeded(r.Message) : FixResult.Failed(r.Message);
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        (await _service.GetStatusAsync(ct)).RealTimeEnabled == false;
}

/// <summary>Bật lại Defender ngay và hủy hẹn giờ.</summary>
public sealed class EnableDefenderFix : IFix
{
    private readonly IDefenderService _service;

    public EnableDefenderFix(IDefenderService service) => _service = service;

    public string Title => "Bật lại Windows Defender";

    public IReadOnlyList<string> Actions => new[]
    {
        "Bật bảo vệ thời gian thực của Windows Defender",
        "Hủy hẹn giờ tự bật lại (không còn cần nữa)"
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang bật lại Defender...");
        var r = await _service.EnableAsync(ct);
        return r.Ok ? FixResult.Succeeded(r.Message) : FixResult.Failed(r.Message);
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        (await _service.GetStatusAsync(ct)).RealTimeEnabled == true;
}

/// <summary>Chỉ ĐỌC: nhìn trạng thái Defender và hẹn giờ rồi đưa ra vấn đề.</summary>
public static class DefenderAnalyzer
{
    public static IReadOnlyList<Finding> Analyze(
        DefenderStatus status, DefenderTimerState? timer, DateTimeOffset now, IFix? enableFix)
    {
        var fixes = enableFix == null ? Array.Empty<IFix>() : new[] { enableFix };
        var list = new List<Finding>();

        if (status.ServiceEnabled == false)
        {
            list.Add(new Finding
            {
                Id = "defender.inactive", Severity = Severity.Info,
                Title = "Windows Defender không hoạt động",
                Cause = "Có thể máy đang dùng phần mềm diệt virus khác. PCTool không can thiệp vào phần mềm đó."
            });
            return list;
        }

        if (status.RealTimeEnabled == false)
        {
            bool overdue = timer != null && now >= timer.ReEnableAt;
            bool waiting = timer != null && !overdue;
            list.Add(new Finding
            {
                Id = overdue ? "defender.off-overdue" : waiting ? "defender.off-temp" : "defender.off",
                Severity = waiting ? Severity.Info : Severity.Warning,
                Title = waiting ? "Defender đang tắt tạm" : overdue ? "Defender vẫn tắt dù đã quá giờ hẹn" : "Defender đang tắt",
                Cause = waiting
                    ? $"Sẽ tự bật lại lúc {timer!.ReEnableAt.ToLocalTime():HH:mm}. Bạn có thể bật lại ngay."
                    : "Máy đang không được bảo vệ khỏi virus. Nên bật lại.",
                Fixes = fixes
            });
        }
        else if (status.RealTimeEnabled == true && status.TamperProtected == true)
        {
            list.Add(new Finding
            {
                Id = "defender.tamper", Severity = Severity.Info,
                Title = "Tamper Protection đang bật",
                Cause = "Windows chặn mọi app tắt Defender. Nếu thật sự cần tắt, hãy tự vào Windows Security > Virus & threat protection > " +
                        "Manage settings > Tamper Protection và tắt một lần. PCTool không tìm cách vượt qua."
            });
        }

        return list;
    }
}
