using System.ComponentModel;
using PCTool.Core.AppInstaller;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Restore;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Restore;

public sealed class RestoreTests
{
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };
    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));

    private sealed class Proc : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();
        public int Exit { get; set; }
        public bool Throw { get; set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add((fileName, args));
            if (Throw) throw new Win32Exception("no");
            return Task.FromResult(new ProcessResult(Exit, "", Exit == 0 ? "" : "boom"));
        }
    }

    // ------------------------------------------------------------ Driver

    [Fact]
    public void Driver_Args_Are_Separate_And_Add_Only()
    {
        var f = new DriverRestoreFix(@"D:\Backup\drivers\", new Proc(), _ => 3);
        Assert.Equal(new[] { "/add-driver", @"D:\Backup\drivers\*.inf", "/subdirs", "/install" }, f.Args());
        Assert.DoesNotContain(f.Args(), a => a.Contains("delete", StringComparison.OrdinalIgnoreCase) || a.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
        Assert.True(f.RequiresRestorePoint);
    }

    [Theory]
    [InlineData(@"C:\Windows\x")]
    [InlineData(@"D:\a'; calc; '")]
    [InlineData("relative")]
    public void Driver_Bad_Path_Rejected(string p) => Assert.Throws<ArgumentException>(() => new DriverRestoreFix(p));

    [Theory]
    [InlineData(0, FixOutcome.Succeeded)]
    [InlineData(3010, FixOutcome.Succeeded)]
    [InlineData(1, FixOutcome.Failed)]
    public async Task Driver_Exit_Codes(int code, FixOutcome expected)
    {
        var p = new Proc { Exit = code };
        var res = await Exec().RunAsync(new DriverRestoreFix(@"D:\B\drv", p, _ => 2), Real(), null, CancellationToken.None);
        Assert.Equal(expected, res.Outcome);
        if (code == 3010) Assert.Contains("KHỞI ĐỘNG LẠI", res.Message);
        Assert.Equal("pnputil.exe", Assert.Single(p.Calls).File);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Driver_No_Inf_Or_Unreadable_Fails_Without_Running(int count)
    {
        var p = new Proc();
        var res = await Exec().RunAsync(new DriverRestoreFix(@"D:\B\drv", p, _ => count), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Empty(p.Calls);
    }

    [Fact]
    public async Task Driver_Missing_Pnputil_And_DryRun_SafeMode()
    {
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new DriverRestoreFix(@"D:\B", new Proc { Throw = true }, _ => 1), Real(), null, CancellationToken.None)).Outcome);
        var p = new Proc();
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(new DriverRestoreFix(@"D:\B", p, _ => 1), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(new DriverRestoreFix(@"D:\B", p, _ => 1), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Empty(p.Calls);
    }

    // ------------------------------------------------------------ Wi-Fi

    private const string Xml = """<?xml version="1.0"?><WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><name>Nhà Tôi</name><MSM><security><sharedKey><keyMaterial>pw</keyMaterial></sharedKey></security></MSM></WLANProfile>""";

    [Fact]
    public void WifiName_Parse()
    {
        Assert.Equal("Nhà Tôi", WifiProfileName.Parse(Xml));
        Assert.Null(WifiProfileName.Parse("<x/>"));
        Assert.Null(WifiProfileName.Parse("not xml"));
        Assert.Null(WifiProfileName.Parse("""<!DOCTYPE a [<!ENTITY e SYSTEM "file:///c:/x">]><WLANProfile><name>&e;</name></WLANProfile>"""));
        Assert.Null(WifiProfileName.Parse("<WLANProfile><name> </name></WLANProfile>"));
    }

    [Theory]
    [InlineData("Wi-Fi-Nha Toi.xml", true)]
    [InlineData("Wi-Fi-Nhà Tôi (5G).xml", true)]
    [InlineData("a'b.xml", false)]
    [InlineData("a;b.xml", false)]
    [InlineData("a$b.xml", false)]
    [InlineData("a.txt", false)]
    public void Wifi_SafeFileName(string n, bool ok) => Assert.Equal(ok, WifiProfileName.SafeFileName.IsMatch(n));

    private static WifiRestoreFix Wifi(ScriptRunner run, params string[] files) =>
        new(@"D:\B\wifi", run, _ => files, _ => Xml);

    [Fact]
    public async Task Wifi_Add_And_Verify()
    {
        var scripts = new List<string>();
        ScriptRunner run = (s, _) =>
        {
            scripts.Add(s);
            return Task.FromResult(new PowerShellResult(0, s.Contains("show profiles") ? "All User Profile : Nhà Tôi" : "", ""));
        };
        var res = await Exec().RunAsync(Wifi(run, "Wi-Fi-a.xml"), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Contains(scripts, s => s.Contains("netsh wlan add profile") && s.Contains(@"D:\B\wifi\Wi-Fi-a.xml") && s.Contains("user=all"));
        Assert.Equal(2, scripts.Count);
    }

    [Fact]
    public async Task Wifi_Verify_Fails_When_Profile_Not_Listed()
    {
        ScriptRunner run = (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("show profiles") ? "nothing" : "", ""));
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(Wifi(run, "a.xml"), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Wifi_Unsafe_Name_Is_Skipped_Not_Run()
    {
        var ran = 0;
        ScriptRunner run = (_, _) => { ran++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        var res = await Exec().RunAsync(Wifi(run, "a'; calc; '.xml"), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Wifi_Partial_Failure_Reports_Counts()
    {
        var n = 0;
        ScriptRunner run = (_, _) => Task.FromResult(n++ == 0 ? new PowerShellResult(0, "", "") : new PowerShellResult(1, "", "x"));
        var res = await Exec().RunAsync(Wifi(run, "a.xml", "b.xml"), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("1/2", res.Message);
    }

    [Fact]
    public async Task Wifi_Invalid_Xml_Empty_And_Too_Many()
    {
        var f = new WifiRestoreFix(@"D:\B\w", Fake(""), _ => new[] { "a.xml" }, _ => "garbage");
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(f, Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(Wifi(Fake("")), Real(), null, CancellationToken.None)).Outcome);
        var many = Enumerable.Range(0, RestoreLimits.MaxWifiFiles + 1).Select(i => $"w{i}.xml").ToArray();
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(Wifi(Fake(""), many), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Wifi_DryRun_SafeMode_Run_Nothing()
    {
        var ran = 0;
        ScriptRunner run = (_, _) => { ran++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        await Exec().RunAsync(Wifi(run, "a.xml"), new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(Wifi(run, "a.xml"), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.Equal(0, ran);
    }

    [Fact]
    public void Wifi_Script_Only_Adds()
    {
        var s = WifiRestoreFix.BuildAddScript(@"D:\B", "a.xml");
        Assert.Contains("netsh wlan add profile", s);
        foreach (var bad in new[] { "delete", "remove", "Remove-Item", "key=clear" }) Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ restore-plan.json

    private const string Plan = """
{"schemaVersion":1,"computerName":"MAY-CU","wingetPackages":[{"id":"7zip.7zip"},{"id":"Google.Chrome"},{"id":"7ZIP.7ZIP"},{"id":"--source"},{"id":"bad id"},{"x":1}],"installedApps":[{"name":"a"},{"name":"b"}]}
""";

    [Fact]
    public void Plan_Parse_Filters_And_Dedupes()
    {
        var p = RestorePlanReader.Parse(Plan);
        Assert.Equal(new[] { "7zip.7zip", "Google.Chrome" }, p.WingetIds);
        Assert.Equal(2, p.InstalledCount);
        Assert.Equal("MAY-CU", p.ComputerName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("oops")]
    [InlineData("[1]")]
    [InlineData("""{"schemaVersion":2}""")]
    public void Plan_Bad_Throws(string j) => Assert.Throws<FormatException>(() => RestorePlanReader.Parse(j));

    [Fact]
    public void Plan_Entries_Pass_Validator_And_Install_Args_Are_Safe()
    {
        var entries = RestorePlanReader.ToEntries(RestorePlanReader.Parse(Plan));
        Assert.Equal(2, entries.Count);
        foreach (var e in entries)
        {
            Assert.Empty(AppEntryValidator.Validate(e));
            var fix = new InstallAppFix(e, new Proc(), @"C:\x", _ => { });
            Assert.Equal(new[] { "install", "--id", e.WingetId! }, fix.InstallArgs().Take(3));
        }
    }

    [Fact]
    public void Plan_Load_Rejects_Bad_Path_And_Missing_File()
    {
        Assert.Throws<ArgumentException>(() => RestorePlanReader.Load(@"C:\Windows\restore-plan.json"));
        Assert.Throws<FileNotFoundException>(() => RestorePlanReader.Load(@"Z:\khong-co\restore-plan.json"));
    }

    // ------------------------------------------------------------ Dữ liệu

    private static UserDataRestoreFix Data(ScriptRunner run, params UserFolder[] f) =>
        new(@"D:\B\2026-11-21_1405\userdata", f.Length == 0 ? new[] { UserFolder.Documents } : f, run);

    private static string R(params (string n, long s, long d, int e)[] rows) =>
        System.Text.Json.JsonSerializer.Serialize(new { folders = rows.Select(r => new { name = r.n, srcFiles = r.s, destFiles = r.d, exit = r.e }).ToArray() });

    [Fact]
    public void Data_Script_Never_Overwrites_Or_Deletes()
    {
        var s = Data(Fake(""), UserFolder.Documents, UserFolder.Desktop).BuildScript();
        Assert.Contains("/E /XC /XN /XO /COPY:DAT /DCOPY:DAT /XJ", s);
        foreach (var bad in new[] { "/MIR", "/MOVE", "/MOV ", "/PURGE", "Remove-Item", "Move-Item", "Rename-Item", "Clear-", "Format-" })
            Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@('Documents', 'Desktop')", s);
        Assert.Contains(@"$src = 'D:\B\2026-11-21_1405\userdata'", s);
        Assert.Contains("Get-UserFolder", s);
    }

    [Fact]
    public void Data_Bad_Args_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new UserDataRestoreFix(@"C:\Windows\x", new[] { UserFolder.Desktop }));
        Assert.Throws<ArgumentException>(() => new UserDataRestoreFix(@"D:\B", Array.Empty<UserFolder>()));
    }

    [Fact]
    public async Task Data_Good_Run()
    {
        var res = await Exec().RunAsync(Data(Fake(R(("Documents", 10, 12, 1)))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task Data_Robocopy_Failure(int code)
    {
        var res = await Exec().RunAsync(Data(Fake(R(("Documents", 10, 10, code)))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("vẫn còn nguyên", res.Message);
    }

    [Fact]
    public async Task Data_Missing_Files_Fails()
    {
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(Data(Fake(R(("Documents", 10, 9, 1)))), Real(), null, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("""{"folders":[]}""")]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task Data_Empty_Or_Garbage_Fails(string output) =>
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(Data(Fake(output)), Real(), null, CancellationToken.None)).Outcome);

    [Fact]
    public async Task Data_Script_Error_And_DryRun_SafeMode()
    {
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(Data(Fake("", 1, "lỗi")), Real(), null, CancellationToken.None)).Outcome);
        var ran = 0;
        ScriptRunner run = (_, _) => { ran++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        await Exec().RunAsync(Data(run), new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(Data(run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.Equal(0, ran);
    }

    [Fact]
    public void Verifier_Single_Object_Tolerated() =>
        Assert.Empty(RestoreVerifier.Problems("""{"folders":{"name":"Desktop","srcFiles":1,"destFiles":1,"exit":0}}"""));
}
