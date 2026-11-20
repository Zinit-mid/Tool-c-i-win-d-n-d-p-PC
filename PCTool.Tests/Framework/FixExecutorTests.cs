using PCTool.Core.Framework;

namespace PCTool.Tests.Framework;

public sealed class FixExecutorTests
{
    private static readonly FixOptions Real = new() { DryRun = false };

    private static (FixExecutor Executor, FakeRestorePoints RestorePoints) Create(FakeRestorePoints? rp = null)
    {
        rp ??= new FakeRestorePoints();
        return (new FixExecutor(rp), rp);
    }

    [Fact]
    public void DefaultOptions_AreDryRun_AndSafeModeOff()
    {
        var options = new FixOptions();
        Assert.True(options.DryRun);
        Assert.False(options.SafeMode);
        Assert.True(options.CreateRestorePoint);
    }

    [Fact]
    public async Task DryRun_NeverExecutesNorCreatesRestorePoint_AndReportsActions()
    {
        var (executor, rp) = Create();
        var fix = new FakeFix { RequiresRestorePoint = true };
        var progress = new ListProgress<string>();

        var result = await executor.RunAsync(fix, new FixOptions { DryRun = true }, progress, CancellationToken.None);

        Assert.Equal(FixOutcome.Simulated, result.Outcome);
        Assert.Equal(0, fix.ExecuteCalls);
        Assert.Equal(0, fix.VerifyCalls);
        Assert.Equal(0, rp.Calls);
        Assert.Contains(progress.Items, s => s.Contains("Việc 1"));
        Assert.Contains(progress.Items, s => s.Contains("Việc 2"));
    }

    [Fact]
    public async Task SafeMode_BlocksEvenRealRun()
    {
        var (executor, rp) = Create();
        var fix = new FakeFix { RequiresRestorePoint = true };

        var result = await executor.RunAsync(
            fix, new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Blocked, result.Outcome);
        Assert.Equal(0, fix.ExecuteCalls);
        Assert.Equal(0, rp.Calls);
    }

    [Fact]
    public async Task RealRun_Success_VerifiesAndReportsSucceeded()
    {
        var (executor, rp) = Create();
        var fix = new FakeFix { RequiresRestorePoint = true };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, fix.ExecuteCalls);
        Assert.Equal(1, fix.VerifyCalls);
        Assert.Equal(1, rp.Calls);
        Assert.Contains(fix.Title, rp.LastDescription);
    }

    [Fact]
    public async Task RealRun_FixNotRequiringRestorePoint_DoesNotCreateOne()
    {
        var (executor, rp) = Create();
        var fix = new FakeFix { RequiresRestorePoint = false };

        await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(0, rp.Calls);
    }

    [Fact]
    public async Task RealRun_UserDisabledRestorePoint_DoesNotCreateOne()
    {
        var (executor, rp) = Create();
        var fix = new FakeFix { RequiresRestorePoint = true };

        var result = await executor.RunAsync(
            fix, new FixOptions { DryRun = false, CreateRestorePoint = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, rp.Calls);
    }

    [Fact]
    public async Task VerifyFalse_ReportsStillPresent_NotSucceeded()
    {
        var (executor, _) = Create();
        var fix = new FakeFix { OnVerify = () => false };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.StillPresent, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task VerifyThrows_ReportsUnverified_NotSucceeded()
    {
        var (executor, _) = Create();
        var fix = new FakeFix { OnVerify = () => throw new InvalidOperationException("hỏng") };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Unverified, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RestorePointFailed_AndFixNotUndoable_AbortsWithoutExecuting()
    {
        var rp = new FakeRestorePoints { Result = new(RestorePointStatus.Failed, "Bảo vệ hệ thống đang tắt") };
        var (executor, _) = Create(rp);
        var fix = new FakeFix { RequiresRestorePoint = true, IsUndoable = false };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(0, fix.ExecuteCalls);
        Assert.Contains("Bảo vệ hệ thống đang tắt", result.Message);
    }

    [Fact]
    public async Task RestorePointFailed_ButFixUndoable_StillExecutes()
    {
        var rp = new FakeRestorePoints { Result = new(RestorePointStatus.Failed, "lỗi") };
        var (executor, _) = Create(rp);
        var fix = new FakeFix { RequiresRestorePoint = true, IsUndoable = true };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, fix.ExecuteCalls);
    }

    [Fact]
    public async Task RestorePointSkipped_StillExecutes()
    {
        var rp = new FakeRestorePoints { Result = new(RestorePointStatus.Skipped, "đã có điểm trong 24 giờ") };
        var (executor, _) = Create(rp);
        var fix = new FakeFix { RequiresRestorePoint = true, IsUndoable = false };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, fix.ExecuteCalls);
    }

    [Fact]
    public async Task ExecuteThrows_ReturnsFailed_AndDoesNotVerify()
    {
        var (executor, _) = Create();
        var fix = new FakeFix { OnExecute = () => throw new InvalidOperationException("không có quyền") };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Contains("không có quyền", result.Message);
        Assert.Equal(0, fix.VerifyCalls);
    }

    [Fact]
    public async Task ExecuteReturnsFailed_IsReturnedAsIs_WithoutVerify()
    {
        var (executor, _) = Create();
        var fix = new FakeFix { OnExecute = () => FixResult.Failed("dịch vụ không khởi động") };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(0, fix.VerifyCalls);
    }

    [Fact]
    public async Task ExecuteCancelled_ReturnsCancelled()
    {
        var (executor, _) = Create();
        var fix = new FakeFix { OnExecute = () => throw new OperationCanceledException() };

        var result = await executor.RunAsync(fix, Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, fix.VerifyCalls);
    }

    [Fact]
    public async Task AlreadyCancelledToken_DoesNotExecute()
    {
        var (executor, _) = Create();
        var fix = new FakeFix();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.RunAsync(fix, Real, null, cts.Token);

        Assert.Equal(FixOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, fix.ExecuteCalls);
    }
}
