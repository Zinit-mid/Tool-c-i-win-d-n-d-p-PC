using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;
using PCTool.Core.Restore;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Restore;

public sealed class ZaloDriverRestoreTests
{
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };
    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));

    private sealed class Ok : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, "", ""));
    }

    // ------------------------------------------------------------ Driver: kiểm tra từng driver

    private static DriverRestoreFix Drv(ScriptRunner run, params string[] names) =>
        new(@"D:\B\drv", new Ok(), _ => names.Length, run, _ => names);

    [Fact]
    public async Task Driver_All_Present_Succeeds()
    {
        var f = Drv(Fake("netwtw10.inf\r\nOEM1.INF\r\nother.inf\r\n"), "netwtw10.inf", "oem1.inf");
        var res = await Exec().RunAsync(f, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Empty(f.Missing);
    }

    [Fact]
    public async Task Driver_One_Missing_Is_StillPresent_And_Named()
    {
        var f = Drv(Fake("a.inf\n"), "a.inf", "b.inf");
        var res = await Exec().RunAsync(f, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.StillPresent, res.Outcome);
        Assert.Equal(new[] { "b.inf" }, f.Missing);
    }

    [Fact]
    public async Task Driver_List_Failure_Is_Unverified_Not_Success()
    {
        var res = await Exec().RunAsync(Drv(Fake("", 1, "lỗi"), "a.inf"), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Unverified, res.Outcome);
    }

    [Fact]
    public void Driver_List_Script_Is_ReadOnly()
    {
        var s = DriverRestoreFix.BuildListScript();
        Assert.Contains("Get-WindowsDriver", s);
        Assert.DoesNotContain("Remove-", s);
        Assert.DoesNotContain("pnputil", s, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ Zalo

    private static string R(params (string Name, int Src, int Dest, int Exit)[] f) =>
        "{\"folders\":[" + string.Join(",", f.Select(x => $"{{\"name\":\"{x.Name}\",\"srcFiles\":{x.Src},\"destFiles\":{x.Dest},\"exit\":{x.Exit}}}")) + "]}";

    [Theory]
    [InlineData(@"C:\Windows\x")]
    [InlineData(@"D:\a'; calc; '")]
    [InlineData("relative")]
    public void Zalo_Bad_Path_Rejected(string p) => Assert.Throws<ArgumentException>(() => new ZaloRestoreFix(p));

    [Fact]
    public void Zalo_Script_Never_Overwrites_Or_Deletes()
    {
        var s = new ZaloRestoreFix(@"D:\B\Zalo\").BuildScript();
        Assert.Contains("/XC /XN /XO", s);
        Assert.Contains(@"D:\B\Zalo'", s);
        Assert.DoesNotContain("/MIR", s);
        Assert.DoesNotContain("/PURGE", s);
        Assert.DoesNotContain("Remove-Item", s);
        Assert.DoesNotContain("-Force -Recurse", s);
        Assert.Contains("'RUNNING'", s);
    }

    [Fact]
    public async Task Zalo_Success_And_Counts()
    {
        var res = await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", Fake(R(("123456789", 10, 12, 1)))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
    }

    [Theory]
    [InlineData("RUNNING", "đang chạy")]
    [InlineData("NOSRC", "toàn số")]
    [InlineData("NODEST", "cài Zalo")]
    public async Task Zalo_Markers_Fail_With_Clear_Message(string marker, string text)
    {
        var res = await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", Fake(marker + "\n")), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains(text, res.Message);
    }

    [Fact]
    public async Task Zalo_Missing_Files_Or_Robocopy_Error_Fails()
    {
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", Fake(R(("123456789", 10, 9, 1)))), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", Fake(R(("123456789", 10, 10, 8)))), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", Fake("", 1, "lỗi")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", Fake("rác")), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Zalo_DryRun_And_SafeMode_Do_Not_Run()
    {
        var calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", run), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(new ZaloRestoreFix(@"D:\B\Zalo", run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, calls);
    }
}
