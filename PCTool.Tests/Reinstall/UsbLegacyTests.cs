using PCTool.Core.Framework;
using PCTool.Core.Reinstall;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Reinstall;

public sealed class UsbLegacyTests
{
    private const long GB = 1_000_000_000L;

    private static IsoInfo Iso() =>
        new(@"D:\Win.iso", 6_500_000_000L, false, true, 5_200_000_000L, false, 0, true,
            new List<IsoImage> { new(1, "Windows", "Professional", "10.0.22631.1", "9", new[] { "vi-VN" }, 5 * GB) });

    private const string Scan = """
{"systemDrive":"C","disks":[
 {"Number":0,"FriendlyName":"SSD","SerialNumber":"W1","UniqueId":"UW","BusType":"NVMe","Size":500000000000,"IsSystem":true,"IsBoot":true,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"GPT",
  "Partitions":[{"PartitionNumber":1,"DriveLetter":"C","Size":499000000000,"Type":"Basic","Label":"","FileSystem":"NTFS","Free":100000000000}]},
 {"Number":1,"FriendlyName":"SanDisk","SerialNumber":"4C530001","UniqueId":"U1","BusType":"USB","Size":32000000000,"IsSystem":false,"IsBoot":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"MBR",
  "Partitions":[{"PartitionNumber":1,"DriveLetter":"E","Size":31000000000,"Type":"IFS","Label":"USB","FileSystem":"FAT32","Free":30000000000}]}
]}
""";

    private const string GoodLegacy = """{"style":"MBR","bootDisk":1,"dataDisk":1,"bootLabel":"PCTOOL-BOOT","bootFs":"FAT32","dataLabel":"PCTOOL-DATA","dataFs":"NTFS","efi":false,"active":true,"bootmgr":true,"bcd":true,"bootWim":true,"hasSwm":true,"hasWim":false,"hasEsd":false,"installBytes":5200000000}""";

    private sealed class R
    {
        public List<string> Scripts { get; } = new();
        public PowerShellResult Boot = new(0, "OK", "");
        public string Verify = GoodLegacy;
        public Task<PowerShellResult> Run(string s, CancellationToken ct)
        {
            Scripts.Add(s);
            if (s.Contains("Clear-Disk")) return Task.FromResult(new PowerShellResult(0, "OK|E|F", ""));
            if (s.Contains("bootsect")) return Task.FromResult(Boot);
            if (s.Contains("robocopy") || s.Contains("FromBase64String")) return Task.FromResult(new PowerShellResult(0, "OK", ""));
            if (s.Contains("installBytes")) return Task.FromResult(new PowerShellResult(0, Verify, ""));
            return Task.FromResult(new PowerShellResult(0, Scan, ""));
        }
    }

    private static UsbWriteFix Make(R r, UsbWriteOptions? o)
    {
        var c = UsbClassifier.Classify(DiskListParser.Parse(Scan), new UsbScanContext(@"D:\Win.iso", null)).Candidates.Single(x => x.Disk.Number == 1);
        return new UsbWriteFix(c, c.ConfirmationToken!, Iso(), null, r.Run, o);
    }

    private static readonly UsbWriteOptions Legacy = new(null, UsbBootMode.Legacy);
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    [Fact]
    public void Default_Is_Uefi_Gpt_And_Unchanged()
    {
        Assert.Equal(UsbBootMode.Uefi, UsbWriteOptions.Default.Mode);
        var script = Make(new R(), null).BuildPartitionScript();
        Assert.Contains("-PartitionStyle GPT", script);
        Assert.DoesNotContain("-IsActive", script);
    }

    [Fact]
    public void Legacy_Partition_Script_Uses_Mbr_And_Active_And_Keeps_Safety_Checks()
    {
        var script = Make(new R(), Legacy).BuildPartitionScript();
        Assert.Contains("-PartitionStyle MBR", script);
        Assert.DoesNotContain("-PartitionStyle GPT", script);
        Assert.Contains("-AssignDriveLetter -IsActive", script);
        foreach (var guard in new[] { "'USB'", "IsSystem", "UniqueId", "SerialNumber", "[long]$disk.Size" })
            Assert.Contains(guard, script);
    }

    [Fact]
    public void BootSector_Script_Touches_Only_Chosen_Partition_And_Uses_Iso_Bootsect()
    {
        var script = Make(new R(), Legacy).BuildBootSectorScript("E");
        Assert.Contains("DiskNumber -ne 1", script);
        Assert.Contains("bootsect.exe", script);
        Assert.Contains("/nt60 ($boot + ':') /force /mbr", script);
        foreach (var bad in new[] { "Clear-Disk", "Format-", "Remove-Item", "robocopy", "Set-Disk", "diskpart" })
            Assert.DoesNotContain(bad, script);
        Assert.Throws<ArgumentException>(() => Make(new R(), Legacy).BuildBootSectorScript("E; calc"));
    }

    [Fact]
    public async Task Legacy_Run_Orders_Partition_Copy_BootSector_Verify()
    {
        var r = new R();
        var res = await Exec().RunAsync(Make(r, Legacy), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        int part = r.Scripts.FindIndex(s => s.Contains("Clear-Disk"));
        int copy = r.Scripts.FindIndex(s => s.Contains("robocopy"));
        int boot = r.Scripts.FindIndex(s => s.Contains("bootsect"));
        int ver = r.Scripts.FindIndex(s => s.Contains("installBytes"));
        Assert.True(part < copy && copy < boot && boot < ver);
    }

    [Fact]
    public async Task Uefi_Run_Never_Calls_Bootsect()
    {
        var r = new R { Verify = GoodLegacy.Replace("\"style\":\"MBR\"", "\"style\":\"GPT\"").Replace("\"efi\":false", "\"efi\":true") };
        var res = await Exec().RunAsync(Make(r, null), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.DoesNotContain(r.Scripts, s => s.Contains("bootsect"));
    }

    [Fact]
    public async Task BootSector_Failure_Fails_With_Clear_Message()
    {
        var r = new R { Boot = new PowerShellResult(1, "", "ISO không có boot\\bootsect.exe") };
        var res = await Exec().RunAsync(Make(r, Legacy), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("bootsect", res.Message);
        Assert.DoesNotContain(r.Scripts, s => s.Contains("installBytes"));
    }

    [Fact]
    public async Task Legacy_With_Answer_Writes_Both_Bootsector_Then_Answer()
    {
        var r = new R();
        var res = await Exec().RunAsync(Make(r, new UsbWriteOptions(new AnswerFileOptions("vi-VN"), UsbBootMode.Legacy)), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.StillPresent, res.Outcome);   // verify giả không có "answer": đúng, vì yêu cầu autounattend
        Assert.True(r.Scripts.FindIndex(s => s.Contains("bootsect")) < r.Scripts.FindIndex(s => s.Contains("FromBase64String")));
    }

    [Fact]
    public async Task DryRun_And_SafeMode_Run_Nothing()
    {
        var r = new R();
        await Exec().RunAsync(Make(r, Legacy), new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(Make(r, Legacy), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.DoesNotContain(r.Scripts, s => s.Contains("Clear-Disk") || s.Contains("bootsect"));
    }

    [Fact]
    public void Actions_Describe_Legacy_Mode()
    {
        var a = Make(new R(), Legacy).Actions;
        Assert.Contains(a, x => x.Contains("MBR") && x.Contains("Active"));
        Assert.Contains(a, x => x.Contains("bootsect"));
        Assert.Contains(a, x => x.Contains("bootmgr"));
        Assert.DoesNotContain(Make(new R(), null).Actions, x => x.Contains("MBR") || x.Contains("bootsect"));
    }

    [Fact]
    public void Verifier_Legacy_Rules()
    {
        Assert.Empty(UsbWriteVerifier.Problems(GoodLegacy, 1, Iso(), true, false, UsbBootMode.Legacy));
        Assert.Contains(UsbWriteVerifier.Problems(GoodLegacy.Replace("\"active\":true", "\"active\":false"), 1, Iso(), true, false, UsbBootMode.Legacy), p => p.Contains("Active"));
        Assert.Contains(UsbWriteVerifier.Problems(GoodLegacy.Replace("\"bootmgr\":true", "\"bootmgr\":false"), 1, Iso(), true, false, UsbBootMode.Legacy), p => p.Contains("bootmgr"));
        Assert.Contains(UsbWriteVerifier.Problems(GoodLegacy.Replace("\"bcd\":true", "\"bcd\":false"), 1, Iso(), true, false, UsbBootMode.Legacy), p => p.Contains("bcd"));
        Assert.Contains(UsbWriteVerifier.Problems(GoodLegacy.Replace("MBR", "GPT"), 1, Iso(), true, false, UsbBootMode.Legacy), p => p.Contains("MBR"));
        // Uefi mode vẫn đòi GPT + efi
        Assert.Contains(UsbWriteVerifier.Problems(GoodLegacy, 1, Iso(), true), p => p.Contains("GPT"));
        Assert.Contains(UsbWriteVerifier.Problems(GoodLegacy.Replace("MBR", "GPT"), 1, Iso(), true), p => p.Contains("bootx64.efi"));
    }
}
