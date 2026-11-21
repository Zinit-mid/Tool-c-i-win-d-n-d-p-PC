using PCTool.Core.Framework;
using PCTool.Core.Printer;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Printer;

public sealed class PrinterFrameworkTests
{
    private sealed class Runner
    {
        public List<string> Calls { get; } = new();
        public Func<string, PowerShellResult> Respond { get; set; } = _ => new(0, "", "");

        public Task<PowerShellResult> Run(string script, CancellationToken ct)
        {
            Calls.Add(script);
            return Task.FromResult(Respond(script));
        }
    }

    private static FixExecutor Executor() => new(new FakeRestorePoints());

    [Fact]
    public async Task RestartSpooler_DryRun_RunsNothing()
    {
        var r = new Runner();

        var result = await Executor().RunAsync(new RestartSpoolerFix(r.Run), new FixOptions { DryRun = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Simulated, result.Outcome);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task RestartSpooler_SafeMode_Blocked()
    {
        var r = new Runner();

        var result = await Executor().RunAsync(new RestartSpoolerFix(r.Run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Blocked, result.Outcome);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task RestartSpooler_Success_VerifiesRunning()
    {
        var r = new Runner { Respond = s => s == RestartSpoolerFix.StatusScript ? new(0, "Running\r\n", "") : new(0, "", "") };

        var result = await Executor().RunAsync(new RestartSpoolerFix(r.Run), new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task RestartSpooler_StillStopped_IsStillPresent()
    {
        var r = new Runner { Respond = s => s == RestartSpoolerFix.StatusScript ? new(0, "Stopped", "") : new(0, "", "") };

        var result = await Executor().RunAsync(new RestartSpoolerFix(r.Run), new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.StillPresent, result.Outcome);
    }

    [Fact]
    public async Task RestartSpooler_CommandFails_IsFailedWithReason()
    {
        var r = new Runner { Respond = _ => new(1, "", "Access denied") };

        var result = await Executor().RunAsync(new RestartSpoolerFix(r.Run), new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Contains("Access denied", result.Message);
    }

    [Fact]
    public async Task ClearQueue_ReportsCount_AndVerifiesEmptyAndRunning()
    {
        var r = new Runner
        {
            Respond = s => s == ClearPrintQueueFix.Script ? new(0, "3", "")
                         : s == ClearPrintQueueFix.VerifyScript ? new(0, "Running:0", "")
                         : new(0, "", "")
        };

        var result = await Executor().RunAsync(new ClearPrintQueueFix(r.Run), new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Contains("3 file", result.Message);
    }

    [Theory]
    [InlineData("Running:2")]
    [InlineData("Stopped:0")]
    [InlineData("rác")]
    public async Task ClearQueue_NotReallyCleared_IsStillPresent(string verifyOutput)
    {
        var r = new Runner
        {
            Respond = s => s == ClearPrintQueueFix.VerifyScript ? new(0, verifyOutput, "") : new(0, "1", "")
        };

        var result = await Executor().RunAsync(new ClearPrintQueueFix(r.Run), new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.StillPresent, result.Outcome);
    }

    [Fact]
    public void ClearQueue_Warns_AndIsNotUndoable()
    {
        var fix = new ClearPrintQueueFix();

        Assert.Contains(fix.Actions, a => a.Contains("TOÀN BỘ"));
        Assert.False(fix.IsUndoable);
    }

    [Fact]
    public void Mapper_ConvertsLevels_AttachesFixesByCode_SkipsOk()
    {
        var report = new PrinterReport();
        report.Findings.Add(new PrinterFinding { Level = "Error", Code = FindingCodes.SpoolerStopped, Problem = "Spooler không chạy", Advice = "Khởi động lại" });
        report.Findings.Add(new PrinterFinding { Level = "Warn", Code = FindingCodes.QueueStuck, Printer = "HP", Problem = "Lệnh in kẹt", Advice = "Xóa hàng đợi" });
        report.Findings.Add(new PrinterFinding { Level = "Warn", Printer = "HP", Problem = "Driver chung", Advice = "Cài driver" });
        report.Findings.Add(new PrinterFinding { Level = "Ok", Problem = "Mọi thứ ổn" });

        var list = PrinterFindingMapper.Map(report, () => new RestartSpoolerFix(), () => new ClearPrintQueueFix());

        Assert.Equal(3, list.Count);
        Assert.Equal(Severity.Critical, list[0].Severity);
        Assert.IsType<RestartSpoolerFix>(Assert.Single(list[0].Fixes));
        Assert.Equal("HP: Lệnh in kẹt", list[1].Title);
        Assert.IsType<ClearPrintQueueFix>(Assert.Single(list[1].Fixes));
        Assert.Empty(list[2].Fixes);
        Assert.Equal(list.Count, list.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public async Task Check_UsesDiagnosisAndMapper()
    {
        var report = new PrinterReport();
        report.Findings.Add(new PrinterFinding { Level = "Error", Code = FindingCodes.SpoolerStopped, Problem = "x" });
        var check = new PrinterCheck(_ => Task.FromResult(report));

        var list = await check.RunAsync(CancellationToken.None);

        Assert.Single(list);
        Assert.Single(list[0].Fixes);
    }
}
