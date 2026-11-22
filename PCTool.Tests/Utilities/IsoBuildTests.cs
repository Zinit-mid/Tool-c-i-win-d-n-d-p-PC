using PCTool.Core.IsoBuild;
using PCTool.Core.Windows;

namespace PCTool.Tests.Utilities;

public class IsoBuildTests
{
    private const string Osc = IsoBuildPlan.DefaultOscdimg;

    private static IsoBuildPlan Plan(Action<Dictionary<string, object?>>? o = null) =>
        new(@"D:\src\win11.iso", @"D:\out\custom.iso", @"D:\work", 6,
            new[] { "Microsoft.BingNews" }, new[] { "fileext", "dark" }, @"D:\drivers", Osc);

    private static bool FileOk(string f) => f == @"D:\src\win11.iso" || f == Osc;
    private static bool DirOk(string d) => d is @"D:\out" or @"D:\work" or @"D:\drivers";

    [Fact]
    public void Validate_AcceptsGoodPlan() => Assert.Null(Plan().Validate(FileOk, DirOk));

    [Fact]
    public void Validate_RejectsBadInputs()
    {
        var p = Plan();
        Assert.NotNull((p with { SourceIso = @"D:\src\a.zip" }).Validate(f => true, DirOk));
        Assert.NotNull((p with { SourceIso = @"..\a.iso" }).Validate(f => true, DirOk));
        Assert.NotNull((p with { OutputIso = p.SourceIso }).Validate(FileOk, DirOk));
        Assert.NotNull((p with { OutputIso = @"D:\out\x.iso" }).Validate(f => true, DirOk)); // đã tồn tại
        Assert.NotNull((p with { OutputIso = @"D:\nothere\x.iso" }).Validate(FileOk, DirOk));
        Assert.NotNull((p with { WorkDir = @"D:\" }).Validate(FileOk, d => true));
        Assert.NotNull((p with { WorkDir = @"D:\a'b" }).Validate(FileOk, d => true));
        Assert.NotNull((p with { EditionIndex = 0 }).Validate(FileOk, DirOk));
        Assert.NotNull((p with { AppIds = new[] { "Microsoft.WindowsStore" } }).Validate(FileOk, DirOk));
        Assert.NotNull((p with { TweakIds = new[] { "disable-uac" } }).Validate(FileOk, DirOk));
        Assert.NotNull((p with { DriverDir = @"D:\x;y" }).Validate(FileOk, DirOk));
        Assert.NotNull((p with { OscdimgPath = @"D:\tools\evil.exe" }).Validate(f => true, DirOk));
        Assert.Contains("ADK", (p).Validate(f => f == p.SourceIso, DirOk));
    }

    [Fact]
    public void Catalog_HasNoSecurityTweaksAndNoCoreApps()
    {
        foreach (var t in IsoBuildCatalog.Tweaks)
            foreach (var (key, name, _) in t.Values)
            {
                Assert.DoesNotContain("Policies", key);
                Assert.DoesNotContain("Defender", key);
                Assert.DoesNotContain("EnableLUA", name);
            }
        Assert.DoesNotContain(IsoBuildCatalog.Apps, a => a.Id.Contains("Store") || a.Id.Contains("Photos") || a.Id.Contains("Calculator") || a.Id.Contains("Notepad"));
        Assert.Equal(IsoBuildCatalog.Apps.Count, IsoBuildCatalog.Apps.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void Scripts_AreSafe()
    {
        var copy = IsoBuildScripts.Copy(@"D:\a.iso", @"D:\w\iso");
        Assert.Contains("-Access ReadOnly", copy);
        Assert.DoesNotContain("Remove-Item", copy);
        var cust = IsoBuildScripts.Customize(@"D:\w\iso", @"D:\w\mount", new[] { "Microsoft.BingNews" }, new[] { "fileext" }, @"D:\drv");
        Assert.Contains("'Microsoft.BingNews'", cust);
        Assert.Contains("Dismount-WindowsImage -Path $mnt -Discard", cust);
        Assert.Contains("reg.exe unload", cust);
        Assert.Contains("HideFileExt", cust);
        Assert.DoesNotContain("Defender", cust);
        Assert.DoesNotContain("Set-MpPreference", cust);
        var none = IsoBuildScripts.Customize(@"D:\w\iso", @"D:\w\mount", Array.Empty<string>(), Array.Empty<string>(), null);
        Assert.DoesNotContain("Add-WindowsDriver", none);
        Assert.DoesNotContain("reg.exe load", none);
        Assert.Contains("-Recurse -Force", IsoBuildScripts.Cleanup(@"D:\w", @"D:\w\mount"));
    }

    private static BuildIsoFix Fix(List<string> log, int failStep = -1, long free = long.MaxValue, long outLen = 5_000_000_000)
    {
        int n = 0;
        ScriptRunner run = (s, ct) =>
        {
            log.Add(s);
            bool isCleanup = s.Contains("'STILL'");
            if (isCleanup) return Task.FromResult(new PowerShellResult(0, "OK", ""));
            n++;
            return Task.FromResult(n == failStep ? new PowerShellResult(1, "", "hỏng") : new PowerShellResult(0, "OK", ""));
        };
        return new BuildIsoFix(Plan(), run, f => f == @"D:\src\win11.iso" ? 5_000_000_000 : outLen, d => free, "T1", FileOk, DirOk);
    }

    [Fact]
    public async Task Execute_RunsFourStepsThenCleanup()
    {
        var log = new List<string>();
        var r = await Fix(log).ExecuteAsync(new Progress<string>(), default);
        Assert.Equal(PCTool.Core.Framework.FixOutcome.Succeeded, r.Outcome);
        Assert.Equal(5, log.Count);
        Assert.Contains("robocopy", log[0]);
        Assert.Contains("Export-Image", log[1]);
        Assert.Contains("Mount-WindowsImage", log[2]);
        Assert.Contains("oscdimg", log[3]);
        Assert.Contains("Remove-Item -LiteralPath", log[4]);
    }

    [Fact]
    public async Task Execute_StopsAtFailedStepButStillCleansUp()
    {
        var log = new List<string>();
        var r = await Fix(log, failStep: 2).ExecuteAsync(new Progress<string>(), default);
        Assert.Equal(PCTool.Core.Framework.FixOutcome.Failed, r.Outcome);
        Assert.Contains("hỏng", r.Message);
        Assert.Equal(3, log.Count); // copy, export (lỗi), dọn dẹp
        Assert.Contains("Remove-Item -LiteralPath", log[2]);
    }

    [Fact]
    public async Task Execute_RefusesWhenNotEnoughSpace()
    {
        var log = new List<string>();
        var r = await Fix(log, free: 1_000_000_000).ExecuteAsync(new Progress<string>(), default);
        Assert.Equal(PCTool.Core.Framework.FixOutcome.Failed, r.Outcome);
        Assert.Empty(log);
    }

    [Fact]
    public void Stage_Paths_AreUnderWorkDir()
    {
        var fix = Fix(new List<string>());
        Assert.StartsWith(@"D:\work\PCTool-ISO-T1", fix.Stage);
        Assert.Equal(@"D:\work\PCTool-ISO-T1\iso", fix.IsoDir);
        Assert.Equal(@"D:\work\PCTool-ISO-T1\mount", fix.Mount);
    }

    [Fact]
    public async Task Verify_NeedsBigEnoughOutput()
    {
        Assert.True(await Fix(new List<string>(), outLen: 5_000_000_000).VerifyAsync(default));
        Assert.False(await Fix(new List<string>(), outLen: 10).VerifyAsync(default));
    }

    [Fact]
    public void Actions_MentionNoSecurityChange()
    {
        var a = string.Join("\n", Fix(new List<string>()).Actions);
        Assert.Contains("không ghi đè", a);
        Assert.Contains("Không tắt Defender", a);
        Assert.False(Fix(new List<string>()).RequiresRestorePoint);
    }
}
