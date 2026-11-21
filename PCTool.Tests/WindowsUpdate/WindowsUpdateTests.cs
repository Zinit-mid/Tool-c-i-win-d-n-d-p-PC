using PCTool.Core.Framework;
using PCTool.Core.Windows;
using PCTool.Core.WindowsUpdate;
using PCTool.Tests.Framework;

namespace PCTool.Tests.WindowsUpdate;

public sealed class WindowsUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pct-wu-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "update-timer.json");
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 14, 0, 0, TimeSpan.FromHours(7));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* bỏ qua */ } }

    /// <summary>Máy giả: nhớ chế độ dịch vụ, ghi lại mọi script đã chạy, có thể ép lỗi từng bước.</summary>
    private sealed class FakeMachine
    {
        public string WuMode = "Manual", UsoMode = "Manual";
        public bool ScheduleFails, PauseFails, PauseHasNoEffect;
        public readonly List<string> Scripts = new();

        public ScriptRunner Runner => (s, _) =>
        {
            Scripts.Add(s);
            if (s == WindowsUpdateService.StatusScript)
                return Task.FromResult(new PowerShellResult(0, $$"""{"wuMode":"{{WuMode}}","wuState":"Running","usoMode":"{{UsoMode}}"}""", ""));
            if (s.Contains("Register-ScheduledTask"))
                return Task.FromResult(new PowerShellResult(0, ScheduleFails ? "FAILED:không hẹn được" : "OK", ""));
            if (s == WindowsUpdateService.PauseScript)
            {
                if (PauseFails) return Task.FromResult(new PowerShellResult(0, "FAILED:Access denied", ""));
                if (!PauseHasNoEffect) { WuMode = "Disabled"; UsoMode = "Disabled"; }
                return Task.FromResult(new PowerShellResult(0, "OK", ""));
            }
            if (s.Contains("Set-Service -Name wuauserv -StartupType") && s.Contains("Start-Service"))
            {
                WuMode = s.Contains("wuauserv -StartupType Automatic") ? "Auto" : "Manual";
                UsoMode = s.Contains("UsoSvc -StartupType Automatic") ? "Auto" : "Manual";
                return Task.FromResult(new PowerShellResult(0, "OK", ""));
            }
            return Task.FromResult(new PowerShellResult(0, "OK", ""));
        };

        public int Count(Func<string, bool> f) => Scripts.Count(f);
        public int PauseCalls => Count(s => s == WindowsUpdateService.PauseScript);
        public int ScheduleCalls => Count(s => s.Contains("Register-ScheduledTask"));
    }

    private WindowsUpdateService Make(FakeMachine m) => new(m.Runner, StatePath);

    // ------------------------------------------------------------ Chế độ khởi động

    [Theory]
    [InlineData("Auto", "Automatic")]
    [InlineData("auto", "Automatic")]
    [InlineData("Manual", "Manual")]
    [InlineData("Disabled", "Manual")]
    [InlineData("'; calc; '", "Manual")]
    [InlineData(null, "Manual")]
    public void StartupType_WhitelistOnly(string? input, string expected) =>
        Assert.Equal(expected, WindowsUpdateService.ToStartupType(input));

    [Fact]
    public void Scripts_EmbedOnlyWhitelistedValues()
    {
        var s = WindowsUpdateService.BuildScheduleScript(360, "Automatic", "Manual");
        Assert.Contains("AddMinutes(360)", s);
        Assert.Contains("PCTool-ReEnableWindowsUpdate", s);
        Assert.Contains("wuauserv -StartupType Automatic", s);

        // Giá trị lạ bị đưa về Manual, không bao giờ nhúng nguyên văn.
        var evil = WindowsUpdateService.BuildResumeScript("Automatic'; calc; '", "x");
        Assert.DoesNotContain("calc", evil);
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsUpdateService.BuildScheduleScript(5, "Manual", "Manual"));
    }

    // ------------------------------------------------------------ Tạm dừng

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(61)]
    [InlineData(100000)]
    public async Task Pause_InvalidMinutes_RejectedWithoutAnyScript(int minutes)
    {
        var m = new FakeMachine();

        var r = await Make(m).PauseWithTimerAsync(minutes, Now, default);

        Assert.False(r.Ok);
        Assert.Empty(m.Scripts);
    }

    [Fact]
    public async Task Pause_Success_SchedulesFirst_ThenPauses_AndRemembersOriginalModes()
    {
        var m = new FakeMachine { WuMode = "Manual", UsoMode = "Auto" };

        var svc = Make(m);
        var r = await svc.PauseWithTimerAsync(60, Now, default);

        Assert.True(r.Ok);
        Assert.Contains("1 giờ", r.Message);
        int schedIdx = m.Scripts.FindIndex(s => s.Contains("Register-ScheduledTask"));
        int pauseIdx = m.Scripts.IndexOf(WindowsUpdateService.PauseScript);
        Assert.True(schedIdx >= 0 && pauseIdx > schedIdx, "Phải hẹn giờ bật lại trước khi tạm dừng.");
        var st = svc.LoadState()!;
        Assert.Equal("Manual", st.WuType);
        Assert.Equal("Automatic", st.UsoType);
        Assert.Equal(Now.AddMinutes(60), st.ReEnableAt);
    }

    [Fact]
    public async Task Pause_ScheduleFails_NothingPaused()
    {
        var m = new FakeMachine { ScheduleFails = true };

        var r = await Make(m).PauseWithTimerAsync(60, Now, default);

        Assert.False(r.Ok);
        Assert.Equal(0, m.PauseCalls);
        Assert.Equal("Manual", m.WuMode);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task Pause_Refused_RestoresOriginalModes_AndExplainsPolicy()
    {
        var m = new FakeMachine { UsoMode = "Auto", PauseFails = true };

        var svc = Make(m);
        var r = await svc.PauseWithTimerAsync(360, Now, default);

        Assert.False(r.Ok);
        Assert.Contains("chính sách", r.Message);
        Assert.Equal("Manual", m.WuMode);
        Assert.Equal("Auto", m.UsoMode); // đã trả đúng chế độ gốc
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task Pause_NoEffect_IsRolledBack_NotReportedAsSuccess()
    {
        var m = new FakeMachine { PauseHasNoEffect = true };

        var r = await Make(m).PauseWithTimerAsync(60, Now, default);

        Assert.False(r.Ok);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task Pause_AlreadyDisabled_Refused_NoScheduleCreated()
    {
        var m = new FakeMachine { WuMode = "Disabled" };

        var r = await Make(m).PauseWithTimerAsync(60, Now, default);

        Assert.False(r.Ok);
        Assert.Equal(0, m.ScheduleCalls);
        Assert.Equal(0, m.PauseCalls);
    }

    [Fact]
    public async Task Pause_CannotReadStatus_ChangesNothing()
    {
        var calls = new List<string>();
        var svc = new WindowsUpdateService((s, _) => { calls.Add(s); return Task.FromResult(new PowerShellResult(0, "", "")); }, StatePath);

        var r = await svc.PauseWithTimerAsync(60, Now, default);

        Assert.False(r.Ok);
        Assert.Single(calls);
    }

    // ------------------------------------------------------------ Bật lại

    [Fact]
    public async Task Resume_RestoresOriginalModes_AndDeletesState()
    {
        var m = new FakeMachine { UsoMode = "Auto" };
        var svc = Make(m);
        await svc.PauseWithTimerAsync(60, Now, default);
        Assert.Equal("Disabled", m.WuMode);

        var r = await svc.ResumeAsync(default);

        Assert.True(r.Ok);
        Assert.Equal("Manual", m.WuMode);
        Assert.Equal("Auto", m.UsoMode);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task Resume_WithoutState_FallsBackToManual()
    {
        var m = new FakeMachine { WuMode = "Disabled", UsoMode = "Disabled" };

        var r = await Make(m).ResumeAsync(default);

        Assert.True(r.Ok);
        Assert.Equal("Manual", m.WuMode);
    }

    [Fact]
    public void CorruptState_RenamedNotDeleted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ không phải json");

        Assert.Null(Make(new FakeMachine()).LoadState());

        Assert.True(File.Exists(StatePath + ".corrupt"));
    }

    // ------------------------------------------------------------ Qua FixExecutor

    [Fact]
    public async Task Fix_DryRun_And_SafeMode_NeverTouchMachine()
    {
        var m = new FakeMachine();
        var ex = new FixExecutor(new FakeRestorePoints());

        var a = await ex.RunAsync(new PauseWindowsUpdateFix(Make(m), 60), new FixOptions { DryRun = true }, null, default);
        var b = await ex.RunAsync(new PauseWindowsUpdateFix(Make(m), 60), new FixOptions { DryRun = false, SafeMode = true }, null, default);

        Assert.Equal(FixOutcome.Simulated, a.Outcome);
        Assert.Equal(FixOutcome.Blocked, b.Outcome);
        Assert.Empty(m.Scripts);
    }

    [Fact]
    public async Task Fix_Pause_Succeeds_AndVerifies()
    {
        var m = new FakeMachine();

        var r = await new FixExecutor(new FakeRestorePoints())
            .RunAsync(new PauseWindowsUpdateFix(Make(m), 1440, () => Now), new FixOptions { DryRun = false }, null, default);

        Assert.Equal(FixOutcome.Succeeded, r.Outcome);
        Assert.Equal("Disabled", m.WuMode);
    }

    [Fact]
    public void Fix_RejectsOtherDurations_AndWarnsAboutSecurityPatches()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PauseWindowsUpdateFix(Make(new FakeMachine()), 45));
        var fix = new PauseWindowsUpdateFix(Make(new FakeMachine()), 60);
        Assert.Contains(fix.Actions, a => a.Contains("bản vá bảo mật"));
        Assert.Contains(fix.Actions, a => a.Contains("tự bật lại"));
    }

    // ------------------------------------------------------------ Phân tích

    [Fact]
    public void Analyzer_Normal_NoFindings() =>
        Assert.Empty(UpdatePauseAnalyzer.Analyze(new UpdateServiceStatus("Manual", "Running", "Manual"), null, Now, null));

    [Fact]
    public void Analyzer_PausedWithTimer_IsInfoWithResumeFix()
    {
        var st = new UpdatePauseState { OffAt = Now, ReEnableAt = Now.AddHours(1) };
        var f = Assert.Single(UpdatePauseAnalyzer.Analyze(new UpdateServiceStatus("Disabled", "Stopped", "Disabled"), st, Now, new ResumeWindowsUpdateFix(Make(new FakeMachine()))));

        Assert.Equal("winupdate.paused", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Single(f.Fixes);
    }

    [Fact]
    public void Analyzer_Overdue_And_NoTimer_AreWarnings()
    {
        var disabled = new UpdateServiceStatus("Disabled", "Stopped", "Manual");
        var overdue = Assert.Single(UpdatePauseAnalyzer.Analyze(disabled, new UpdatePauseState { ReEnableAt = Now.AddMinutes(-5) }, Now, null));
        var noTimer = Assert.Single(UpdatePauseAnalyzer.Analyze(disabled, null, Now, null));

        Assert.Equal("winupdate.off-overdue", overdue.Id);
        Assert.Equal(Severity.Warning, overdue.Severity);
        Assert.Equal("winupdate.off", noTimer.Id);
        Assert.Contains("công ty", noTimer.Cause);
    }

    [Theory]
    [InlineData("")]
    [InlineData("không phải json")]
    [InlineData("[1,2]")]
    public void Status_BadInput_IsUnknown(string json) => Assert.Null(UpdateServiceStatus.Parse(json).IsPaused);
}
