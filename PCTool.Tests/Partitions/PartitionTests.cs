using PCTool.Core.Framework;
using PCTool.Core.Partitions;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Partitions;

public sealed class PartitionTests
{
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };
    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));
    private const long Gb = 1073741824;

    private static VolumeInfo Vol(string l = "D", string fs = "NTFS", long size = 100 * Gb, long free = 60 * Gb, string bus = "SATA", bool sys = false, bool bad = false)
        => new(l, "Data", fs, size, free, 0, bus, sys, bad);

    [Fact]
    public void Scan_Parse_Array_Single_Garbage_And_BadLetters()
    {
        var json = """[{"letter":"c","label":"","fs":"NTFS","size":300,"free":100,"disk":0,"bus":"NVMe","isSystem":true,"bad":false},{"letter":"??","fs":"NTFS"},{"letter":"D","label":"SOFTWARE","fs":"NTFS","size":10,"free":5,"disk":1,"bus":"SATA","isSystem":false,"bad":false}]""";
        var v = VolumeScanner.Parse(json);
        Assert.Equal(2, v.Count);
        Assert.True(v[0].IsSystem);
        Assert.Equal("C", v[0].Letter);
        Assert.Single(VolumeScanner.Parse("""{"letter":"E","fs":"NTFS"}"""));
        Assert.Throws<FormatException>(() => VolumeScanner.Parse("rác"));
        Assert.Contains("Ổ WINDOWS", v[0].Describe());
        Assert.DoesNotContain("Set-", VolumeScanner.Script);
        Assert.DoesNotContain("Remove-", VolumeScanner.Script);
        Assert.DoesNotContain("Format-", VolumeScanner.Script);
    }

    [Fact]
    public void Shrink_Rejects_Unsafe_Inputs()
    {
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(fs: "exFAT"), 2048, "H", "NEW", "CHIA D"));
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(bus: "USB"), 2048, "H", "NEW", "CHIA D"));
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(bad: true), 2048, "H", "NEW", "CHIA D"));
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(), 100, "H", "NEW", "CHIA D"));              // quá nhỏ
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(free: 6 * Gb), 2048, "H", "NEW", "CHIA D")); // còn <5 GB
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(), 2048, "C", "NEW", "CHIA D"));             // ký tự dành riêng
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(), 2048, "D", "NEW", "CHIA D"));             // trùng nguồn
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(), 2048, "h", "NEW", "CHIA D"));
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(), 2048, "H", "a'; calc; '", "CHIA D"));
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(), 2048, "H", "NEW", "chia d"));             // sai mã
        Assert.Throws<ArgumentException>(() => new ShrinkAndCreateFix(Vol(), 2048, "H", "NEW", ""));
    }

    [Fact]
    public void Shrink_Script_Never_Deletes_And_Formats_Only_New_Partition()
    {
        var s = new ShrinkAndCreateFix(Vol(), 2048, "H", "DATA_NEW", "CHIA D").BuildScript();
        Assert.DoesNotContain("Remove-Partition", s);
        Assert.DoesNotContain("Clear-Disk", s);
        Assert.DoesNotContain("diskpart", s, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(s, "Format-Volume"));
        Assert.Contains("Format-Volume -DriveLetter $nl", s);   // chỉ ổ mới
        Assert.Contains("-Offset", s);                           // đặt đúng chỗ vừa giải phóng
        Assert.Contains("USB", s);
    }

    private static string Ok(long old = 100 * Gb, long now = 98 * Gb, long newSize = 2 * Gb, string fs = "NTFS", string src = "D", string nl = "H")
        => $"{{\"src\":\"{src}\",\"srcOld\":{old},\"srcNew\":{now},\"newLetter\":\"{nl}\",\"fs\":\"{fs}\",\"label\":\"X\",\"newSize\":{newSize}}}";

    [Fact]
    public async Task Shrink_Success_And_Result_Checks()
    {
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(new ShrinkAndCreateFix(Vol(), 2048, "H", "X", "CHIA D", Fake(Ok())), Real(), null, CancellationToken.None)).Outcome);
        foreach (var bad in new[] { Ok(now: 100 * Gb), Ok(fs: "RAW"), Ok(newSize: 100), Ok(src: "E"), "rác" })
            Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ShrinkAndCreateFix(Vol(), 2048, "H", "X", "CHIA D", Fake(bad)), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Shrink_Script_Error_Is_Reported_And_DryRun_SafeMode_Run_Nothing()
    {
        var res = await Exec().RunAsync(new ShrinkAndCreateFix(Vol(), 2048, "H", "X", "CHIA D", Fake("", 1, "ĐÃ THU NHỎ ổ D: nhưng CHƯA tạo")), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("ĐÃ THU NHỎ", res.Message);
        var calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, Ok(), "")); };
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(new ShrinkAndCreateFix(Vol(), 2048, "H", "X", "CHIA D", run), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(new ShrinkAndCreateFix(Vol(), 2048, "H", "X", "CHIA D", run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Extend_Needs_Code_Space_And_Growth()
    {
        Assert.Throws<ArgumentException>(() => new ExtendIntoFreeSpaceFix(Vol(), "sai"));
        Assert.Throws<ArgumentException>(() => new ExtendIntoFreeSpaceFix(Vol(bus: "USB"), "MO RONG D"));
        var g = 1073741824L;
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(new ExtendIntoFreeSpaceFix(Vol(), "MO RONG D", Fake($"{{\"letter\":\"D\",\"before\":{g},\"after\":{3 * g}}}")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ExtendIntoFreeSpaceFix(Vol(), "MO RONG D", Fake("NOSPACE")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ExtendIntoFreeSpaceFix(Vol(), "MO RONG D", Fake($"{{\"letter\":\"D\",\"before\":{g},\"after\":{g}}}")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ExtendIntoFreeSpaceFix(Vol(), "MO RONG D", Fake("rác")), Real(), null, CancellationToken.None)).Outcome);
        var s = new ExtendIntoFreeSpaceFix(Vol(), "MO RONG D").BuildScript();
        Assert.DoesNotContain("Remove-Partition", s);
        Assert.DoesNotContain("Format-Volume", s);
    }

    [Fact]
    public void ChangeLetter_Blocks_System_And_Reserved()
    {
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("C", sys: true), "H", "DOI C"));
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("D", sys: true), "H", "DOI D"));
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("C"), "H", "DOI C"));
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("D"), "C", "DOI D"));
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("D"), "A", "DOI D"));
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("D"), "D", "DOI D"));
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("D"), "H", "doi d"));
        Assert.Throws<ArgumentException>(() => new ChangeLetterFix(Vol("D"), "H; calc", "DOI D"));
    }

    [Fact]
    public async Task ChangeLetter_Verifies_By_Reading_Letters()
    {
        ScriptRunner Run(string letters) => (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("Set-Partition") ? "" : letters, ""));
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(new ChangeLetterFix(Vol("D"), "H", "DOI D", Run("C\r\nE\r\nH\r\n")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(new ChangeLetterFix(Vol("D"), "H", "DOI D", Run("C\r\nD\r\n")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new ChangeLetterFix(Vol("D"), "H", "DOI D", Fake("", 1, "lỗi")), Real(), null, CancellationToken.None)).Outcome);
        var s = new ChangeLetterFix(Vol("D"), "H", "DOI D").BuildScript();
        Assert.Contains("SystemDrive", s);
        Assert.DoesNotContain("Remove-", s);
    }
}
