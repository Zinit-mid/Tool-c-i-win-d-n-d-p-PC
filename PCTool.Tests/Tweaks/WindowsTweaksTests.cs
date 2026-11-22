using PCTool.Core.Framework;
using PCTool.Core.Tweaks;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Tweaks;

public sealed class WindowsTweaksTests
{
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };
    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));

    [Fact]
    public void Catalog_Is_Safe_And_Consistent()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in TweakCatalog.All)
        {
            Assert.StartsWith(@"HKCU:\", t.Key);                 // chỉ tài khoản đang dùng
            Assert.NotEqual(t.OnValue, t.OffValue);
            Assert.DoesNotContain("'", t.Key + t.Id);
            foreach (var bad in new[] { "Policies", "Defender", "System\\", "SmartScreen", "Firewall", "UAC", "Security", "Notifications" })
                Assert.DoesNotContain(bad, t.Key + t.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Unknown_Tweak_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new ApplyTweakFix("../../x", true));
        Assert.Throws<ArgumentException>(() => new ApplyTweakFix("uac", false));
    }

    [Fact]
    public void ParseStates_Handles_Array_Missing_Unknown_And_Garbage()
    {
        var s = TweakCatalog.ParseStates("""[{"id":"hideext","value":0},{"id":"launchto","value":2},{"id":"taskview","value":null},{"id":"darkapps","value":7},{"id":"zzz","value":1}]""");
        Assert.Equal(TweakCatalog.State.On, s["hideext"]);
        Assert.Equal(TweakCatalog.State.Off, s["launchto"]);
        Assert.Equal(TweakCatalog.State.Default, s["taskview"]);
        Assert.Equal(TweakCatalog.State.Default, s["darkapps"]);
        Assert.False(s.ContainsKey("zzz"));
        Assert.Equal(TweakCatalog.State.On, TweakCatalog.ParseStates("""{"id":"hideext","value":0}""")["hideext"]);
        Assert.Throws<FormatException>(() => TweakCatalog.ParseStates("rác"));
        Assert.Throws<FormatException>(() => TweakCatalog.ParseStates("5"));
    }

    [Fact]
    public async Task Read_Script_Is_ReadOnly_And_Failure_Throws()
    {
        var s = TweakCatalog.ReadScript();
        Assert.DoesNotContain("Set-", s);
        Assert.DoesNotContain("Remove-", s);
        Assert.DoesNotContain("New-Item", s);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TweakCatalog.ReadAsync(Fake("", 1, "lỗi"), CancellationToken.None));
    }

    [Theory]
    [InlineData("hideext", true, "0")]
    [InlineData("hideext", false, "1")]
    [InlineData("launchto", true, "1")]
    public async Task Apply_Sets_And_Verifies(string id, bool on, string value)
    {
        var f = new ApplyTweakFix(id, on, (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("Set-ItemProperty") ? "" : value, "")));
        Assert.Contains("-Value " + value + " ", f.SetScript() + " ");
        Assert.DoesNotContain("Remove-", f.SetScript());
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(f, Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Apply_Wrong_Readback_Failure_DryRun_SafeMode()
    {
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(new ApplyTweakFix("hideext", true, Fake("1")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ApplyTweakFix("hideext", true, Fake("", 1, "bị khóa")), Real(), null, CancellationToken.None)).Outcome);
        var calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(new ApplyTweakFix("hideext", true, run), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(new ApplyTweakFix("hideext", true, run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(PowerPlan.Balanced, "381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData(PowerPlan.HighPerformance, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData(PowerPlan.PowerSaver, "a1841308-3541-4fab-bc81-f71556f20b4a")]
    public async Task PowerPlan_Uses_Fixed_Guid_And_Verifies(PowerPlan plan, string guid)
    {
        var f = new SetPowerPlanFix(plan, (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("getactivescheme") ? $"Power Scheme GUID: {guid.ToUpperInvariant()}  (x)" : "", "")));
        Assert.Contains("powercfg /setactive " + guid, f.SetScript());
        Assert.DoesNotContain("-delete", f.SetScript(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-duplicatescheme", f.SetScript(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(f, Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task PowerPlan_Missing_Or_Not_Active_Is_Reported()
    {
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new SetPowerPlanFix(PowerPlan.HighPerformance, Fake("", 1, "không có")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(new SetPowerPlanFix(PowerPlan.HighPerformance, Fake("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e")), Real(), null, CancellationToken.None)).Outcome);
    }
}
