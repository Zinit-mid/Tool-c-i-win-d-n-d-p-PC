using System.Text;
using System.Xml;
using PCTool.Core.Framework;
using PCTool.Core.Reinstall;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Reinstall;

public sealed class AnswerFileTests
{
    private const long GB = 1_000_000_000L;

    private static IsoInfo Iso(int build = 22631, params int[] indexes)
    {
        var idx = indexes.Length == 0 ? new[] { 1, 6 } : indexes;
        return new IsoInfo(@"D:\Win.iso", 6_500_000_000L, false, true, 5_200_000_000L, false, 0, true,
            idx.Select(i => new IsoImage(i, "Windows", "Professional", $"10.0.{build}.1", "9", new[] { "vi-VN" }, 5 * GB)).ToList());
    }

    // ------------------------------------------------------------ Trang tải ISO

    [Fact]
    public void Download_Pages_Are_Official_And_Allow_List_Only()
    {
        foreach (var u in new[] { IsoDownloadPages.Windows10, IsoDownloadPages.Windows11 })
        {
            Assert.StartsWith("https://www.microsoft.com/", u);
            Assert.True(IsoDownloadPages.IsAllowed(u));
        }
        var opened = new List<string>();
        IsoDownloadPages.Open(IsoDownloadPages.Windows11, opened.Add);
        Assert.Equal(new[] { IsoDownloadPages.Windows11 }, opened);
        foreach (var bad in new[] { "https://evil.com", "http://www.microsoft.com/software-download/windows11", "https://www.microsoft.com/software-download/windows11?x=1", "", null })
            Assert.Throws<ArgumentException>(() => IsoDownloadPages.Open(bad!, opened.Add));
        Assert.Single(opened);
    }

    // ------------------------------------------------------------ Nội dung file

    [Fact]
    public void Build_Is_Valid_Xml_With_Language_Eula_And_Empty_Key()
    {
        var xml = AnswerFileBuilder.Build(new AnswerFileOptions("vi-VN"));
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        Assert.Contains("<UILanguage>vi-VN</UILanguage>", xml);
        Assert.Contains("<AcceptEula>true</AcceptEula>", xml);
        Assert.Contains("<Key></Key>", xml);
        Assert.DoesNotContain("LabConfig", xml);
        Assert.DoesNotContain("/IMAGE/INDEX", xml);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 6)]
    [InlineData(true, 1)]
    public void Build_Never_Contains_Disk_Or_Account_Settings(bool bypass, int? index)
    {
        var xml = AnswerFileBuilder.Build(new AnswerFileOptions("en-US", index, bypass));
        foreach (var bad in AnswerFileBuilder.Forbidden)
            Assert.DoesNotContain(bad, xml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<WillShowUI>Always</WillShowUI>", xml);   // vẫn hiện màn hình chọn của Microsoft
        new XmlDocument().LoadXml(xml);
    }

    [Fact]
    public void Build_Edition_And_Bypass_Appear_Only_When_Asked()
    {
        var xml = AnswerFileBuilder.Build(new AnswerFileOptions("vi-VN", 6, true));
        Assert.Contains("<Key>/IMAGE/INDEX</Key><Value>6</Value>", xml);
        foreach (var v in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassStorageCheck", "BypassCPUCheck" })
            Assert.Contains($@"reg add HKLM\SYSTEM\Setup\LabConfig /v {v} /t REG_DWORD /d 1 /f", xml);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vi")]
    [InlineData("vi-VN<x>")]
    [InlineData("vi-VN'; calc; '")]
    [InlineData("VI-vn ")]
    public void Build_Rejects_Bad_Language(string lang) => Assert.Throws<ArgumentException>(() => AnswerFileBuilder.Build(new AnswerFileOptions(lang)));

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-1)]
    public void Build_Rejects_Bad_Index(int i) => Assert.Throws<ArgumentException>(() => AnswerFileBuilder.Build(new AnswerFileOptions("vi-VN", i)));

    [Fact]
    public void Validate_Checks_Against_Iso()
    {
        Assert.Empty(AnswerFileBuilder.Validate(new AnswerFileOptions("vi-VN", 6, true), Iso()));
        Assert.NotEmpty(AnswerFileBuilder.Validate(new AnswerFileOptions("vi-VN", 7), Iso()));              // index không có trong ISO
        Assert.NotEmpty(AnswerFileBuilder.Validate(new AnswerFileOptions("vi-VN", null, true), Iso(19045))); // bypass chỉ cho Win 11
        Assert.NotEmpty(AnswerFileBuilder.Validate(new AnswerFileOptions("bad"), Iso()));
        Assert.Empty(AnswerFileBuilder.Validate(new AnswerFileOptions("vi-VN"), Iso(19045)));               // Win 10, không bypass: ổn
    }

    // ------------------------------------------------------------ Ghi qua UsbWriteFix

    private static string ScanJson() => """
{"systemDrive":"C","disks":[
 {"Number":0,"FriendlyName":"SSD","SerialNumber":"W1","UniqueId":"UW","BusType":"NVMe","Size":500000000000,"IsSystem":true,"IsBoot":true,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"GPT",
  "Partitions":[{"PartitionNumber":1,"DriveLetter":"C","Size":499000000000,"Type":"Basic","Label":"","FileSystem":"NTFS","Free":100000000000}]},
 {"Number":1,"FriendlyName":"SanDisk","SerialNumber":"4C530001","UniqueId":"U1","BusType":"USB","Size":32000000000,"IsSystem":false,"IsBoot":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"MBR",
  "Partitions":[{"PartitionNumber":1,"DriveLetter":"E","Size":31000000000,"Type":"IFS","Label":"USB","FileSystem":"FAT32","Free":30000000000}]}
]}
""";

    private const string VerifyWithAnswer = """{"style":"GPT","bootDisk":1,"dataDisk":1,"bootLabel":"PCTOOL-BOOT","bootFs":"FAT32","dataLabel":"PCTOOL-DATA","dataFs":"NTFS","efi":true,"bootWim":true,"answer":true,"hasSwm":true,"hasWim":false,"hasEsd":false,"installBytes":5200000000}""";
    private const string VerifyNoAnswer = VerifyWithAnswer;

    private sealed class R
    {
        public List<string> Scripts { get; } = new();
        public PowerShellResult Answer = new(0, "OK", "");
        public string Verify = VerifyWithAnswer;
        public Task<PowerShellResult> Run(string s, CancellationToken ct)
        {
            Scripts.Add(s);
            if (s.Contains("Clear-Disk")) return Task.FromResult(new PowerShellResult(0, "OK|E|F", ""));
            if (s.Contains("robocopy")) return Task.FromResult(new PowerShellResult(0, "OK", ""));
            if (s.Contains("FromBase64String")) return Task.FromResult(Answer);
            if (s.Contains("installBytes")) return Task.FromResult(new PowerShellResult(0, Verify, ""));
            return Task.FromResult(new PowerShellResult(0, ScanJson(), ""));
        }
    }

    private static UsbWriteFix Make(R r, UsbWriteOptions? opt, IsoInfo? iso = null)
    {
        var c = UsbClassifier.Classify(DiskListParser.Parse(ScanJson()), new UsbScanContext(@"D:\Win.iso", null)).Candidates.Single(x => x.Disk.Number == 1);
        return new UsbWriteFix(c, c.ConfirmationToken!, iso ?? Iso(), null, r.Run, opt);
    }

    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    [Fact]
    public async Task Without_Options_No_Answer_Script_Runs()
    {
        var r = new R();
        var res = await Exec().RunAsync(Make(r, null), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.DoesNotContain(r.Scripts, s => s.Contains("FromBase64String"));
    }

    [Fact]
    public async Task Answer_Written_After_Copy_And_Verified()
    {
        var r = new R();
        var res = await Exec().RunAsync(Make(r, new UsbWriteOptions(new AnswerFileOptions("vi-VN", null, false))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        int copy = r.Scripts.FindIndex(s => s.Contains("robocopy"));
        int ans = r.Scripts.FindIndex(s => s.Contains("FromBase64String"));
        Assert.True(copy >= 0 && ans > copy);
        var script = r.Scripts[ans];
        var b64 = script.Split("FromBase64String('")[1].Split("')")[0];
        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Contains("vi-VN", xml);
        foreach (var bad in AnswerFileBuilder.Forbidden) Assert.DoesNotContain(bad, xml, StringComparison.OrdinalIgnoreCase);
        foreach (var bad in new[] { "Remove-Item", "Clear-Disk", "Format-", "robocopy" }) Assert.DoesNotContain(bad, script);
        Assert.Contains("$boot = 'E'", script);
    }

    [Fact]
    public async Task Answer_Write_Failure_Fails()
    {
        var r = new R { Answer = new PowerShellResult(1, "", "đầy ổ") };
        var res = await Exec().RunAsync(Make(r, new UsbWriteOptions(new AnswerFileOptions("vi-VN"))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("autounattend", res.Message);
    }

    [Fact]
    public async Task Verify_Fails_When_Answer_File_Missing()
    {
        var r = new R { Verify = VerifyWithAnswer.Replace("\"answer\":true", "\"answer\":false") };
        var res = await Exec().RunAsync(Make(r, new UsbWriteOptions(new AnswerFileOptions("vi-VN"))), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.StillPresent, res.Outcome);
    }

    [Fact]
    public async Task Answer_Not_Required_By_Verify_When_Not_Asked()
    {
        var r = new R { Verify = VerifyWithAnswer.Replace("\"answer\":true", "\"answer\":false") };
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(Make(r, null), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Bad_Options_Reject_Before_Anything_Runs()
    {
        var r = new R();
        Assert.Throws<ArgumentException>(() => Make(r, new UsbWriteOptions(new AnswerFileOptions("bad"))));
        Assert.Throws<ArgumentException>(() => Make(r, new UsbWriteOptions(new AnswerFileOptions("vi-VN", null, true)), Iso(19045)));
        Assert.Empty(r.Scripts.Where(s => s.Contains("Clear-Disk")));
    }

    [Fact]
    public void Actions_Say_Disk_Is_Still_Your_Choice_And_Warn_On_Bypass()
    {
        var plain = Make(new R(), new UsbWriteOptions(new AnswerFileOptions("vi-VN"))).Actions;
        Assert.Contains(plain, a => a.Contains("autounattend") && a.Contains("KHÔNG xóa ổ cài"));
        var bypass = Make(new R(), new UsbWriteOptions(new AnswerFileOptions("vi-VN", null, true))).Actions;
        Assert.Contains(bypass, a => a.Contains("không chính thức"));
        Assert.DoesNotContain(Make(new R(), null).Actions, a => a.Contains("autounattend"));
    }

    [Fact]
    public async Task DryRun_And_SafeMode_Write_Nothing()
    {
        var r = new R();
        var o = new UsbWriteOptions(new AnswerFileOptions("vi-VN"));
        await Exec().RunAsync(Make(r, o), new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(Make(r, o), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.DoesNotContain(r.Scripts, s => s.Contains("Clear-Disk") || s.Contains("FromBase64String"));
    }

    [Fact]
    public void Verifier_Checks_Answer_Only_When_Expected()
    {
        var noAnswer = VerifyWithAnswer.Replace("\"answer\":true,", "");
        Assert.Empty(UsbWriteVerifier.Problems(noAnswer, 1, Iso(), true, false));
        Assert.Contains(UsbWriteVerifier.Problems(noAnswer, 1, Iso(), true, true), p => p.Contains("autounattend"));
        Assert.Empty(UsbWriteVerifier.Problems(VerifyWithAnswer, 1, Iso(), true, true));
    }
}
