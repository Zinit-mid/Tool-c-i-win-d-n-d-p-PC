using PCTool.Core.Framework;
using PCTool.Core.Uninstall;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Uninstall;

public sealed class StoreAppsTests
{
    private const string Full = "Microsoft.XboxGamingOverlay_7.1.0.0_x64__8wekyb3d8bbwe";

    private static StoreApp A(string name = "Microsoft.XboxGamingOverlay", string full = Full, bool framework = false, bool nonRemovable = false, string sig = "Store", bool resource = false) =>
        new(name, full, "7.1.0.0", "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond", framework, nonRemovable, sig, resource);

    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    [Fact]
    public void Parse_Array_Single_Garbage_And_PublisherText()
    {
        var json = """[{"name":"Zeta","full":"Zeta_1.0.0.0_x64__8wekyb3d8bbwe","version":"1.0.0.0","publisher":"CN=Zeta Inc, O=Zeta","framework":false,"nonremovable":false,"sig":"Store","resource":false},{"name":"alpha","full":"alpha_2.0.0.0_x64__8wekyb3d8bbwe","version":"2","publisher":"","framework":true,"nonremovable":true,"sig":"System","resource":false},{"name":"","full":"x"}]""";
        var list = StoreAppsScanner.Parse(json);
        Assert.Equal(new[] { "alpha", "Zeta" }, list.Select(a => a.Name).ToArray());
        Assert.Equal("Zeta Inc", list[1].PublisherText);
        Assert.True(list[0].IsFramework);
        Assert.Single(StoreAppsScanner.Parse("""{"name":"One","full":"One_1.0.0.0_x64__8wekyb3d8bbwe"}"""));
        Assert.Empty(StoreAppsScanner.Parse(""));
        Assert.Throws<InvalidOperationException>(() => StoreAppsScanner.Parse("không phải json"));
    }

    [Fact]
    public void Scan_Script_Is_Read_Only()
    {
        foreach (var bad in new[] { "Remove-", "Set-", "New-Item", "Stop-", "Add-AppxPackage" })
            Assert.DoesNotContain(bad, StoreAppsScanner.Script);
    }

    [Fact]
    public async Task Scan_Reports_Script_Failure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StoreAppsScanner.RunAsync((_, _) => Task.FromResult(new PowerShellResult(1, "", "lỗi")), CancellationToken.None));
    }

    [Fact]
    public void Policy_Allows_Normal_Store_App()
    {
        var v = StoreAppPolicy.Evaluate(A());
        Assert.True(v.Allowed);
        Assert.NotNull(v.Warning);
    }

    [Theory]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.MicrosoftEdge.Stable")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("Windows.PrintDialog")]
    [InlineData("c5e2524a-ea46-4f67-841f-6a9465d9d515")]
    public void Policy_Blocks_Core_Windows_Packages(string name) =>
        Assert.False(StoreAppPolicy.Evaluate(A(name: name)).Allowed);

    [Fact]
    public void Policy_Blocks_Framework_Resource_NonRemovable_System_And_BadName()
    {
        Assert.False(StoreAppPolicy.Evaluate(A(framework: true)).Allowed);
        Assert.False(StoreAppPolicy.Evaluate(A(resource: true)).Allowed);
        Assert.False(StoreAppPolicy.Evaluate(A(nonRemovable: true)).Allowed);
        Assert.False(StoreAppPolicy.Evaluate(A(sig: "System")).Allowed);
        Assert.False(StoreAppPolicy.Evaluate(A(full: "x'; calc; '_1.0_x64__8wekyb3d8bbwe")).Allowed);
        Assert.False(StoreAppPolicy.Evaluate(A(full: "")).Allowed);
    }

    [Fact]
    public void FullName_Validation()
    {
        Assert.True(StoreAppPolicy.IsValidFullName(Full));
        Assert.True(StoreAppPolicy.IsValidFullName("Foo-Bar.App_1.2.3.4_neutral_en-us_8wekyb3d8bbwe"));
        Assert.False(StoreAppPolicy.IsValidFullName("a b_1.0_x64__8wekyb3d8bbwe"));
        Assert.False(StoreAppPolicy.IsValidFullName("a_1.0_x64__short"));
        Assert.False(StoreAppPolicy.IsValidFullName(null));
    }

    [Fact]
    public void Fix_Refuses_Blocked_App()
    {
        Assert.Throws<ArgumentException>(() => new StoreAppFix(A(name: "Microsoft.WindowsStore")));
        Assert.Throws<ArgumentException>(() => new StoreAppFix(A(framework: true)));
    }

    [Fact]
    public void Fix_Metadata_And_Script_Contains_Only_Validated_Name()
    {
        var fix = new StoreAppFix(A());
        Assert.True(fix.RequiresRestorePoint);
        Assert.False(fix.IsUndoable);
        Assert.Contains(Full, fix.Script);
        Assert.Contains("Remove-AppxPackage", fix.Script);
        Assert.DoesNotContain("-AllUsers", fix.Script);
        Assert.Contains(fix.Actions, x => x.Contains(Full));
    }

    [Fact]
    public async Task Fix_DryRun_Never_Runs_Script()
    {
        int calls = 0;
        var fix = new StoreAppFix(A(), (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); });
        var res = await Exec().RunAsync(fix, new FixOptions { DryRun = true, SafeMode = false, CreateRestorePoint = false }, new Progress<string>(), CancellationToken.None);
        Assert.Equal(FixOutcome.Simulated, res.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Fix_Succeeds_When_Package_Gone_After_Remove()
    {
        var scripts = new List<string>();
        var fix = new StoreAppFix(A(), (s, _) =>
        {
            scripts.Add(s);
            return Task.FromResult(new PowerShellResult(0, s.Contains("Remove-AppxPackage") ? "" : "[]", ""));
        });
        var res = await Exec().RunAsync(fix, Real(), new Progress<string>(), CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Contains(scripts, s => s.Contains("Remove-AppxPackage"));
    }

    [Fact]
    public async Task Fix_Reports_StillPresent_When_Package_Remains()
    {
        var json = """[{"name":"Microsoft.XboxGamingOverlay","full":"Microsoft.XboxGamingOverlay_7.1.0.0_x64__8wekyb3d8bbwe","version":"7.1.0.0","publisher":"","framework":false,"nonremovable":false,"sig":"Store","resource":false}]""";
        var fix = new StoreAppFix(A(), (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("Remove-AppxPackage") ? "" : json, "")));
        var res = await Exec().RunAsync(fix, Real(), new Progress<string>(), CancellationToken.None);
        Assert.Equal(FixOutcome.StillPresent, res.Outcome);
    }

    [Fact]
    public async Task Fix_Fails_When_Script_Fails()
    {
        var fix = new StoreAppFix(A(), (_, _) => Task.FromResult(new PowerShellResult(1, "", "0x80073CFA: bị chặn")));
        var res = await Exec().RunAsync(fix, Real(), new Progress<string>(), CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("0x80073CFA", res.Message);
    }
}
