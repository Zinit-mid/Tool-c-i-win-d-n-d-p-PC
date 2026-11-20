using PCTool.Core.Defender;
using PCTool.Core.Framework;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Defender;

internal sealed class FakeDefender : IDefenderService
{
    public DefenderStatus Status { get; set; } = new(true, false, true);
    public DefenderOpResult DisableResult { get; set; } = new(true, "ok");
    public int DisableCalls { get; private set; }
    public int EnableCalls { get; private set; }
    public int? LastMinutes { get; private set; }

    public Task<DefenderStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);
    public DefenderTimerState? LoadTimer() => null;

    public Task<DefenderOpResult> DisableWithTimerAsync(int minutes, DateTimeOffset now, CancellationToken ct)
    {
        DisableCalls++;
        LastMinutes = minutes;
        if (DisableResult.Ok) Status = new DefenderStatus(false, false, true);
        return Task.FromResult(DisableResult);
    }

    public Task<DefenderOpResult> EnableAsync(CancellationToken ct)
    {
        EnableCalls++;
        Status = new DefenderStatus(true, false, true);
        return Task.FromResult(new DefenderOpResult(true, "ok"));
    }
}

public sealed class DefenderStatusTests
{
    [Fact]
    public void Parse_ReadsAllFlags()
    {
        var s = DefenderStatus.Parse("""{"RealTimeProtectionEnabled":true,"IsTamperProtected":false,"AMServiceEnabled":true}""");

        Assert.Equal(new DefenderStatus(true, false, true), s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("hỏng")]
    [InlineData("[1]")]
    public void Parse_MissingOrBad_IsUnknown(string? json) =>
        Assert.Equal(DefenderStatus.Unknown, DefenderStatus.Parse(json));
}

public sealed class DefenderAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly IFix Enable = new EnableDefenderFix(new FakeDefender());

    [Fact]
    public void ProtectionOn_NoTamper_NoFindings() =>
        Assert.Empty(DefenderAnalyzer.Analyze(new(true, false, true), null, Now, Enable));

    [Fact]
    public void TamperOn_GivesGuidanceOnly_NoFix()
    {
        var f = Assert.Single(DefenderAnalyzer.Analyze(new(true, true, true), null, Now, Enable));

        Assert.Equal("defender.tamper", f.Id);
        Assert.Empty(f.Fixes);
    }

    [Fact]
    public void OffWithoutTimer_IsWarning_WithEnableFix()
    {
        var f = Assert.Single(DefenderAnalyzer.Analyze(new(false, false, true), null, Now, Enable));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Single(f.Fixes);
    }

    [Fact]
    public void OffWithinTimer_IsInfo()
    {
        var timer = new DefenderTimerState { OffAt = Now, ReEnableAt = Now.AddMinutes(15) };

        var f = Assert.Single(DefenderAnalyzer.Analyze(new(false, false, true), timer, Now.AddMinutes(1), Enable));

        Assert.Equal("defender.off-temp", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
    }

    [Fact]
    public void OffPastTimer_IsWarning_Overdue()
    {
        var timer = new DefenderTimerState { OffAt = Now, ReEnableAt = Now.AddMinutes(15) };

        var f = Assert.Single(DefenderAnalyzer.Analyze(new(false, false, true), timer, Now.AddMinutes(20), Enable));

        Assert.Equal("defender.off-overdue", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
    }

    [Fact]
    public void ServiceInactive_OtherAntivirus_ReportsInfoOnly()
    {
        var f = Assert.Single(DefenderAnalyzer.Analyze(new(false, null, false), null, Now, Enable));

        Assert.Equal("defender.inactive", f.Id);
        Assert.Empty(f.Fixes);
    }
}

public sealed class DefenderFixTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(61)]
    [InlineData(-5)]
    public void DisableFix_RejectsOtherDurations(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisableDefenderFix(new FakeDefender(), minutes));

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void DisableFix_AcceptsAllowedDurations(int minutes) =>
        Assert.Equal(minutes, new DisableDefenderFix(new FakeDefender(), minutes).Minutes);

    [Fact]
    public async Task DryRun_NeverTouchesDefender()
    {
        var svc = new FakeDefender();
        var executor = new FixExecutor(new FakeRestorePoints());

        var result = await executor.RunAsync(new DisableDefenderFix(svc, 15), new FixOptions { DryRun = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Simulated, result.Outcome);
        Assert.Equal(0, svc.DisableCalls);
        Assert.True(svc.Status.RealTimeEnabled);
    }

    [Fact]
    public async Task SafeMode_BlocksDisable()
    {
        var svc = new FakeDefender();
        var executor = new FixExecutor(new FakeRestorePoints());

        var result = await executor.RunAsync(new DisableDefenderFix(svc, 15),
            new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Blocked, result.Outcome);
        Assert.Equal(0, svc.DisableCalls);
    }

    [Fact]
    public async Task RealRun_DisablesAndVerifies()
    {
        var svc = new FakeDefender();
        var restore = new FakeRestorePoints();
        var executor = new FixExecutor(restore);

        var result = await executor.RunAsync(new DisableDefenderFix(svc, 30),
            new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal(30, svc.LastMinutes);
        Assert.Equal(0, restore.Calls);   // tắt Defender không đổi file nên không cần điểm khôi phục
    }

    [Fact]
    public async Task DisableFailed_IsReportedAsFailed()
    {
        var svc = new FakeDefender { DisableResult = new(false, "Tamper") };
        var executor = new FixExecutor(new FakeRestorePoints());

        var result = await executor.RunAsync(new DisableDefenderFix(svc, 15),
            new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task EnableFix_EnablesAndVerifies()
    {
        var svc = new FakeDefender { Status = new(false, false, true) };
        var executor = new FixExecutor(new FakeRestorePoints());

        var result = await executor.RunAsync(new EnableDefenderFix(svc),
            new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, svc.EnableCalls);
    }

    [Fact]
    public void DisableFix_ActionsWarnAboutRisk() =>
        Assert.Contains(new DisableDefenderFix(new FakeDefender(), 15).Actions, a => a.Contains("KHÔNG được bảo vệ"));
}

public sealed class DefenderServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pctool-test-" + Guid.NewGuid().ToString("N"));
    private string TimerPath => Path.Combine(_dir, "defender-timer.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static PowerShellResult Ok(string o = "OK") => new(0, o, "");

    private sealed class Script
    {
        public List<string> Calls { get; } = new();
        public Func<string, PowerShellResult> Respond { get; set; } = _ => Ok();

        public Task<PowerShellResult> Run(string script, CancellationToken ct)
        {
            Calls.Add(script);
            return Task.FromResult(Respond(script));
        }
    }

    [Fact]
    public async Task Disable_SchedulesFirst_ThenDisables_AndSavesTimer()
    {
        var s = new Script
        {
            Respond = script => script.Contains("Get-MpComputerStatus")
                ? Ok("""{"RealTimeProtectionEnabled":false,"IsTamperProtected":false,"AMServiceEnabled":true}""")
                : Ok()
        };
        var svc = new DefenderService(s.Run, TimerPath);
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        var r = await svc.DisableWithTimerAsync(30, now, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Contains("Register-ScheduledTask", s.Calls[0]);
        Assert.Contains("AddMinutes(30)", s.Calls[0]);
        Assert.Contains("DisableRealtimeMonitoring $true", s.Calls[1]);
        var timer = svc.LoadTimer();
        Assert.Equal(now.AddMinutes(30), timer?.ReEnableAt);
    }

    [Fact]
    public async Task Disable_ScheduleFails_NeverDisables()
    {
        var s = new Script { Respond = _ => Ok("FAILED:không có quyền") };
        var svc = new DefenderService(s.Run, TimerPath);

        var r = await svc.DisableWithTimerAsync(15, DateTimeOffset.Now, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.DoesNotContain(s.Calls, c => c.Contains("DisableRealtimeMonitoring $true"));
        Assert.Null(svc.LoadTimer());
    }

    [Fact]
    public async Task Disable_StillOnAfterCommand_TamperProtection_CleansUpAndExplains()
    {
        var s = new Script
        {
            Respond = script => script.Contains("Get-MpComputerStatus")
                ? Ok("""{"RealTimeProtectionEnabled":true,"IsTamperProtected":true,"AMServiceEnabled":true}""")
                : Ok()
        };
        var svc = new DefenderService(s.Run, TimerPath);

        var r = await svc.DisableWithTimerAsync(15, DateTimeOffset.Now, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("Tamper Protection", r.Message);
        Assert.Contains(s.Calls, c => c.Contains("Unregister-ScheduledTask") && !c.Contains("Set-MpPreference"));
        Assert.Null(svc.LoadTimer());
    }

    [Fact]
    public async Task Disable_InvalidMinutes_DoesNothing()
    {
        var s = new Script();
        var svc = new DefenderService(s.Run, TimerPath);

        var r = await svc.DisableWithTimerAsync(5, DateTimeOffset.Now, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(s.Calls);
    }

    [Fact]
    public async Task Disable_RunnerThrows_ReturnsFailure_NotCrash()
    {
        var svc = new DefenderService((_, _) => throw new InvalidOperationException("không có PowerShell"), TimerPath);

        var r = await svc.DisableWithTimerAsync(15, DateTimeOffset.Now, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("không có PowerShell", r.Message);
    }

    [Fact]
    public async Task Enable_RemovesTimerFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TimerPath, "{}");
        var svc = new DefenderService(new Script().Run, TimerPath);

        var r = await svc.EnableAsync(CancellationToken.None);

        Assert.True(r.Ok);
        Assert.False(File.Exists(TimerPath));
    }

    [Fact]
    public void LoadTimer_Corrupt_IsRenamed_NotDeleted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TimerPath, "{ hỏng");
        var svc = new DefenderService(new Script().Run, TimerPath);

        Assert.Null(svc.LoadTimer());
        Assert.True(File.Exists(TimerPath + ".corrupt"));
    }

    [Fact]
    public void LoadTimer_NoFile_ReturnsNull() =>
        Assert.Null(new DefenderService(new Script().Run, TimerPath).LoadTimer());

    [Fact]
    public void ScheduleScript_OnlyEmbedsNumberAndFixedTaskName()
    {
        var script = DefenderService.BuildScheduleScript(60);

        Assert.Contains("AddMinutes(60)", script);
        Assert.Contains("-AtStartup", script);        // khởi động lại máy cũng tự bật lại
        Assert.Contains("'SYSTEM'", script);
        Assert.Contains("PCTool-ReEnableDefender", script);
    }

    [Fact]
    public void Parse_ResultText()
    {
        Assert.True(DefenderService.Parse(Ok("OK\r\n")).Ok);
        Assert.Equal("lý do", DefenderService.Parse(Ok("FAILED:lý do")).Message);
        Assert.False(DefenderService.Parse(new PowerShellResult(1, "", "lỗi lạ")).Ok);
    }
}
