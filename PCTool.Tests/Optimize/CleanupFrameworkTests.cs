using PCTool.Core.Framework;
using PCTool.Core.Optimize;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Optimize;

/// <summary>Chạy trên thư mục tạm riêng, không đụng dữ liệu thật của máy.</summary>
public sealed class CleanupFrameworkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pctool-test-" + Guid.NewGuid().ToString("N"));

    public CleanupFrameworkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private CleanupItem Item(string id = "t") => new() { Id = id, Name = "Thử " + id, Description = "Thư mục thử", Paths = new[] { _root } };

    private void Write(string name, int bytes) => File.WriteAllBytes(Path.Combine(_root, name), new byte[bytes]);

    private static FixExecutor Executor() => new(new FakeRestorePoints());

    private static CleanupItem Scanned(CleanupItem item)
    {
        CleanupScanner.Scan(item, CancellationToken.None);
        return item;
    }

    [Fact]
    public void Actions_NameTheFolderAndSize()
    {
        Write("a.tmp", 2048);
        var fix = new CleanupFix(Scanned(Item()));

        Assert.Contains(fix.Actions, a => a.Contains(_root) && a.Contains("2 KB"));
        Assert.False(fix.RequiresRestorePoint);
        Assert.False(fix.IsUndoable);
    }

    [Fact]
    public async Task DryRun_DeletesNothing()
    {
        Write("a.tmp", 100);
        var fix = new CleanupFix(Scanned(Item()));

        var result = await Executor().RunAsync(fix, new FixOptions { DryRun = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Simulated, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_root, "a.tmp")));
    }

    [Fact]
    public async Task SafeMode_DeletesNothing()
    {
        Write("a.tmp", 100);
        var fix = new CleanupFix(Scanned(Item()));

        var result = await Executor().RunAsync(fix, new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Blocked, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_root, "a.tmp")));
    }

    [Fact]
    public async Task RealRun_DeletesFiles_AndVerifies()
    {
        Write("a.tmp", 100);
        Write("b.tmp", 200);
        var item = Scanned(Item());

        var result = await Executor().RunAsync(new CleanupFix(item), new FixOptions { DryRun = false, CreateRestorePoint = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Empty(Directory.GetFiles(_root));
        Assert.Equal(0, item.SizeBytes);
    }

    [Fact]
    public async Task RealRun_OnlyTouchesItsOwnFolder()
    {
        var other = Path.Combine(Path.GetTempPath(), "pctool-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            File.WriteAllBytes(Path.Combine(other, "keep.txt"), new byte[10]);
            Write("a.tmp", 100);

            await Executor().RunAsync(new CleanupFix(Scanned(Item())), new FixOptions { DryRun = false }, null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
        }
        finally { Directory.Delete(other, true); }
    }

    [Fact]
    public async Task Check_ReportsOnlyNonEmptyItems_AndDoesNotDelete()
    {
        Write("a.tmp", 5000);
        var empty = Path.Combine(_root, "empty-root");
        var items = new List<CleanupItem>
        {
            Item("has-files"),
            new() { Id = "none", Name = "Trống", Description = "d", Paths = new[] { empty } }
        };

        var findings = await new CleanupCheck(() => items).RunAsync(CancellationToken.None);

        var f = Assert.Single(findings);
        Assert.Equal("cleanup.has-files", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.IsType<CleanupFix>(Assert.Single(f.Fixes));
        Assert.True(File.Exists(Path.Combine(_root, "a.tmp")));
    }

    [Fact]
    public async Task Check_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CleanupCheck(() => new[] { Item() }).RunAsync(cts.Token));
    }

    [Fact]
    public void BuildItems_HaveUniqueIds()
    {
        var ids = CleanupScanner.BuildItems().Select(i => i.Id).ToList();

        Assert.All(ids, id => Assert.NotEmpty(id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
