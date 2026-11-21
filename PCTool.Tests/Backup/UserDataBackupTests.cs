using System.Text.Json;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Backup;

public sealed class UserDataBackupTests
{
    private const long GB = 1_000_000_000L;
    private static readonly DateTime T = new(2026, 11, 21, 14, 5, 0);

    private static UserFolderScan F(UserFolder f, long files = 100, long bytes = 5 * GB, long cloud = 0, long cloudBytes = 0, bool exists = true, string? path = null) =>
        new(f, path ?? $@"C:\Users\An\{f}", exists, files, bytes, cloud, cloudBytes);

    private static UserDataScan Scan(int sys = 0, int dest = 1, long free = 500 * GB, params UserFolderScan[] folders) =>
        new(@"C:\Users\An", sys, dest, free, folders.Length > 0 ? folders : new[] { F(UserFolder.Desktop), F(UserFolder.Documents) });

    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));

    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    private static string GoodResult(params (string name, long pf, long pb, long df, long db, int exit)[] rows) =>
        JsonSerializer.Serialize(new
        {
            folders = rows.Select(r => new { name = r.name, source = "x", preFiles = r.pf, preBytes = r.pb, destFiles = r.df, destBytes = r.db, exit = r.exit }).ToArray()
        });

    // ---------------------------------------------------------------- Đọc kết quả quét

    [Fact]
    public void Parse_Good()
    {
        const string j = """{"userProfile":"C:\\Users\\An","systemDisk":0,"destDisk":1,"destFree":123456,"folders":[{"name":"Desktop","path":"C:\\Users\\An\\Desktop","exists":true,"files":10,"bytes":2000,"cloudFiles":1,"cloudBytes":50},{"name":"Bogus","path":"x","exists":true,"files":1,"bytes":1,"cloudFiles":0,"cloudBytes":0}]}""";
        var s = UserDataScanner.Parse(j);
        Assert.Equal(0, s.SystemDisk);
        Assert.Equal(1, s.DestDisk);
        Assert.Equal(123456, s.DestFreeBytes);
        var d = Assert.Single(s.Folders); // tên lạ bị bỏ qua
        Assert.Equal(UserFolder.Desktop, d.Folder);
        Assert.Equal(1, d.CloudFiles);
    }

    [Fact]
    public void Parse_Single_Folder_Object_And_Missing_Disks_Default_Minus1()
    {
        var s = UserDataScanner.Parse("""{"folders":{"name":"Music","path":"","exists":false,"files":0,"bytes":0}}""");
        Assert.Equal(-1, s.SystemDisk);
        Assert.Equal(-1, s.DestDisk);
        Assert.Equal(-1, s.DestFreeBytes);
        Assert.Single(s.Folders);
    }

    [Theory]
    [InlineData("")]
    [InlineData("oops")]
    [InlineData("[1]")]
    public void Parse_Bad_Throws(string j) => Assert.Throws<FormatException>(() => UserDataScanner.Parse(j));

    // ---------------------------------------------------------------- Script quét (chỉ đọc)

    [Fact]
    public void Scan_Script_Is_ReadOnly_And_Targets_Only_Chosen_Folders()
    {
        var s = UserDataScanner.BuildScript(new[] { UserFolder.Desktop, UserFolder.Downloads }, @"D:\SaoLuu");
        Assert.Contains("@('Desktop', 'Downloads')", s);
        Assert.Contains("$dl = 'D'", s);
        Assert.Contains("{374DE290-123F-4565-9164-39C4925E467B}", s);
        foreach (var bad in new[] { "robocopy", "Remove-Item", "Move-Item", "Copy-Item", "Set-Content", "New-Item", "Out-File", "Clear-", "Format-", "Rename-Item" })
            Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_Script_Rejects_Bad_Dest() =>
        Assert.Throws<ArgumentException>(() => UserDataScanner.BuildScript(new[] { UserFolder.Desktop }, @"D:\a'; calc; '"));

    [Fact]
    public async Task Scanner_Requires_Folders_And_Reports_Empty_Output()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new UserDataScanner(Fake("")).ScanAsync(Array.Empty<UserFolder>(), @"D:\B", CancellationToken.None));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UserDataScanner(Fake("", 0, "Access denied")).ScanAsync(new[] { UserFolder.Desktop }, @"D:\B", CancellationToken.None));
        Assert.Contains("Access denied", ex.Message);
    }

    // ---------------------------------------------------------------- Kế hoạch & lớp chặn

    [Fact]
    public void Healthy_Plan_Can_Run_With_Totals()
    {
        var plan = UserDataPlanner.Evaluate(Scan(), @"D:\SaoLuu");
        Assert.True(plan.CanRun);
        Assert.Equal(2, plan.Copy.Count);
        Assert.Equal(200, plan.TotalFiles);
        Assert.Equal(10 * GB, plan.TotalBytes);
        Assert.Equal((long)(10 * GB * 1.05) + (1L << 30), plan.NeedBytes);
    }

    [Fact]
    public void Same_Physical_Disk_Blocks()
    {
        var plan = UserDataPlanner.Evaluate(Scan(sys: 0, dest: 0), @"D:\SaoLuu");
        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("CÙNG ổ vật lý"));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    public void Unknown_Disk_Blocks(int sys, int dest)
    {
        var plan = UserDataPlanner.Evaluate(Scan(sys, dest), @"D:\SaoLuu");
        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("không thể bảo đảm"));
    }

    [Fact]
    public void Not_Enough_Space_Blocks()
    {
        var plan = UserDataPlanner.Evaluate(Scan(free: 10 * GB), @"D:\SaoLuu"); // cần ~11,5 GB
        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("không đủ chỗ"));
    }

    [Fact]
    public void Unknown_Free_Space_Blocks() =>
        Assert.False(UserDataPlanner.Evaluate(Scan(free: -1), @"D:\SaoLuu").CanRun);

    [Fact]
    public void Cloud_Only_Folder_Is_Skipped_Not_Copied()
    {
        var plan = UserDataPlanner.Evaluate(Scan(0, 1, 500 * GB, F(UserFolder.Desktop, cloud: 5, cloudBytes: GB), F(UserFolder.Documents)), @"D:\SaoLuu");
        Assert.True(plan.CanRun);
        Assert.Equal(UserFolder.Documents, Assert.Single(plan.Copy).Folder);
        Assert.Contains(plan.Skipped, s => s.Contains("đám mây") && s.Contains("BỎ QUA"));
    }

    [Fact]
    public void All_Folders_Cloud_Only_Cannot_Run()
    {
        var plan = UserDataPlanner.Evaluate(Scan(0, 1, 500 * GB, F(UserFolder.Desktop, cloud: 5, cloudBytes: GB)), @"D:\SaoLuu");
        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("Không có thư mục nào"));
    }

    [Fact]
    public void Missing_And_Empty_Folders_Skipped()
    {
        var plan = UserDataPlanner.Evaluate(Scan(0, 1, 500 * GB, F(UserFolder.Music, exists: false), F(UserFolder.Videos, files: 0, bytes: 0), F(UserFolder.Documents)), @"D:\SaoLuu");
        Assert.True(plan.CanRun);
        Assert.Single(plan.Copy);
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public void Dest_Inside_Source_Blocks_But_Sibling_Does_Not()
    {
        var inside = UserDataPlanner.Evaluate(Scan(0, 1, 500 * GB, F(UserFolder.Documents, path: @"D:\Docs")), @"D:\Docs\Backup");
        Assert.False(inside.CanRun);
        Assert.Contains(inside.Blockers, b => b.Contains("bên trong"));

        var sibling = UserDataPlanner.Evaluate(Scan(0, 1, 500 * GB, F(UserFolder.Documents, path: @"D:\Docs")), @"D:\Docs2\Backup");
        Assert.True(sibling.CanRun);
    }

    [Theory]
    [InlineData(@"D:\a\b", @"D:\a", true)]
    [InlineData(@"D:\a", @"D:\a", true)]
    [InlineData(@"d:\A\b\", @"D:\a", true)]
    [InlineData(@"D:\ab", @"D:\a", false)]
    [InlineData(@"E:\a\b", @"D:\a", false)]
    [InlineData("", @"D:\a", false)]
    [InlineData(@"D:\a", "", false)]
    public void IsInside(string child, string parent, bool expected) => Assert.Equal(expected, UserDataPlanner.IsInside(child, parent));

    [Fact]
    public void Invalid_Dest_Path_Blocks() =>
        Assert.False(UserDataPlanner.Evaluate(Scan(), @"D:\a'; calc; '").CanRun);

    [Fact]
    public void Warnings_Mention_Privacy_And_Current_Account_Only()
    {
        var plan = UserDataPlanner.Evaluate(Scan(), @"D:\SaoLuu");
        Assert.Contains(plan.Warnings, w => w.Contains("riêng tư"));
        Assert.Contains(plan.Warnings, w => w.Contains("C:\\Users\\An") && w.Contains("khác"));
    }

    [Fact]
    public void ToText_Is_Clear_That_Nothing_Was_Copied()
    {
        var s = Scan();
        var t = UserDataPlanner.ToText(s, UserDataPlanner.Evaluate(s, @"D:\SaoLuu"));
        Assert.Contains("chưa chép gì", t);
        Assert.Contains("Desktop", t);
    }

    // ---------------------------------------------------------------- Fix sao lưu

    private static UserDataBackupFix MakeFix(ScriptRunner? run = null, UserDataPlan? plan = null) =>
        new(plan ?? UserDataPlanner.Evaluate(Scan(), @"D:\SaoLuu"), @"D:\SaoLuu", T, run ?? Fake(""));

    [Fact]
    public void Fix_Rejects_Plan_That_Cannot_Run() =>
        Assert.Throws<ArgumentException>(() => new UserDataBackupFix(UserDataPlanner.Evaluate(Scan(0, 0), @"D:\SaoLuu"), @"D:\SaoLuu", T));

    [Fact]
    public void Fix_Destination_Has_Timestamp_And_Is_Not_Undoable_Free()
    {
        var f = MakeFix();
        Assert.Equal(@"D:\SaoLuu\2026-11-21_1405\userdata", f.Destination);
        Assert.True(f.IsUndoable);
        Assert.False(f.RequiresRestorePoint);
        Assert.Contains(f.Actions, a => a.Contains("không xóa, không di chuyển"));
    }

    [Fact]
    public void Copy_Script_Only_Copies_Never_Deletes_Or_Moves()
    {
        var s = MakeFix().BuildScript();
        Assert.Contains("& robocopy $src $dst /E /COPY:DAT /DCOPY:DAT /XJ", s);
        foreach (var bad in new[] { "/MIR", "/MOVE", "/MOV ", "/PURGE", "Remove-Item", "Move-Item", "Rename-Item", "Clear-", "Format-", "Delete" })
            Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$sd -eq $dd", s);                       // chặn cùng ổ vật lý ngay trong script
        Assert.Contains("$pre.CloudFiles -gt 0", s);             // không tải file OneDrive
        Assert.Contains("SizeRemaining", s);
        Assert.Contains($"-lt {MakeFix().BuildScript().Split("$free -lt ")[1].Split(')')[0]}", s);
        Assert.Contains("@('Desktop', 'Documents')", s);
        Assert.Contains(@"$destRoot = 'D:\SaoLuu\2026-11-21_1405\userdata'", s);
        Assert.Contains("manifest.csv", s);
        Assert.Contains("userdata.json", s);
    }

    [Fact]
    public async Task DryRun_And_SafeMode_Run_No_Script()
    {
        var ran = 0;
        ScriptRunner run = (_, _) => { ran++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(MakeFix(run), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(MakeFix(run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Good_Run_Succeeds_And_Verifies()
    {
        var calls = new List<string>();
        ScriptRunner run = (s, _) =>
        {
            calls.Add(s);
            return Task.FromResult(s.Contains("robocopy")
                ? new PowerShellResult(0, GoodResult(("Desktop", 100, 5 * GB, 100, 5 * GB, 1), ("Documents", 100, 5 * GB, 100, 5 * GB, 3)), "")
                : new PowerShellResult(0, "True", ""));
        };
        var res = await Exec().RunAsync(MakeFix(run), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Contains("manifest.csv", res.Message);
        Assert.Equal(2, calls.Count); // chép, rồi kiểm tra lại
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task Robocopy_Failure_Code_Fails(int code)
    {
        var res = await Exec().RunAsync(MakeFix(Fake(GoodResult(("Desktop", 100, 5 * GB, 100, 5 * GB, code), ("Documents", 100, 5 * GB, 100, 5 * GB, 1)))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("robocopy", res.Message);
        Assert.Contains("không bị xóa", res.Message);
    }

    [Fact]
    public async Task Missing_Files_Or_Bytes_Fails()
    {
        var a = await Exec().RunAsync(MakeFix(Fake(GoodResult(("Desktop", 100, 5 * GB, 90, 5 * GB, 1), ("Documents", 100, 5 * GB, 100, 5 * GB, 1)))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, a.Outcome);
        var b = await Exec().RunAsync(MakeFix(Fake(GoodResult(("Desktop", 100, 5 * GB, 100, 4 * GB, 1), ("Documents", 100, 5 * GB, 100, 5 * GB, 1)))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, b.Outcome);
    }

    [Fact]
    public async Task Folder_Missing_From_Result_Fails()
    {
        var res = await Exec().RunAsync(MakeFix(Fake(GoodResult(("Desktop", 100, 5 * GB, 100, 5 * GB, 1)))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("Documents", res.Message);
    }

    [Fact]
    public async Task Script_Error_Fails_With_Message()
    {
        var res = await Exec().RunAsync(MakeFix(Fake("", 1, "Nơi lưu nằm cùng ổ vật lý với Windows.")), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("cùng ổ vật lý", res.Message);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("{}")]
    public async Task Garbage_Output_Fails(string output) =>
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(MakeFix(Fake(output)), Real(), null, CancellationToken.None)).Outcome);

    [Fact]
    public async Task Verify_Missing_Manifest_Is_StillPresent()
    {
        ScriptRunner run = (s, _) => Task.FromResult(s.Contains("robocopy")
            ? new PowerShellResult(0, GoodResult(("Desktop", 100, 5 * GB, 100, 5 * GB, 1), ("Documents", 100, 5 * GB, 100, 5 * GB, 1)), "")
            : new PowerShellResult(0, "False", ""));
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(MakeFix(run), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Verifier_Direct() =>
        Assert.Empty(UserDataVerifier.Problems(GoodResult(("Desktop", 5, 100, 5, 100, 0)), new[] { UserFolder.Desktop }));
}

public sealed class AppListBackupTests
{
    private static readonly DateTime T = new(2026, 11, 21, 14, 5, 0);
    private const string Good = """
{"installed":[{"name":"7-Zip 23.01","version":"23.01","publisher":"Igor Pavlov"},{"name":"7-Zip 23.01","version":"23.01","publisher":"Igor Pavlov"},{"name":"Google Chrome","version":"120.0","publisher":"Google LLC"}],
 "winget":{"ok":true,"error":"","ids":["7zip.7zip","Google.Chrome","bad id!","7ZIP.7ZIP"]}}
""";

    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    private sealed class Disk
    {
        public Dictionary<string, string> Files { get; } = new();
        public Task Write(string p, string c, CancellationToken ct) { Files[p] = c; return Task.CompletedTask; }
        public string? Read(string p) => Files.TryGetValue(p, out var v) ? v : null;
    }

    private static AppListBackupFix Make(string output, Disk d, int code = 0, string err = "") =>
        new(@"D:\SaoLuu", T, (_, _) => Task.FromResult(new PowerShellResult(code, output, err)), d.Write, d.Read, () => new DateTimeOffset(2026, 11, 21, 14, 5, 0, TimeSpan.FromHours(7)));

    [Fact]
    public void Parse_Dedupes_Sorts_And_Drops_Invalid_Winget_Ids()
    {
        var r = AppListParser.Parse(Good);
        Assert.Equal(2, r.Installed.Count);
        Assert.Equal("7-Zip 23.01", r.Installed[0].Name);
        Assert.Equal(new[] { "7zip.7zip", "Google.Chrome" }, r.WingetIds); // "bad id!" bị loại, trùng không phân biệt hoa thường chỉ giữ một
        Assert.True(r.WingetOk);
    }

    [Fact]
    public void Parse_Winget_Failure_Keeps_Installed()
    {
        var r = AppListParser.Parse("""{"installed":{"name":"X","version":"1","publisher":"P"},"winget":{"ok":false,"error":"Máy không có winget.","ids":[]}}""");
        Assert.Single(r.Installed);
        Assert.False(r.WingetOk);
        Assert.Contains("winget", r.WingetError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("oops")]
    [InlineData("[1]")]
    public void Parse_Bad_Throws(string j) => Assert.Throws<FormatException>(() => AppListParser.Parse(j));

    [Fact]
    public void Script_Only_Reads_And_Exports()
    {
        var s = AppListBackupFix.Script;
        Assert.Contains("winget export", s);
        foreach (var bad in new[] { "winget install", "winget uninstall", "winget upgrade", "Uninstall-", "msiexec", "Start-Process", "Set-ItemProperty", "Remove-ItemProperty", "New-ItemProperty" })
            Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRun_And_SafeMode_Write_Nothing()
    {
        var d = new Disk();
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(Make(Good, d), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(Make(Good, d), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Empty(d.Files);
    }

    [Fact]
    public async Task Good_Run_Writes_RestorePlan_And_Verifies()
    {
        var d = new Disk();
        var fix = Make(Good, d);
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        var path = @"D:\SaoLuu\2026-11-21_1405\apps\restore-plan.json";
        Assert.Equal(path, fix.FilePath);
        using var doc = JsonDocument.Parse(d.Files[path]);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("installedApps").GetArrayLength());
        Assert.Equal(2, root.GetProperty("wingetPackages").GetArrayLength());
        Assert.Equal("7zip.7zip", root.GetProperty("wingetPackages")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Vietnamese_Names_Survive_Json()
    {
        var d = new Disk();
        await Exec().RunAsync(Make("""{"installed":[{"name":"Bộ gõ Unikey","version":"4.6","publisher":"Phạm Kim Long"}],"winget":{"ok":false,"error":"","ids":[]}}""", d), Real(), null, CancellationToken.None);
        Assert.Contains("Bộ gõ Unikey", d.Files.Values.Single());
    }

    [Fact]
    public async Task Empty_List_Fails_Without_Writing()
    {
        var d = new Disk();
        var res = await Exec().RunAsync(Make("""{"installed":[],"winget":{"ok":false,"error":"x","ids":[]}}""", d), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Empty(d.Files);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task Bad_Output_Fails_Without_Writing(string output)
    {
        var d = new Disk();
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(Make(output, d), Real(), null, CancellationToken.None)).Outcome);
        Assert.Empty(d.Files);
    }

    [Fact]
    public async Task Verify_Fails_When_File_Missing()
    {
        var d = new Disk();
        var fix = Make(Good, d);
        await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
        d.Files.Clear();
        Assert.False(await fix.VerifyAsync(CancellationToken.None));
    }

    [Fact]
    public void Bad_Dest_Rejected() =>
        Assert.Throws<ArgumentException>(() => new AppListBackupFix(@"D:\a'; calc; '", T));
}
