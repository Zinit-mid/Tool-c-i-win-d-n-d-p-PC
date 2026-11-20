using PCTool.Core.Framework;

namespace PCTool.Tests.Framework;

public sealed class ScanRunnerTests
{
    [Fact]
    public async Task Findings_AreSortedMostSevereFirst()
    {
        var checks = new ICheck[]
        {
            new FakeCheck("a", () => new[] { Make.Finding("B nhẹ", Severity.Info), Make.Finding("A cảnh báo", Severity.Warning) }),
            new FakeCheck("b", () => new[] { Make.Finding("C nặng", Severity.Critical) })
        };

        var report = await ScanRunner.RunAsync(checks, null, CancellationToken.None);

        Assert.Equal(
            new[] { Severity.Critical, Severity.Warning, Severity.Info },
            report.Findings.Select(f => f.Severity).ToArray());
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task OneCheckThrowing_DoesNotStopTheOthers_AndIsRecorded()
    {
        var checks = new ICheck[]
        {
            new FakeCheck("tot-1", () => new[] { Make.Finding("Vấn đề 1", Severity.Warning) }),
            new FakeCheck("hong", () => throw new InvalidOperationException("không đọc được")),
            new FakeCheck("tot-2", () => new[] { Make.Finding("Vấn đề 2", Severity.Info) })
        };

        var report = await ScanRunner.RunAsync(checks, null, CancellationToken.None);

        Assert.Equal(2, report.Findings.Count);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("hong", failure.CheckId);
        Assert.Contains("không đọc được", failure.Message);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var checks = new ICheck[] { new FakeCheck("a", () => Array.Empty<Finding>()) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ScanRunner.RunAsync(checks, null, cts.Token));
    }

    [Fact]
    public async Task Progress_IsReportedForEveryCheck_InOrder()
    {
        var checks = new ICheck[]
        {
            new FakeCheck("a", () => Array.Empty<Finding>()),
            new FakeCheck("b", () => Array.Empty<Finding>())
        };
        var progress = new ListProgress<ScanProgress>();

        await ScanRunner.RunAsync(checks, progress, CancellationToken.None);

        Assert.Equal(2, progress.Items.Count);
        Assert.Equal(new[] { 0, 1 }, progress.Items.Select(p => p.Index).ToArray());
        Assert.All(progress.Items, p => Assert.Equal(2, p.Total));
    }

    [Fact]
    public async Task NoChecks_ReturnsEmptyReport()
    {
        var report = await ScanRunner.RunAsync(Array.Empty<ICheck>(), null, CancellationToken.None);

        Assert.Empty(report.Findings);
        Assert.Empty(report.Failures);
    }
}
