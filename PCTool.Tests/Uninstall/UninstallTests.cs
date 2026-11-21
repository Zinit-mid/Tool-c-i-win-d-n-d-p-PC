using PCTool.Core.AppInstaller;
using PCTool.Core.Folders;
using PCTool.Core.Framework;
using PCTool.Core.Uninstall;
using PCTool.Core.Windows;
using PCTool.Tests.Folders;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Uninstall;

public sealed class UninstallTests
{
    private const string Win = @"C:\Windows";

    private static InstalledProgram P(string name = "Cool App", string key = "CoolApp", string publisher = "Cool Inc", string uninstall = @"""C:\Program Files\Cool\unins000.exe""",
        string location = @"C:\Program Files\Cool", bool system = false, string parent = "", string version = "1.0") =>
        new("HKLM", false, key, name, version, publisher, uninstall, "", location, 2048, system, parent, false);

    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    // ------------------------------------------------------------ Đọc danh sách

    [Fact]
    public void Parse_Array_And_Single_Object_And_Garbage()
    {
        var arr = """[{"hive":"HKLM","wow":false,"key":"b","name":"Beta","version":"1","publisher":"P","uninstall":"x","quiet":"","location":"C:\\B\\","sizeKb":100,"system":false,"parent":"","msi":true},{"hive":"HKCU","wow":false,"key":"a","name":"alpha","version":"","publisher":"","uninstall":"","quiet":"","location":"","sizeKb":0,"system":false,"parent":"","msi":false},{"key":"c","name":""}]""";
        var list = InstalledProgramsScanner.Parse(arr);
        Assert.Equal(new[] { "alpha", "Beta" }, list.Select(p => p.Name).ToArray());   // bỏ mục không tên, xếp theo tên
        Assert.Equal(@"C:\B", list[1].InstallLocation.TrimEnd('\\'));
        Assert.True(list[1].IsMsi);
        Assert.Single(InstalledProgramsScanner.Parse("""{"key":"k","name":"One","hive":"HKLM"}"""));
        Assert.Empty(InstalledProgramsScanner.Parse(""));
        Assert.Throws<InvalidOperationException>(() => InstalledProgramsScanner.Parse("không phải json"));
    }

    [Fact]
    public void Scan_Script_Is_Read_Only()
    {
        foreach (var bad in new[] { "Remove-", "Set-", "New-Item", "Stop-", "Start-Process", "uninstall /", "reg delete" })
            Assert.DoesNotContain(bad, InstalledProgramsScanner.Script);
    }

    [Fact]
    public async Task Scan_Reports_Script_Failure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstalledProgramsScanner.RunAsync((_, _) => Task.FromResult(new PowerShellResult(1, "", "lỗi")), CancellationToken.None));
    }

    // ------------------------------------------------------------ Chính sách

    [Fact]
    public void Policy_Blocks_System_Updates_Microsoft_And_No_Uninstaller()
    {
        Assert.False(UninstallPolicy.Evaluate(P(system: true)).Allowed);
        Assert.False(UninstallPolicy.Evaluate(P(parent: "KB1")).Allowed);
        Assert.False(UninstallPolicy.Evaluate(P(name: "Update for Windows 10 (KB5001234)")).Allowed);
        Assert.False(UninstallPolicy.Evaluate(P(name: "Microsoft Visual C++ 2015 Redistributable", publisher: "Microsoft Corporation")).Allowed);
        Assert.False(UninstallPolicy.Evaluate(P(uninstall: "")).Allowed);
        Assert.True(UninstallPolicy.Evaluate(P(name: "Microsoft Office Professional", publisher: "Microsoft Corporation")).Allowed);
        Assert.True(UninstallPolicy.Evaluate(P()).Allowed);
        Assert.Null(UninstallPolicy.Evaluate(P()).Warning);
    }

    [Fact]
    public void Policy_Warns_For_Drivers_And_Security()
    {
        Assert.NotNull(UninstallPolicy.Evaluate(P(name: "Realtek High Definition Audio Driver", publisher: "Realtek")).Warning);
        Assert.NotNull(UninstallPolicy.Evaluate(P(name: "Kaspersky Antivirus")).Warning);
    }

    // ------------------------------------------------------------ Lệnh gỡ

    private static bool Exists(string _) => true;

    [Fact]
    public void Command_Quoted_And_Unquoted_Paths()
    {
        var a = UninstallCommand.Parse(@"""C:\Program Files\Cool\unins000.exe"" /SILENT ""arg two""", Win, Exists);
        Assert.Equal(@"C:\Program Files\Cool\unins000.exe", a.Exe);
        Assert.Equal(new[] { "/SILENT", "arg two" }, a.Args.ToArray());
        var b = UninstallCommand.Parse(@"C:\Program Files\Cool App\uninstall.exe /S", Win, Exists);
        Assert.Equal(@"C:\Program Files\Cool App\uninstall.exe", b.Exe);
        Assert.Equal(new[] { "/S" }, b.Args.ToArray());
    }

    [Theory]
    [InlineData(@"MsiExec.exe /I{12345678-1234-1234-1234-1234567890AB}")]
    [InlineData(@"MsiExec.exe /X{12345678-1234-1234-1234-1234567890AB}")]
    [InlineData(@"""C:\Windows\System32\msiexec.exe"" /x {12345678-1234-1234-1234-1234567890AB}")]
    public void Command_Msi_Becomes_Interactive_Uninstall(string s)
    {
        var c = UninstallCommand.Parse(s, Win, Exists);
        Assert.Equal(@"C:\Windows\System32\msiexec.exe", c.Exe);
        Assert.Equal(new[] { "/x", "{12345678-1234-1234-1234-1234567890AB}" }, c.Args.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"MsiExec.exe /X{NOT-A-GUID}")]
    [InlineData(@"msiexec.exe /x {12345678-1234-1234-1234-1234567890AB} /quiet & calc")]
    [InlineData(@"cmd.exe /c del C:\x")]
    [InlineData(@"""C:\Windows\System32\cmd.exe"" /c calc")]
    [InlineData(@"""C:\Program Files\X\powershell.exe"" -c calc")]
    [InlineData(@"""C:\Windows\System32\rundll32.exe"" x,y")]
    [InlineData(@"unins000.exe /S")]
    [InlineData(@"\\server\share\unins.exe")]
    [InlineData(@"""C:\Program Files\X\..\..\Y\u.exe""")]
    [InlineData(@"""C:\Program Files\X\setup.bat""")]
    [InlineData(@"""C:\Windows\Installer\x.exe""")]
    [InlineData(@"""C:\Program Files\X\u.exe"" ""không đóng")]
    public void Command_Rejects_Dangerous_Or_Odd(string s) =>
        Assert.Throws<ArgumentException>(() => UninstallCommand.Parse(s, Win, Exists));

    [Fact]
    public void Command_Rejects_Missing_File_Long_And_Control_Chars()
    {
        Assert.Throws<ArgumentException>(() => UninstallCommand.Parse(@"""C:\Program Files\X\u.exe""", Win, _ => false));
        Assert.Throws<ArgumentException>(() => UninstallCommand.Parse(@"""C:\Program Files\X\u.exe"" " + new string('a', 700), Win, Exists));
        Assert.Throws<ArgumentException>(() => UninstallCommand.Parse("\"C:\\Program Files\\X\\u.exe\"\n/S", Win, Exists));
    }

    // ------------------------------------------------------------ UninstallFix

    private sealed class Proc : IProcessRunner
    {
        public List<(string exe, string[] args)> Calls { get; } = new();
        public ProcessResult Result = new(0, "", "");
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
        { Calls.Add((fileName, args.ToArray())); return Task.FromResult(Result); }
    }

    private static string ListJson(params string[] keys) =>
        "[" + string.Join(",", keys.Select(k => $"{{\"hive\":\"HKLM\",\"wow\":false,\"key\":\"{k}\",\"name\":\"{k}\"}}")) + "]";

    [Fact]
    public void Fix_Refuses_Blocked_Program_And_Bad_Command()
    {
        Assert.Throws<ArgumentException>(() => new UninstallFix(P(system: true), Win, new Proc(), null, Exists));
        Assert.Throws<ArgumentException>(() => new UninstallFix(P(uninstall: "cmd.exe /c calc"), Win, new Proc(), null, Exists));
    }

    [Fact]
    public async Task Fix_Runs_Own_Uninstaller_Then_Verifies_Entry_Is_Gone()
    {
        var proc = new Proc { Result = new ProcessResult(5, "", "") };   // mã lạ nhưng vẫn gỡ xong
        var fix = new UninstallFix(P(), Win, proc, (_, _) => Task.FromResult(new PowerShellResult(0, ListJson("Other"), "")), Exists, TimeSpan.Zero, 2);

        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        var call = Assert.Single(proc.Calls);
        Assert.Equal(@"C:\Program Files\Cool\unins000.exe", call.exe);
        Assert.Empty(call.args);
        Assert.True(fix.RequiresRestorePoint);
        Assert.DoesNotContain(fix.Actions, a => a.Contains("/quiet", StringComparison.OrdinalIgnoreCase) || a.Contains("/silent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Fix_Reports_StillPresent_When_Entry_Remains()
    {
        var fix = new UninstallFix(P(), Win, new Proc(), (_, _) => Task.FromResult(new PowerShellResult(0, ListJson("CoolApp"), "")), Exists, TimeSpan.Zero, 3);
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.StillPresent, res.Outcome);
    }

    [Fact]
    public async Task Fix_Timeout_Fails_And_DryRun_SafeMode_Run_Nothing()
    {
        var timeout = new Proc { Result = new ProcessResult(-1, "", "Quá thời gian chờ (1800 giây).") };
        var f1 = new UninstallFix(P(), Win, timeout, null, Exists, TimeSpan.Zero, 1);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(f1, Real(), null, CancellationToken.None)).Outcome);

        var proc = new Proc();
        var f2 = new UninstallFix(P(), Win, proc, null, Exists, TimeSpan.Zero, 1);
        await Exec().RunAsync(f2, new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(f2, new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.Empty(proc.Calls);
    }

    [Fact]
    public void Actions_Show_Warning_First_And_Interactive_Note()
    {
        var a = new UninstallFix(P(name: "Kaspersky Antivirus"), Win, new Proc(), null, Exists).Actions;
        Assert.StartsWith("CẢNH BÁO", a[0]);
        Assert.Contains(a, x => x.Contains("không gỡ im lặng"));
    }

    // ------------------------------------------------------------ Tiến trình / dịch vụ

    [Theory]
    [InlineData(@"C:\Program Files\Cool", true)]
    [InlineData(@"D:\Games\Cool Game (x86)\", true)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"C:\Program Files\Cool'; calc; '", false)]
    [InlineData(@"C:\Program Files\Cool$(calc)", false)]
    [InlineData(@"\\srv\share\x", false)]
    [InlineData(@"C:\Program Files\..\Windows", false)]
    [InlineData("", false)]
    public void InstallPathGuard_Cases(string p, bool ok) => Assert.Equal(ok, InstallPathGuard.Check(p, out _));

    [Fact]
    public void Activity_Parse_And_Scripts()
    {
        var a = AppActivityScanner.Parse("P|cool\nP|cool\nS|CoolSvc\nrác\nX|bad\nP|\n");
        Assert.Equal(new[] { "cool" }, a.Processes.ToArray());
        Assert.Equal(new[] { "CoolSvc" }, a.Services.ToArray());

        var scan = AppActivityScanner.ScanScript(@"C:\Program Files\Cool");
        Assert.Contains(@"$loc = 'C:\Program Files\Cool\'", scan);
        Assert.DoesNotContain("Stop-", scan);
        var stop = AppActivityScanner.StopScript(@"C:\Program Files\Cool");
        Assert.Contains("Stop-Process", stop);
        foreach (var bad in new[] { "Remove-", "Set-Service", "sc.exe", "Disable", "reg " }) Assert.DoesNotContain(bad, stop);
    }

    [Fact]
    public async Task StopApp_Runs_Stop_Script_And_Verifies()
    {
        var scripts = new List<string>();
        bool stopped = false;
        ScriptRunner run = (s, _) =>
        {
            scripts.Add(s);
            if (s.Contains("Stop-Process")) { stopped = true; return Task.FromResult(new PowerShellResult(0, "OK", "")); }
            return Task.FromResult(new PowerShellResult(0, stopped ? "" : "P|cool\nS|CoolSvc", ""));
        };
        var p = P();
        var fix = new StopAppFix(p, new AppActivity(new[] { "cool" }, new[] { "CoolSvc" }), run);

        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Contains(fix.Actions, a => a.Contains("CoolSvc")); Assert.Contains(fix.Actions, a => a.Contains("cool"));
    }

    [Fact]
    public async Task StopApp_StillPresent_When_Process_Survives_And_Ctor_Rejects()
    {
        ScriptRunner run = (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("Stop-Process") ? "OK" : "P|cool", ""));
        var fix = new StopAppFix(P(), new AppActivity(new[] { "cool" }, Array.Empty<string>()), run);
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(fix, Real(), null, CancellationToken.None)).Outcome);

        Assert.Throws<ArgumentException>(() => new StopAppFix(P(location: ""), new AppActivity(new[] { "x" }, Array.Empty<string>())));
        Assert.Throws<ArgumentException>(() => new StopAppFix(P(), new AppActivity(Array.Empty<string>(), Array.Empty<string>())));
    }
}

public sealed class LeftoverTests
{
    private static InstalledProgram P(string name, string location = "", string key = "k") =>
        new("HKLM", false, key, name, "1", "Pub", "x", "", location, 0, false, "", false);

    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    [Fact]
    public void Scan_Refuses_When_Still_Installed()
    {
        using var t = new TempDir();
        var p = P("Cool App");
        Assert.Throws<InvalidOperationException>(() => LeftoverScanner.Scan(p, new[] { p }, new[] { t.Path }));
    }

    [Fact]
    public void Scan_Finds_Install_Location_And_Same_Name_Folders_Only_Inside_Roots()
    {
        using var t = new TempDir();
        using var outside = new TempDir();
        t.File("Cool App\\a.dat"); t.File("Cool App\\sub\\b.dat");
        t.File("Cool Location\\c.dat");
        t.File("Khac\\d.dat");
        outside.File("Cool App\\e.dat");
        var p = P("Cool App", System.IO.Path.Combine(t.Path, "Cool Location"));

        var found = LeftoverScanner.Scan(p, Array.Empty<InstalledProgram>(), new[] { t.Path });

        Assert.Equal(new[] { System.IO.Path.Combine(t.Path, "Cool Location"), System.IO.Path.Combine(t.Path, "Cool App") }, found.Select(f => f.Path).ToArray());
        Assert.Equal(2, found[1].Files);
        // InstallLocation ngoài các thư mục gốc được phép thì bị bỏ qua
        var p2 = P("Zed Zed", System.IO.Path.Combine(outside.Path, "Cool App"));
        Assert.Empty(LeftoverScanner.Scan(p2, Array.Empty<InstalledProgram>(), new[] { t.Path }));
    }

    [Fact]
    public void Scan_Skips_Generic_Names_And_Folders_Used_By_Other_Programs()
    {
        using var t = new TempDir();
        t.File("Microsoft\\x.dat"); t.File("Shared Dir\\y.dat");
        Assert.Empty(LeftoverScanner.Scan(P("Microsoft"), Array.Empty<InstalledProgram>(), new[] { t.Path }));
        var other = P("Other", System.IO.Path.Combine(t.Path, "Shared Dir"), key: "o");
        var p = P("Cool", System.IO.Path.Combine(t.Path, "Shared Dir"));
        Assert.Empty(LeftoverScanner.Scan(p, new[] { other }, new[] { t.Path }));
    }

    [Fact]
    public async Task Move_Moves_Folder_Writes_Csv_Keeps_Data()
    {
        using var t = new TempDir();
        using var q = new TempDir();
        var f = t.File("Cool App\\a.dat", fill: 4);
        var found = new[] { new LeftoverFolder(System.IO.Path.Combine(t.Path, "Cool App"), 2048, 1) };
        var fix = new LeftoverMoveFix("Cool App", found, new DateTime(2026, 11, 21, 10, 0, 0), new[] { t.Path }, q.Path);

        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.False(File.Exists(f));
        Assert.True(File.Exists(System.IO.Path.Combine(fix.QuarantineFolder, "01_Cool App", "a.dat")));   // dữ liệu còn nguyên, chỉ đổi chỗ
        Assert.Contains("Cool App", File.ReadAllText(System.IO.Path.Combine(fix.QuarantineFolder, "vi-tri-cu.csv")));
    }

    [Fact]
    public async Task Move_DryRun_SafeMode_Nothing_And_Ctor_Guards()
    {
        using var t = new TempDir();
        using var q = new TempDir();
        var f = t.File("Cool App\\a.dat");
        var found = new[] { new LeftoverFolder(System.IO.Path.Combine(t.Path, "Cool App"), 1, 1) };
        var fix = new LeftoverMoveFix("Cool App", found, null, new[] { t.Path }, q.Path);
        await Exec().RunAsync(fix, new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(fix, new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.True(File.Exists(f));

        Assert.Throws<ArgumentException>(() => new LeftoverMoveFix("x", Array.Empty<LeftoverFolder>(), null, new[] { t.Path }, q.Path));
        Assert.Throws<ArgumentException>(() => new LeftoverMoveFix("x", new[] { new LeftoverFolder(t.Path, 1, 1) }, null, new[] { t.Path }, q.Path));   // chính thư mục gốc
        Assert.Throws<ArgumentException>(() => new LeftoverMoveFix("x", new[] { new LeftoverFolder(@"C:\Windows\System32", 1, 1) }, null, new[] { t.Path }, q.Path));
        Assert.Contains(fix.Actions, a => a.Contains("KHÔNG xóa") && a.Contains("Registry"));
    }
}
