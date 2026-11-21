using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Optimize;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Optimize;

public sealed class SystemChecksTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static IFix NoFix() => new FakeFix();
    private static ScriptRunner Returns(string output, int code = 0) => (_, _) => Task.FromResult(new PowerShellResult(code, output, ""));

    // ------------------------------------------------------------ RAM / CPU

    [Theory]
    [InlineData(8000000, 4000000, 0)]   // 50% dùng
    [InlineData(8000000, 1600000, 1)]   // 80% -> thông tin
    [InlineData(8000000, 400000, 1)]    // 95% -> cảnh báo
    public void Memory_Thresholds(long total, long free, int expectedCount) =>
        Assert.Equal(expectedCount, MemoryCheck.Evaluate(J($$"""{"totalKb":{{total}},"freeKb":{{free}},"cpu":10}""")).Count);

    [Fact]
    public void Memory_Critical_NamesBiggestProcesses_NoFix()
    {
        var f = Assert.Single(MemoryCheck.Evaluate(J("""
            {"totalKb":8000000,"freeKb":300000,"cpu":5,"top":[{"Name":"chrome","MemMB":2100},{"Name":"zalo","MemMB":800}]}
            """)));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("chrome (2100 MB)", f.Cause);
        Assert.Empty(f.Fixes);   // đóng chương trình nào là việc của người dùng
    }

    [Fact]
    public void Memory_SingleTopObject_IsAccepted() =>
        Assert.Contains("chrome", Assert.Single(MemoryCheck.Evaluate(J("""{"totalKb":100,"freeKb":1,"top":{"Name":"chrome","MemMB":5}}"""))).Cause);

    [Fact]
    public void Cpu_High_IsInfoOnly()
    {
        var f = Assert.Single(MemoryCheck.Evaluate(J("""{"totalKb":8000000,"freeKb":6000000,"cpu":97}""")));

        Assert.Equal("cpu.busy", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
    }

    [Fact]
    public void Memory_MissingData_NoFindings_NoCrash() =>
        Assert.Empty(MemoryCheck.Evaluate(J("{}")));

    // ------------------------------------------------------------ Khởi động

    private static string Startup(int n) =>
        "{\"items\":[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"Name\":\"App{i}\",\"Location\":\"HKLM\"}}")) + "]}";

    [Theory]
    [InlineData(9, 0, Severity.Info)]
    [InlineData(10, 1, Severity.Info)]
    [InlineData(14, 1, Severity.Info)]
    [InlineData(15, 1, Severity.Warning)]
    public void Startup_Thresholds(int n, int count, Severity sev)
    {
        var list = StartupCheck.Evaluate(J(Startup(n)));

        Assert.Equal(count, list.Count);
        if (count > 0) Assert.Equal(sev, list[0].Severity);
    }

    [Fact]
    public void Startup_DuplicatesAreCountedOnce_AndNoAutoFix()
    {
        var items = string.Join(",", Enumerable.Range(0, 20).Select(_ => "{\"Name\":\"Same\"}"));

        Assert.Empty(StartupCheck.Evaluate(J("{\"items\":[" + items + "]}")));
        Assert.Empty(StartupCheck.Evaluate(J(Startup(12)))[0].Fixes);
    }

    // ------------------------------------------------------------ Mạng

    [Fact]
    public void Network_DnsWorks_NoFinding_EvenIfPingBlocked() =>
        Assert.Empty(NetworkCheck.Evaluate(J("""{"adapters":[{"InterfaceAlias":"Wi-Fi","Gateway":"192.168.1.1"}],"gatewayOk":false,"internetIpOk":false,"dnsOk":true}"""), NoFix));

    [Fact]
    public void Network_NoAdapter_IsCritical()
    {
        var f = Assert.Single(NetworkCheck.Evaluate(J("""{"adapters":[],"gatewayOk":false,"internetIpOk":false,"dnsOk":false}"""), NoFix));

        Assert.Equal("network.none", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public void Network_GatewayDown() =>
        Assert.Equal("network.gateway", Assert.Single(NetworkCheck.Evaluate(
            J("""{"adapters":[{"InterfaceAlias":"Wi-Fi","Gateway":"192.168.1.1"}],"gatewayOk":false,"internetIpOk":false,"dnsOk":false}"""), NoFix)).Id);

    [Fact]
    public void Network_NoInternetBehindRouter() =>
        Assert.Equal("network.internet", Assert.Single(NetworkCheck.Evaluate(
            J("""{"adapters":{"InterfaceAlias":"Wi-Fi","Gateway":"192.168.1.1"},"gatewayOk":true,"internetIpOk":false,"dnsOk":false}"""), NoFix)).Id);

    [Fact]
    public void Network_DnsOnlyFailure_OffersFlushDns()
    {
        var f = Assert.Single(NetworkCheck.Evaluate(
            J("""{"adapters":[{"InterfaceAlias":"Wi-Fi","Gateway":"192.168.1.1"}],"gatewayOk":true,"internetIpOk":true,"dnsOk":false}"""), () => new FlushDnsFix()));

        Assert.Equal("network.dns", f.Id);
        Assert.IsType<FlushDnsFix>(Assert.Single(f.Fixes));
    }

    [Fact]
    public async Task FlushDns_DryRun_RunsNothing_RealRunVerifies()
    {
        int calls = 0;
        ScriptRunner run = (s, _) => { calls++; return Task.FromResult(new PowerShellResult(0, s == FlushDnsFix.VerifyScript ? "OK" : "", "")); };
        var executor = new FixExecutor(new FakeRestorePoints());

        var dry = await executor.RunAsync(new FlushDnsFix(run), new FixOptions { DryRun = true }, null, CancellationToken.None);
        Assert.Equal(FixOutcome.Simulated, dry.Outcome);
        Assert.Equal(0, calls);

        var real = await executor.RunAsync(new FlushDnsFix(run), new FixOptions { DryRun = false }, null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, real.Outcome);
    }

    [Fact]
    public async Task FlushDns_StillFailing_IsStillPresent()
    {
        ScriptRunner run = (s, _) => Task.FromResult(new PowerShellResult(0, s == FlushDnsFix.VerifyScript ? "FAIL" : "", ""));

        var r = await new FixExecutor(new FakeRestorePoints())
            .RunAsync(new FlushDnsFix(run), new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.StillPresent, r.Outcome);
    }

    // ------------------------------------------------------------ Windows Update

    [Fact]
    public void Update_Healthy_NoFindings() =>
        Assert.Empty(WindowsUpdateCheck.Evaluate(J("""{"updateStartMode":"Manual","pendingReboot":false,"systemFreeBytes":50000000000}"""), NoFix));

    [Fact]
    public void Update_Disabled_OffersFix_AndMentionsCompanyPolicy()
    {
        var f = Assert.Single(WindowsUpdateCheck.Evaluate(J("""{"updateStartMode":"Disabled","pendingReboot":false,"systemFreeBytes":50000000000}"""), () => new StartUpdateServiceFix()));

        Assert.Equal("update.disabled", f.Id);
        Assert.Contains("công ty", f.Cause);
        Assert.IsType<StartUpdateServiceFix>(Assert.Single(f.Fixes));
    }

    [Fact]
    public void Update_PendingReboot_AndLowDisk()
    {
        var ids = WindowsUpdateCheck.Evaluate(J("""{"updateStartMode":"Auto","pendingReboot":true,"systemFreeBytes":3000000000}"""), NoFix).Select(f => f.Id).ToArray();

        Assert.Equal(new[] { "update.reboot-pending", "update.low-disk" }, ids);
    }

    [Fact]
    public async Task StartUpdateService_VerifyFailsIfStillDisabled()
    {
        ScriptRunner run = (s, _) => Task.FromResult(new PowerShellResult(0, s == StartUpdateServiceFix.VerifyScript ? "Disabled" : "", ""));

        var r = await new FixExecutor(new FakeRestorePoints())
            .RunAsync(new StartUpdateServiceFix(run), new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.StillPresent, r.Outcome);
    }

    [Fact]
    public async Task StartUpdateService_SafeMode_Blocked()
    {
        int calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); };

        var r = await new FixExecutor(new FakeRestorePoints())
            .RunAsync(new StartUpdateServiceFix(run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Blocked, r.Outcome);
        Assert.Equal(0, calls);
    }

    // ------------------------------------------------------------ Chung

    [Fact]
    public async Task Check_UsesRunner_AndParses()
    {
        var f = Assert.Single(await new MemoryCheck(Returns("""{"totalKb":100,"freeKb":1}""")).RunAsync(CancellationToken.None));

        Assert.Equal("memory.critical", f.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("không phải json")]
    [InlineData("[1]")]
    public async Task Check_BadOutput_ThrowsClearly_SoScanRunnerCanIsolateIt(string output)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NetworkCheck(Returns(output)).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanRunner_BadCheckDoesNotStopOthers()
    {
        var checks = new ICheck[] { new MemoryCheck(Returns("hỏng")), new StartupCheck(Returns(Startup(16))) };

        var report = await ScanRunner.RunAsync(checks, null, CancellationToken.None);

        Assert.Single(report.Failures);
        Assert.Equal("startup.many", Assert.Single(report.Findings).Id);
    }

    [Fact]
    public void Scripts_AreReadOnly_ExceptFixes()
    {
        foreach (var s in new[] { MemoryCheck.ScriptText, StartupCheck.ScriptText, NetworkCheck.ScriptText, WindowsUpdateCheck.ScriptText })
            foreach (var bad in new[] { "Set-", "Remove-", "Stop-", "Start-", "Restart-", "New-Item", "Clear-", "Invoke-", "Out-File", "netsh", "ipconfig /release" })
                Assert.DoesNotContain(bad, s);
    }
}
