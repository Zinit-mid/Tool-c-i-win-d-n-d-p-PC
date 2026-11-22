using PCTool.Core.AppInstaller;
using PCTool.Core.Checklist;
using PCTool.Core.Disk;
using PCTool.Core.Drivers;
using PCTool.Core.Framework;
using PCTool.Core.License;
using PCTool.Core.Pdf;
using PCTool.Core.Report;
using PCTool.Core.Startup;
using PCTool.Core.Windows;

namespace PCTool.Tests.Utilities;

public class UtilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pct-ut-" + Guid.NewGuid().ToString("N"));
    public UtilityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ScriptRunner Fake(int exit, string output, string error = "") =>
        (s, ct) => Task.FromResult(new PowerShellResult(exit, output, error));

    // ---------- Driver ----------
    private const string TwoProblems = """{"maker":"Dell Inc.","model":"XPS","problems":[{"name":"Ethernet Controller","cls":"Net","code":28,"id":"PCI\\X"},{"name":"Cổng tắt","cls":"Ports","code":22,"id":"USB\\Y"}]}""";

    [Fact]
    public void DriverScan_Parse_ObjectArrayAndEmpty()
    {
        var r = DriverScanner.Parse(TwoProblems);
        Assert.Equal(2, r.Problems.Count);
        Assert.Equal(1, r.MissingCount);
        Assert.Contains("Chưa có driver", r.Problems[0].Meaning);
        Assert.False(r.Problems[1].NeedsDriver);
        var single = DriverScanner.Parse("""{"maker":"A","model":"B","problems":{"name":"X","cls":"C","code":28,"id":"I"}}""");
        Assert.Single(single.Problems);
        Assert.Empty(DriverScanner.Parse("""{"maker":"A","model":"B","problems":[]}""").Problems);
        Assert.Throws<System.Text.Json.JsonException>(() => DriverScanner.Parse("khong phai json"));
    }

    [Fact]
    public void DriverScan_Script_IsReadOnly()
    {
        Assert.DoesNotContain("Set-", DriverScanner.Script);
        Assert.DoesNotContain("Remove-", DriverScanner.Script);
        Assert.DoesNotContain("Install", DriverScanner.Script);
    }

    [Fact]
    public async Task WuDriverFix_Succeeds_WhenMissingDrops()
    {
        int scans = 0;
        ScriptRunner scan = (s, ct) => Task.FromResult(new PowerShellResult(0, scans++ == 0 ? TwoProblems : """{"maker":"A","model":"B","problems":[]}""", ""));
        var fix = new WindowsUpdateDriverFix(Fake(0, """{"found":2,"skipped":0,"installed":2,"failed":0,"reboot":true,"titles":["a","b"]}"""), scan);
        var res = await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Contains("KHỞI ĐỘNG LẠI", res.Message);
        Assert.True(await fix.VerifyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WuDriverFix_StillPresent_WhenNothingChanges()
    {
        ScriptRunner scan = (s, ct) => Task.FromResult(new PowerShellResult(0, TwoProblems, ""));
        var fix = new WindowsUpdateDriverFix(Fake(0, """{"found":0,"skipped":0,"installed":0,"failed":0,"reboot":false,"titles":[]}"""), scan);
        var res = await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.False(await fix.VerifyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WuDriverFix_Failures_Reported()
    {
        ScriptRunner scan = (s, ct) => Task.FromResult(new PowerShellResult(0, TwoProblems, ""));
        var bad = new WindowsUpdateDriverFix(Fake(0, """{"found":2,"skipped":0,"installed":0,"failed":2,"reboot":false,"titles":[]}"""), scan);
        Assert.Equal(FixOutcome.Failed, (await bad.ExecuteAsync(new Progress<string>(), CancellationToken.None)).Outcome);
        var err = new WindowsUpdateDriverFix(Fake(1, "", "mất mạng"), scan);
        Assert.Equal(FixOutcome.Failed, (await err.ExecuteAsync(new Progress<string>(), CancellationToken.None)).Outcome);
        var junk = new WindowsUpdateDriverFix(Fake(0, "rac"), scan);
        Assert.Equal(FixOutcome.Failed, (await junk.ExecuteAsync(new Progress<string>(), CancellationToken.None)).Outcome);
        Assert.True(bad.RequiresRestorePoint);
        Assert.Contains("Type='Driver'", WindowsUpdateDriverFix.Script);
        Assert.DoesNotContain("AcceptEula()", WindowsUpdateDriverFix.Script);
    }

    [Fact]
    public void VendorPages_Whitelist()
    {
        var pages = DriverVendorPages.ForMaker("Dell Inc.");
        Assert.Contains("dell.com", pages[0].Url);
        Assert.True(pages.Count > DriverVendorPages.Common.Count);
        Assert.Equal(DriverVendorPages.Common.Count, DriverVendorPages.ForMaker("Hãng lạ").Count);
        Assert.All(pages, p => Assert.StartsWith("https://", p.Url));
        Assert.False(DriverVendorPages.IsAllowed("https://evil.example/driver"));
        Assert.False(DriverVendorPages.IsAllowed(null));
        Assert.Throws<ArgumentException>(() => DriverVendorPages.Open("http://www.dell.com/support/home/", _ => { }));
        string? opened = null;
        DriverVendorPages.Open(pages[0].Url, u => opened = u);
        Assert.Equal(pages[0].Url, opened);
    }

    // ---------- Startup ----------
    [Fact]
    public void Startup_Parse()
    {
        var items = StartupScanner.Parse("""[{"loc":"HkcuRun","name":"Zalo","cmd":"C:\\z.exe","enabled":true},{"loc":"HklmRun","name":"Teams","cmd":"t","enabled":false},{"loc":"Bad","name":"x","cmd":"","enabled":true}]""");
        Assert.Equal(2, items.Count);
        Assert.False(items[1].Enabled);
        Assert.Empty(StartupScanner.Parse("[]"));
        Assert.DoesNotContain("Set-", StartupScanner.Script);
        Assert.DoesNotContain("Remove-", StartupScanner.Script);
    }

    [Fact]
    public async Task StartupToggle_EncodesName_AndVerifies()
    {
        var item = new StartupItem(StartupLocation.HkcuRun, "a'b$(calc)", "cmd", true);
        string? script = null;
        ScriptRunner run = (s, ct) => { script = s; return Task.FromResult(new PowerShellResult(0, s.Contains("OFF") ? "OFF" : "OFF", "")); };
        var fix = new StartupToggleFix(item, enable: false, run);
        var res = await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.DoesNotContain("a'b", script);
        Assert.DoesNotContain("calc", script);
        Assert.DoesNotContain("Remove-", script);
        Assert.True(await fix.VerifyAsync(CancellationToken.None));
        var enableFix = new StartupToggleFix(item, enable: true, Fake(0, "OFF"));
        Assert.False(await enableFix.VerifyAsync(CancellationToken.None));
        Assert.Equal(FixOutcome.Failed, (await new StartupToggleFix(item, true, Fake(1, "", "không còn")).ExecuteAsync(new Progress<string>(), CancellationToken.None)).Outcome);
        Assert.False(fix.RequiresRestorePoint);
        Assert.True(fix.IsUndoable);
    }

    [Fact]
    public void StartupToggle_RejectsBadItems()
    {
        Assert.Throws<ArgumentException>(() => new StartupToggleFix(new StartupItem(StartupLocation.HkcuRun, "", "", true), false));
        Assert.Throws<ArgumentException>(() => new StartupToggleFix(new StartupItem(StartupLocation.HkcuRun, "a\nb", "", true), false));
        Assert.Throws<ArgumentException>(() => new StartupToggleFix(new StartupItem((StartupLocation)99, "x", "", true), false));
    }

    // ---------- Disk ----------
    [Fact]
    public void DiskUsage_Scan_SortsBySize()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "small"));
        Directory.CreateDirectory(Path.Combine(_dir, "big", "sub"));
        File.WriteAllBytes(Path.Combine(_dir, "small", "a.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_dir, "big", "sub", "b.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_dir, "x.bin"), new byte[5]);
        var r = DiskUsage.Scan(_dir, CancellationToken.None);
        Assert.Equal("big", r[0].Name);
        Assert.Equal(1000, r[0].Bytes);
        Assert.Contains(r, f => !f.IsFolder && f.Bytes == 5);
        Assert.True(File.Exists(Path.Combine(_dir, "x.bin")));
    }

    [Fact]
    public void DiskUsage_BadRoots()
    {
        Assert.NotNull(DiskUsage.CheckRoot(null));
        Assert.NotNull(DiskUsage.CheckRoot(@"\\server\share"));
        Assert.NotNull(DiskUsage.CheckRoot(Path.Combine(_dir, "none")));
        Assert.Null(DiskUsage.CheckRoot(_dir));
        Assert.Throws<ArgumentException>(() => DiskUsage.Scan(Path.Combine(_dir, "none"), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() =>
        {
            Directory.CreateDirectory(Path.Combine(_dir, "d")); File.WriteAllBytes(Path.Combine(_dir, "d", "f"), new byte[1]);
            var c = new CancellationTokenSource(); c.Cancel();
            DiskUsage.Scan(_dir, c.Token);
        });
    }

    // ---------- License ----------
    [Fact]
    public void License_Parse_KeepsWindowsAndOfficeOnly()
    {
        var r = LicenseChecker.Parse("""[{"name":"Windows(R), Professional edition","app":"55C92734-D682-4D71-983E-D6EC3F16059F","status":1},{"name":"Office 16","app":"0ff1ce15-a989-479d-af46-f275c6370663","status":5},{"name":"Other","app":"zzz","status":1}]""");
        Assert.Equal(2, r.Count);
        Assert.Equal(LicenseKind.Windows, r[0].Kind);
        Assert.True(r[0].Licensed);
        Assert.False(r[1].Licensed);
        Assert.Contains("Chưa kích hoạt", r[1].StatusText);
        Assert.DoesNotContain("ProductKey", LicenseChecker.Script);
        Assert.DoesNotContain("Install", LicenseChecker.Script);
        Assert.DoesNotContain("Activate", LicenseChecker.Script);
    }

    // ---------- Checklist ----------
    [Fact]
    public void Checklist_SaveLoad_Unknown_Corrupt()
    {
        var path = Path.Combine(_dir, "cl.json");
        var c = new PostInstallChecklist(path);
        Assert.Empty(c.LoadDone());
        Assert.True(c.Set("update", true));
        Assert.True(c.Set("driver", true));
        Assert.True(c.Set("update", false));
        Assert.Equal(new[] { "driver" }, c.LoadDone().ToArray());
        Assert.False(c.Set("khong-co", true));
        File.WriteAllText(path, "{hong");
        Assert.Empty(c.LoadDone());
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Equal(PostInstallChecklist.Items.Count, PostInstallChecklist.Items.Select(i => i.Id).Distinct().Count());
    }

    // ---------- Profiles ----------
    [Fact]
    public void AppProfiles_UseOnlyKnownIds()
    {
        var known = AppCatalog.Defaults.Select(d => d.Id).Concat(AppCatalog.Suggested.Select(s => s.Id)).ToHashSet();
        foreach (var p in AppProfiles.All)
        {
            Assert.NotEmpty(p.Ids);
            Assert.All(p.Ids, id => Assert.Contains(id, known));
            Assert.Equal(p.Ids.Count, p.Ids.Distinct().Count());
        }
    }

    // ---------- Report ----------
    [Fact]
    public void MachineReport_HandlesNullsAndSections()
    {
        var empty = MachineReport.Build(DateTimeOffset.Now, "", null, null, null, null);
        Assert.Contains("chưa quét", empty);
        var full = MachineReport.Build(DateTimeOffset.Now, "CPU x", DriverScanner.Parse(TwoProblems),
            new[] { new LicenseEntry(LicenseKind.Windows, "Windows 11", 1) },
            new[] { new StartupItem(StartupLocation.HkcuRun, "Zalo", "c", true) }, new[] { "12:00 [INFO] a" });
        Assert.Contains("CPU x", full);
        Assert.Contains("Ethernet Controller", full);
        Assert.Contains("Đã kích hoạt", full);
        Assert.Contains("Zalo", full);
        Assert.Contains("Nhật ký gần nhất", full);
    }

    // ---------- Word ----------
    [Fact]
    public async Task WordConvert_Success_Failure_BadInput()
    {
        var docx = Path.Combine(_dir, "a.docx"); File.WriteAllText(docx, "x");
        string? script = null;
        ScriptRunner ok = (s, ct) =>
        {
            script = s;
            var outB64 = System.Text.RegularExpressions.Regex.Matches(s, @"FromBase64String\('([^']+)'\)")[1].Groups[1].Value;
            File.WriteAllText(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(outB64)), "pdf");
            return Task.FromResult(new PowerShellResult(0, "", ""));
        };
        var r = await WordConvert.WordToPdfAsync(docx, _dir, ok);
        Assert.True(r.Ok, r.Message);
        Assert.EndsWith(".pdf", r.Outputs![0]);
        Assert.DoesNotContain(docx, script);
        Assert.DoesNotContain("Remove-Item", script);
        Assert.True(File.Exists(docx));

        var nothing = await WordConvert.WordToPdfAsync(docx, _dir, Fake(0, ""));
        Assert.False(nothing.Ok);
        var noWord = await WordConvert.WordToPdfAsync(docx, _dir, Fake(1, "", "Retrieving the COM class factory for Word.Application failed"));
        Assert.Contains("chưa cài Microsoft Word", noWord.Message);
        Assert.False((await WordConvert.PdfToWordAsync(docx, _dir, Fake(0, ""))).Ok);
        Assert.False((await WordConvert.WordToPdfAsync(Path.Combine(_dir, "none.docx"), _dir, Fake(0, ""))).Ok);
    }
}
