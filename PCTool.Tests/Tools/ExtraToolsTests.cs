using PCTool.Core.Framework;
using PCTool.Core.Tools;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;
using PCTool.Tests.Folders;

namespace PCTool.Tests.Tools;

public sealed class ExtraToolsTests
{
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };
    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));

    [Theory]
    [InlineData(@"D:\Thu muc", true)]
    [InlineData(@"D:\Tài liệu\2026", true)]
    [InlineData(@"D:\a'; calc; '", false)]
    [InlineData(@"D:\a$b", false)]
    [InlineData(@"D:\..\x", false)]
    [InlineData("relative", false)]
    [InlineData(@"\\server\share", false)]
    public void SafePath_Whitelist(string p, bool ok) => Assert.Equal(ok, SafePath.Check(p, out _));

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "2")]
    public async Task Hidden_Sets_And_Verifies(bool show, string value)
    {
        var f = new ShowHiddenFilesFix(show, (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("Set-ItemProperty") ? "" : value, "")));
        Assert.Contains("-Value " + value, f.SetScript());
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(f, Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Hidden_Wrong_Readback_Is_StillPresent_And_DryRun_Does_Nothing()
    {
        var f = new ShowHiddenFilesFix(true, Fake("2"));
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(f, Real(), null, CancellationToken.None)).Outcome);
        var calls = 0;
        var g = new ShowHiddenFilesFix(true, (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); });
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(g, new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(g, new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("ShowSuperHidden", f.SetScript());
    }

    private static string Make(TempDir t, string name) { var p = System.IO.Path.Combine(t.Path, name); Directory.CreateDirectory(p); return p; }

    private static string R(int src, int dest, int exit) => $"{{\"folders\":[{{\"name\":\"copy\",\"srcFiles\":{src},\"destFiles\":{dest},\"exit\":{exit}}}]}}";

    [Fact]
    public void Copy_Rejects_Bad_Paths()
    {
        using var t = new TempDir();
        var src = Make(t, "nguon");
        Assert.Throws<ArgumentException>(() => new BulkCopyFix(src, @"C:\Windows\x"));
        Assert.Throws<ArgumentException>(() => new BulkCopyFix(src, @"D:\a'; calc; '"));
        Assert.Throws<ArgumentException>(() => new BulkCopyFix(@"C:\Windows", @"D:\B"));
        Assert.Throws<ArgumentException>(() => new BulkCopyFix(src, t.Path));            // đích chứa nguồn
        Assert.Throws<ArgumentException>(() => new BulkCopyFix(src, src + "\\con"));      // đích trong nguồn
    }

    [Fact]
    public void Copy_Script_Never_Overwrites_Or_Deletes()
    {
        using var t = new TempDir();
        var f = new BulkCopyFix(Make(t, "nguon"), @"D:\Dich");
        var s = f.BuildScript();
        Assert.Contains("/XC /XN /XO", s);
        Assert.DoesNotContain("/MIR", s);
        Assert.DoesNotContain("/MOVE", s);
        Assert.DoesNotContain("/PURGE", s);
        Assert.DoesNotContain("Remove-Item", s);
        Assert.EndsWith(@"D:\Dich\nguon", f.Dest);
    }

    [Theory]
    [InlineData(10, 12, 1, FixOutcome.Succeeded)]
    [InlineData(10, 9, 1, FixOutcome.Failed)]
    [InlineData(10, 10, 8, FixOutcome.Failed)]
    public async Task Copy_Result_Is_Checked(int src, int dest, int exit, FixOutcome expected)
    {
        using var t = new TempDir();
        var f = new BulkCopyFix(Make(t, "nguon"), @"D:\Dich", Fake(R(src, dest, exit)));
        Assert.Equal(expected, (await Exec().RunAsync(f, Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Copy_Garbage_Or_Script_Error_Fails_And_DryRun_Skips()
    {
        using var t = new TempDir();
        var n = Make(t, "nguon");
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new BulkCopyFix(n, @"D:\Dich", Fake("rác")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new BulkCopyFix(n, @"D:\Dich", Fake("", 1, "lỗi")), Real(), null, CancellationToken.None)).Outcome);
        var calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(new BulkCopyFix(n, @"D:\Dich", run), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Device_Parse_Array_Single_Object_And_Empty()
    {
        var json = """{"video":{"Name":"NVIDIA RTX 3060","CurrentHorizontalResolution":2560,"CurrentVerticalResolution":1440,"CurrentRefreshRate":144,"DriverVersion":"31.0","AdapterRAM":4293918720,"Status":"OK"},"keyboards":[{"Name":"HID Keyboard","Status":"OK"},{"Name":"PS/2","Status":"Error"}],"cameras":[],"mics":[{"FriendlyName":"Microphone (Realtek)","Status":"OK"}]}""";
        var r = DeviceProbe.Parse(json);
        Assert.Contains("2560x1440 @ 144 Hz", Assert.Single(r.Screens));
        Assert.Equal(2, r.Keyboards.Count);
        Assert.Contains("trạng thái: Error", r.Keyboards[1]);
        Assert.Empty(r.Cameras);
        Assert.Contains("không thấy thiết bị nào", r.ToText());
        Assert.Contains("chưa thử thiết bị hoạt động thật", r.ToText());
        Assert.Empty(DeviceProbe.Parse("{}").Screens);
    }

    [Fact]
    public async Task Device_Garbage_Throws_And_Script_Is_ReadOnly()
    {
        Assert.Throws<FormatException>(() => DeviceProbe.Parse("rác"));
        Assert.Throws<FormatException>(() => DeviceProbe.Parse("[1]"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceProbe.ReadAsync(Fake("", 1, "lỗi"), CancellationToken.None));
        Assert.DoesNotContain("Set-", DeviceProbe.Script);
        Assert.DoesNotContain("Remove-", DeviceProbe.Script);
        Assert.DoesNotContain("Disable-", DeviceProbe.Script);
    }
}
