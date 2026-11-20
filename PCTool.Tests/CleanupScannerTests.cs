using System.Globalization;
using PCTool.Core.Optimize;

namespace PCTool.Tests;

/// <summary>Kiểm thử logic dọn rác trên thư mục tạm riêng, không đụng dữ liệu thật của máy.</summary>
public sealed class CleanupScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pctool-test-" + Guid.NewGuid().ToString("N"));

    public CleanupScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* thư mục thử đã dọn hoặc đang bị khóa, bỏ qua */ }
    }

    private CleanupItem MakeItem() => new()
    {
        Name = "Test",
        Description = "Thư mục thử",
        Paths = new[] { _root }
    };

    private void WriteFile(string relativePath, int bytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void FormatSize_ReturnsReadableText(long bytes, string expected)
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, CleanupItem.FormatSize(bytes)); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Scan_CountsFilesAndSize_IncludingSubfolders()
    {
        WriteFile("a.tmp", 100);
        WriteFile(Path.Combine("sub", "b.tmp"), 50);
        var item = MakeItem();

        CleanupScanner.Scan(item, CancellationToken.None);

        Assert.Equal(2, item.FileCount);
        Assert.Equal(150, item.SizeBytes);
    }

    [Fact]
    public void Scan_MissingFolder_ReturnsZeroWithoutError()
    {
        var item = new CleanupItem
        {
            Name = "Không có",
            Description = "Thư mục không tồn tại",
            Paths = new[] { Path.Combine(_root, "khong-co") }
        };

        CleanupScanner.Scan(item, CancellationToken.None);

        Assert.Equal(0, item.FileCount);
        Assert.Equal(0, item.SizeBytes);
    }

    [Fact]
    public void Clean_DryRun_ReportsButDeletesNothing()
    {
        WriteFile("a.tmp", 100);
        WriteFile(Path.Combine("sub", "b.tmp"), 50);

        var result = CleanupScanner.Clean(MakeItem(), dryRun: true, CancellationToken.None);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(150, result.FreedBytes);
        Assert.True(File.Exists(Path.Combine(_root, "a.tmp")));
        Assert.True(File.Exists(Path.Combine(_root, "sub", "b.tmp")));
    }

    [Fact]
    public void Clean_RealRun_DeletesFilesAndEmptySubfolders_ButKeepsRoot()
    {
        WriteFile("a.tmp", 100);
        WriteFile(Path.Combine("sub", "b.tmp"), 50);

        var result = CleanupScanner.Clean(MakeItem(), dryRun: false, CancellationToken.None);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.False(File.Exists(Path.Combine(_root, "a.tmp")));
        Assert.False(Directory.Exists(Path.Combine(_root, "sub")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Clean_SkipsLockedFile_AndStillDeletesTheRest()
    {
        WriteFile("a.tmp", 10);
        var lockedPath = Path.Combine(_root, "locked.tmp");
        using var locked = new FileStream(lockedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        locked.Write(new byte[20]);

        var result = CleanupScanner.Clean(MakeItem(), dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.False(File.Exists(Path.Combine(_root, "a.tmp")));
        Assert.True(File.Exists(lockedPath));
    }

    [Fact]
    public void Clean_WhenCancelled_Throws()
    {
        WriteFile("a.tmp", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CleanupScanner.Clean(MakeItem(), dryRun: true, cts.Token));
    }

    [Fact]
    public void BuildItems_DefaultsAreSafe()
    {
        var items = CleanupScanner.BuildItems();

        Assert.NotEmpty(items);
        // Thùng rác (mất vĩnh viễn) không được chọn sẵn.
        Assert.All(items.Where(i => i.IsRecycleBin), i => Assert.False(i.Selected));
        // Không mục nào trỏ vào thư mục người dùng như Documents/Desktop/Downloads.
        var forbidden = new[] { "Documents", "Desktop", "Downloads", "Pictures" };
        Assert.All(items.SelectMany(i => i.Paths), p =>
            Assert.DoesNotContain(forbidden, f => p.Contains(Path.DirectorySeparatorChar + f, StringComparison.OrdinalIgnoreCase)));
    }
}
