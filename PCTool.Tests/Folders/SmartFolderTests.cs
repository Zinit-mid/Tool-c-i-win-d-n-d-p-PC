using PCTool.Core.Folders;
using PCTool.Core.Framework;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Folders;

/// <summary>Dùng thư mục tạm nằm cạnh file test (không nằm trong AppData, vì FolderGuard chặn AppData).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "tmp-folder-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string rel, int size = 2048, byte fill = 1, DateTime? created = null, DateTime? modified = null)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, Enumerable.Repeat(fill, size).ToArray());
        if (created is { } c) System.IO.File.SetCreationTimeUtc(p, c);
        System.IO.File.SetLastWriteTime(p, modified ?? DateTime.Now.AddDays(-2));
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public sealed class FolderGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\")]
    [InlineData(@"D:")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"Downloads")]
    [InlineData(@"C:\Users\An\AppData\Local\Temp")]
    [InlineData(@"C:\Users\An\appdata")]
    public void Rejects_Bad_Paths(string? p) => Assert.False(FolderGuard.Check(p, new[] { @"C:\Windows" }, out _));

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\windows\system32")]
    [InlineData(@"C:\Program Files\App")]
    [InlineData(@"C:\ProgramData\x")]
    public void Rejects_System_Roots(string p) =>
        Assert.False(FolderGuard.Check(p, new[] { @"C:\Windows", @"C:\Program Files", @"C:\ProgramData" }, out _));

    [Fact]
    public void Does_Not_Confuse_Similar_Names()
    {
        using var t = new TempDir();
        var blocked = t.Path + "2";   // tên bắt đầu giống nhưng khác thư mục
        Assert.True(FolderGuard.Check(t.Path, new[] { blocked }, out var err), err);
    }

    [Fact]
    public void Rejects_Missing_And_Accepts_Existing()
    {
        using var t = new TempDir();
        Assert.True(FolderGuard.Check(t.Path, out var e1), e1);
        Assert.False(FolderGuard.Check(System.IO.Path.Combine(t.Path, "khong-co"), out _));
    }

    [Fact]
    public void IsInside_Uses_Folder_Boundary()
    {
        Assert.True(FolderGuard.IsInside(@"D:\A", @"D:\A\b.txt"));
        Assert.False(FolderGuard.IsInside(@"D:\A", @"D:\A2\b.txt"));
        Assert.False(FolderGuard.IsInside(@"D:\A", @"D:\A"));
    }
}

public sealed class DuplicateTests
{
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    [Fact]
    public void Scan_Finds_Groups_Keeps_Oldest_Skips_Small_And_Own_Folders()
    {
        using var t = new TempDir();
        var old = t.File("a.bin", fill: 7, created: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        t.File("sub\\copy.bin", fill: 7, created: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        t.File("other.bin", fill: 8);                          // cùng dung lượng, khác nội dung
        t.File("tiny1.txt", size: 10, fill: 3);                // quá nhỏ
        t.File("tiny2.txt", size: 10, fill: 3);
        t.File("_PCTool_Trung_x\\dup.bin", fill: 7);           // thư mục của PCTool: bỏ qua

        var groups = DuplicateScanner.Scan(t.Path, CancellationToken.None);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Files.Count);
        Assert.Equal(old, g.Keep.Path);
        Assert.Equal(2048, g.WastedBytes);
    }

    [Fact]
    public void Scan_Skips_Unreadable_Files()
    {
        using var t = new TempDir();
        t.File("a.bin", fill: 5); t.File("b.bin", fill: 5);
        var groups = DuplicateScanner.Scan(t.Path, CancellationToken.None, p => throw new IOException("đang mở"));
        Assert.Empty(groups);
    }

    [Fact]
    public async Task Move_Moves_Extras_Not_Keeper_And_Writes_Csv()
    {
        using var t = new TempDir();
        var a = t.File("a.bin", fill: 7, created: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = t.File("b.bin", fill: 7, created: new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var c = t.File("c.bin", fill: 7, created: new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var groups = DuplicateScanner.Scan(t.Path, CancellationToken.None);
        var fix = new DuplicateMoveFix(t.Path, groups, new DateTime(2026, 11, 21, 10, 0, 0));

        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(File.Exists(c));
        Assert.EndsWith("_PCTool_Trung_20261121-1000", fix.QuarantineFolder);
        Assert.Equal(2, Directory.GetFiles(fix.QuarantineFolder, "*.bin").Length);
        var csv = File.ReadAllText(Path.Combine(fix.QuarantineFolder, "vi-tri-cu.csv"));
        Assert.Contains(b, csv); Assert.Contains(c, csv);
    }

    [Fact]
    public async Task Move_Skips_File_Changed_After_Scan()
    {
        using var t = new TempDir();
        t.File("a.bin", fill: 7, created: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = t.File("b.bin", fill: 7, created: new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var groups = DuplicateScanner.Scan(t.Path, CancellationToken.None);
        File.AppendAllText(b, "sửa sau khi quét");

        var res = await Exec().RunAsync(new DuplicateMoveFix(t.Path, groups), Real(), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task Move_Does_Nothing_When_Keeper_Is_Gone()
    {
        using var t = new TempDir();
        var a = t.File("a.bin", fill: 7, created: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = t.File("b.bin", fill: 7, created: new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var groups = DuplicateScanner.Scan(t.Path, CancellationToken.None);
        File.Delete(a);

        var res = await Exec().RunAsync(new DuplicateMoveFix(t.Path, groups), Real(), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.True(File.Exists(b));   // không có bản giữ lại thì không đụng bản nào
    }

    [Fact]
    public async Task DryRun_And_SafeMode_Change_Nothing()
    {
        using var t = new TempDir();
        t.File("a.bin", fill: 7); var b = t.File("b.bin", fill: 7);
        var groups = DuplicateScanner.Scan(t.Path, CancellationToken.None);
        var fix = new DuplicateMoveFix(t.Path, groups);

        await Exec().RunAsync(fix, new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(fix, new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.True(File.Exists(b));
        Assert.False(Directory.Exists(fix.QuarantineFolder));
    }

    [Fact]
    public void Ctor_Rejects_Bad_Input()
    {
        using var t = new TempDir();
        t.File("a.bin", fill: 7); t.File("b.bin", fill: 7);
        var groups = DuplicateScanner.Scan(t.Path, CancellationToken.None);
        Assert.Throws<ArgumentException>(() => new DuplicateMoveFix(t.Path, Array.Empty<DuplicateGroup>()));
        Assert.Throws<ArgumentException>(() => new DuplicateMoveFix(@"C:\", groups));
        using var other = new TempDir();
        Assert.Throws<ArgumentException>(() => new DuplicateMoveFix(other.Path, groups));   // file ngoài thư mục đã chọn
        var one = new[] { new DuplicateGroup("h", 1, new[] { groups[0].Files[0] }) };
        Assert.Throws<ArgumentException>(() => new DuplicateMoveFix(t.Path, one));
    }

    [Fact]
    public void Actions_Say_Nothing_Is_Deleted()
    {
        using var t = new TempDir();
        t.File("a.bin", fill: 7); t.File("b.bin", fill: 7);
        var fix = new DuplicateMoveFix(t.Path, DuplicateScanner.Scan(t.Path, CancellationToken.None));
        Assert.Contains(fix.Actions, a => a.Contains("KHÔNG xóa"));
        Assert.False(fix.RequiresRestorePoint);
    }
}

public sealed class OrganizeTests
{
    private static readonly DateTime Now = DateTime.Now;
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    [Theory]
    [InlineData("a.JPG", FileCategory.Images)]
    [InlineData("b.pdf", FileCategory.Documents)]
    [InlineData("c.mkv", FileCategory.Videos)]
    [InlineData("d.mp3", FileCategory.Music)]
    [InlineData("e.zip", FileCategory.Archives)]
    [InlineData("f.exe", FileCategory.Installers)]
    [InlineData("g.xyz", FileCategory.Other)]
    [InlineData("noext", FileCategory.Other)]
    public void Categories(string name, FileCategory expect) => Assert.Equal(expect, FileCategories.Of(name));

    [Fact]
    public void Plan_Only_Top_Level_Known_Safe_Old_Files()
    {
        using var t = new TempDir();
        t.File("anh.jpg"); t.File("bai.docx"); t.File("sub\\trong.jpg");
        t.File("tat.lnk"); t.File("desktop.ini"); t.File("dang-tai.crdownload"); t.File("la.xyz");
        t.File("_PCTool_x.csv");
        t.File("moi.jpg", modified: DateTime.Now);                         // mới sửa: có thể đang tải
        var hidden = t.File("an.jpg"); File.SetAttributes(hidden, FileAttributes.Hidden);

        var plan = OrganizePlanner.Plan(t.Path, Now);

        Assert.Equal(new[] { "anh.jpg", "bai.docx" }, plan.Select(m => m.FileName).ToArray());
        Assert.Equal(new[] { "Ảnh", "Tài liệu" }, plan.Select(m => Path.GetFileName(m.DestFolder)).ToArray());
    }

    [Fact]
    public async Task Fix_Moves_Without_Overwriting_And_Writes_Csv()
    {
        using var t = new TempDir();
        var a = t.File("anh.jpg", fill: 1);
        var existing = t.File("Ảnh\\anh.jpg", fill: 9);     // đã có file cùng tên ở đích
        var plan = OrganizePlanner.Plan(t.Path, Now);
        var fix = new OrganizeFix(t.Path, plan, new DateTime(2026, 11, 21, 10, 0, 0));

        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.False(File.Exists(a));
        Assert.Equal(9, File.ReadAllBytes(existing)[0]);                       // file cũ nguyên vẹn
        Assert.True(File.Exists(Path.Combine(t.Path, "Ảnh", "anh (2).jpg")));  // bản mới thêm số
        Assert.Contains("anh.jpg", File.ReadAllText(fix.CsvPath));
    }

    [Fact]
    public async Task Fix_Skips_Changed_File_And_Dry_Run_Changes_Nothing()
    {
        using var t = new TempDir();
        var a = t.File("anh.jpg"); t.File("bai.pdf");
        var plan = OrganizePlanner.Plan(t.Path, Now);
        var fix = new OrganizeFix(t.Path, plan);

        await Exec().RunAsync(fix, new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(fix, new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.True(File.Exists(a));
        Assert.False(Directory.Exists(Path.Combine(t.Path, "Ảnh")));

        File.AppendAllText(a, "đã sửa");
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.True(File.Exists(a));                                           // file đổi: bỏ qua
        Assert.True(File.Exists(Path.Combine(t.Path, "Tài liệu", "bai.pdf")));
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
    }

    [Fact]
    public void Ctor_Rejects_Nested_Source_Blocked_Folder_And_Empty()
    {
        using var t = new TempDir();
        t.File("sub\\x.jpg");
        var nested = new[] { new MoveItem(Path.Combine(t.Path, "sub", "x.jpg"), Path.Combine(t.Path, "Ảnh"), 1, DateTime.UtcNow) };
        Assert.Throws<ArgumentException>(() => new OrganizeFix(t.Path, nested));
        Assert.Throws<ArgumentException>(() => new OrganizeFix(t.Path, Array.Empty<MoveItem>()));
        var ok = new[] { new MoveItem(Path.Combine(t.Path, "x.jpg"), Path.Combine(t.Path, "Ảnh"), 1, DateTime.UtcNow) };
        Assert.Throws<ArgumentException>(() => new OrganizeFix(@"C:\", ok));
        var escape = new[] { new MoveItem(Path.Combine(t.Path, "x.jpg"), @"C:\Windows\Ảnh", 1, DateTime.UtcNow) };
        Assert.Throws<ArgumentException>(() => new OrganizeFix(t.Path, escape));
    }

    [Fact]
    public void Unique_Name_Adds_Numbers()
    {
        using var t = new TempDir();
        t.File("a.txt"); t.File("a (2).txt");
        Assert.Equal(Path.Combine(t.Path, "a (3).txt"), OrganizeFix.UniqueName(t.Path, "a.txt"));
        Assert.Equal(Path.Combine(t.Path, "b.txt"), OrganizeFix.UniqueName(t.Path, "b.txt"));
    }
}
