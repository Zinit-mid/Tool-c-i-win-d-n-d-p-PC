using PCTool.Core.Reinstall;
using PCTool.Core.Windows;

namespace PCTool.Tests.Reinstall;

public sealed class UsbClassifierTests
{
    private const long GB = 1_000_000_000L;

    private static PartitionInfo Part(string letter, long size, long? free, string fs = "NTFS") =>
        new(1, letter, size, "Basic", "", fs, free);

    private static DiskInfo Disk(int n, string bus, long size, string serial = "SN123456AB", string uid = "UID-1",
        bool sys = false, bool boot = false, bool offline = false, bool ro = false, PartitionInfo[]? parts = null) =>
        new(n, "Disk " + n, serial, uid, bus, size, sys, boot, offline, ro, "GPT", parts ?? Array.Empty<PartitionInfo>());

    private static readonly DiskInfo WinDisk = Disk(0, "NVMe", 500 * GB, "WIN0001", "UID-W", true, true, false, false, new[] { Part("C", 499 * GB, 100 * GB) });

    private static UsbScanResult Run(string? iso = null, string? backup = null, params DiskInfo[] disks) =>
        UsbClassifier.Classify(new DiskScan("C", disks.Prepend(WinDisk).ToList()), new UsbScanContext(iso, backup));

    // ---------------------------------------------------------------- Chỉ USB được liệt kê

    [Fact]
    public void Only_Usb_Disks_Listed_Others_Counted_Hidden()
    {
        var r = Run(null, null,
            Disk(1, "SATA", 1000 * GB, parts: new[] { Part("D", 999 * GB, 500 * GB) }),
            Disk(2, "USB", 32 * GB, parts: new[] { Part("E", 31 * GB, 31 * GB) }));
        Assert.Single(r.Candidates);
        Assert.Equal(2, r.Candidates[0].Disk.Number);
        Assert.Equal(2, r.HiddenNonUsbCount); // ổ NVMe Windows + ổ SATA
    }

    [Fact]
    public void Usb_BusType_Is_Case_Insensitive() =>
        Assert.Single(Run(null, null, Disk(3, "usb", 32 * GB)).Candidates);

    [Theory]
    [InlineData("NVMe")]
    [InlineData("SATA")]
    [InlineData("SD")]
    [InlineData("")]
    [InlineData("Unknown")]
    public void NonUsb_Bus_Never_Listed(string bus) =>
        Assert.Empty(Run(null, null, Disk(1, bus, 64 * GB)).Candidates);

    // ---------------------------------------------------------------- Lớp chặn

    [Fact]
    public void Healthy_Usb_Is_Eligible_With_Token()
    {
        var c = Run(null, null, Disk(2, "USB", 32 * GB, "ABCD1234WXYZ", parts: new[] { Part("E", 31 * GB, 31 * GB) })).Candidates.Single();
        Assert.True(c.Eligible);
        Assert.Equal("2-WXYZ", c.ConfirmationToken);
    }

    [Fact]
    public void System_Or_Boot_Flag_Blocks()
    {
        Assert.False(Run(null, null, Disk(2, "USB", 64 * GB, sys: true)).Candidates.Single().Eligible);
        Assert.False(Run(null, null, Disk(2, "USB", 64 * GB, boot: true)).Candidates.Single().Eligible);
    }

    [Fact]
    public void Disk_Holding_Windows_Drive_Letter_Blocks_Even_Without_Flags()
    {
        var c = Run(null, null, Disk(2, "USB", 64 * GB, parts: new[] { Part("C", 60 * GB, 10 * GB) })).Candidates.Single();
        Assert.False(c.Eligible);
        Assert.Contains(c.Blockers, b => b.Contains("Windows"));
    }

    [Fact]
    public void Disk_Holding_Iso_Drive_Blocks()
    {
        var c = Run(@"E:\iso\Win11.iso", null, Disk(2, "USB", 64 * GB, parts: new[] { Part("E", 60 * GB, 10 * GB) })).Candidates.Single();
        Assert.False(c.Eligible);
        Assert.Contains(c.Blockers, b => b.Contains("ISO"));
    }

    [Fact]
    public void Disk_Holding_Backup_Drive_Blocks_Lowercase_Path()
    {
        var c = Run(null, @"e:\SaoLuu", Disk(2, "USB", 64 * GB, parts: new[] { Part("E", 60 * GB, 10 * GB) })).Candidates.Single();
        Assert.False(c.Eligible);
        Assert.Contains(c.Blockers, b => b.Contains("sao lưu"));
    }

    [Fact]
    public void Other_Usb_Not_Blocked_By_Iso_On_Different_Drive()
    {
        var r = Run(@"D:\iso\x.iso", null,
            Disk(2, "USB", 64 * GB, parts: new[] { Part("E", 60 * GB, 10 * GB) }));
        Assert.True(r.Candidates.Single().Eligible);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Offline_Or_ReadOnly_Blocks(bool offline, bool ro) =>
        Assert.False(Run(null, null, Disk(2, "USB", 64 * GB, offline: offline, ro: ro)).Candidates.Single().Eligible);

    [Theory]
    [InlineData(0L)]
    [InlineData(8 * GB)]
    [InlineData(16 * GB)]
    [InlineData(27_999_999_999L)]
    public void Too_Small_Blocks(long size) =>
        Assert.False(Run(null, null, Disk(2, "USB", size)).Candidates.Single().Eligible);

    [Fact]
    public void Size_At_Threshold_Passes() =>
        Assert.True(Run(null, null, Disk(2, "USB", UsbClassifier.MinBytes)).Candidates.Single().Eligible);

    [Fact]
    public void Missing_Serial_Or_UniqueId_Blocks()
    {
        Assert.False(Run(null, null, Disk(2, "USB", 64 * GB, serial: "")).Candidates.Single().Eligible);
        Assert.False(Run(null, null, Disk(2, "USB", 64 * GB, uid: "")).Candidates.Single().Eligible);
    }

    [Fact]
    public void Blocked_Disk_Has_No_Confirmation_Token() =>
        Assert.Null(Run(null, null, Disk(2, "USB", 8 * GB)).Candidates.Single().ConfirmationToken);

    // ---------------------------------------------------------------- Cảnh báo

    [Fact]
    public void Big_Drive_Warns_But_Stays_Eligible()
    {
        var c = Run(null, null, Disk(2, "USB", 1000 * GB, parts: new[] { Part("E", 999 * GB, 100 * GB) })).Candidates.Single();
        Assert.True(c.Eligible);
        Assert.Contains(c.Warnings, w => w.Contains("128 GB"));
    }

    [Fact]
    public void Exactly_128GB_No_Big_Warning() =>
        Assert.DoesNotContain(Run(null, null, Disk(2, "USB", 128 * GB)).Candidates.Single().Warnings, w => w.Contains("128 GB"));

    [Fact]
    public void Used_Bytes_Summed_Across_Partitions()
    {
        var c = Run(null, null, Disk(2, "USB", 64 * GB, parts: new[] { Part("E", 30 * GB, 10 * GB), Part("F", 30 * GB, 25 * GB) })).Candidates.Single();
        Assert.Equal(25 * GB, c.UsedBytes);
        Assert.False(c.UsedBytesUnknown);
        Assert.Contains(c.Warnings, w => w.Contains("có dữ liệu"));
    }

    [Fact]
    public void Partition_Without_Free_Info_Marks_Unknown_And_Counts_As_Data()
    {
        var c = Run(null, null, Disk(2, "USB", 64 * GB, parts: new[] { Part("", 30 * GB, null, "") })).Candidates.Single();
        Assert.True(c.UsedBytesUnknown);
        Assert.Contains(c.Warnings, w => w.Contains("coi như đang có dữ liệu"));
    }

    [Fact]
    public void Empty_Disk_Has_Zero_Used_And_No_Data_Warning()
    {
        var c = Run(null, null, Disk(2, "USB", 64 * GB)).Candidates.Single();
        Assert.Equal(0, c.UsedBytes);
        Assert.DoesNotContain(c.Warnings, w => w.Contains("có dữ liệu"));
    }

    // ---------------------------------------------------------------- Xác nhận & kiểm tra lại

    [Theory]
    [InlineData("2-WXYZ", true)]
    [InlineData("  2-wxyz ", true)]
    [InlineData("2-WXYA", false)]
    [InlineData("3-WXYZ", false)]
    [InlineData("WXYZ", false)]
    [InlineData("2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Confirmation_Must_Match_Number_And_Last4(string? typed, bool ok) =>
        Assert.Equal(ok, UsbClassifier.ConfirmationMatches(Disk(2, "USB", 64 * GB, "ABCD1234WXYZ"), typed));

    [Fact]
    public void Short_Serial_Uses_Whole_Serial() =>
        Assert.Equal("5-AB", UsbClassifier.ConfirmationFor(Disk(5, "USB", 64 * GB, "ab")));

    [Fact]
    public void Empty_Serial_Cannot_Be_Confirmed() =>
        Assert.False(UsbClassifier.ConfirmationMatches(Disk(5, "USB", 64 * GB, ""), "5-"));

    [Fact]
    public void IsSameDisk_True_When_Everything_Matches()
    {
        var a = Disk(2, "USB", 64 * GB);
        Assert.True(UsbClassifier.IsSameDisk(a, a with { }));
    }

    [Fact]
    public void IsSameDisk_False_When_Number_Reassigned()
    {
        var a = Disk(2, "USB", 64 * GB);
        Assert.False(UsbClassifier.IsSameDisk(a, a with { Number = 3 }));
    }

    [Fact]
    public void IsSameDisk_False_When_Different_Device_Same_Number()
    {
        var a = Disk(2, "USB", 64 * GB);
        Assert.False(UsbClassifier.IsSameDisk(a, a with { UniqueId = "OTHER" }));
        Assert.False(UsbClassifier.IsSameDisk(a, a with { Serial = "OTHER" }));
        Assert.False(UsbClassifier.IsSameDisk(a, a with { Size = 32 * GB }));
    }

    [Fact]
    public void IsSameDisk_False_When_Gone_Or_No_Longer_Usb()
    {
        var a = Disk(2, "USB", 64 * GB);
        Assert.False(UsbClassifier.IsSameDisk(a, null));
        Assert.False(UsbClassifier.IsSameDisk(a, a with { BusType = "SATA" }));
    }

    [Fact]
    public void Candidates_Sorted_By_Number()
    {
        var r = Run(null, null, Disk(5, "USB", 64 * GB, uid: "u5"), Disk(2, "USB", 64 * GB, uid: "u2"));
        Assert.Equal(new[] { 2, 5 }, r.Candidates.Select(c => c.Disk.Number).ToArray());
    }

    [Theory]
    [InlineData(@"E:\a.iso", "E")]
    [InlineData(@"e:\a", "E")]
    [InlineData(@"\\srv\share", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DriveLetterOf(string? path, string? expected) => Assert.Equal(expected, UsbClassifier.DriveLetterOf(path));
}

public sealed class DiskListParserAndScannerTests
{
    private const string Good = """
{"systemDrive":"C","disks":[
 {"Number":0,"FriendlyName":"Samsung SSD","SerialNumber":"S1","UniqueId":"U0","BusType":"NVMe","Size":500000000000,"IsSystem":true,"IsBoot":true,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"GPT",
  "Partitions":[{"PartitionNumber":3,"DriveLetter":"C","Size":499000000000,"Type":"Basic","Label":"","FileSystem":"NTFS","Free":100000000000}]},
 {"Number":1,"FriendlyName":"SanDisk Ultra","SerialNumber":"4C530001","UniqueId":"U1","BusType":"USB","Size":32000000000,"IsSystem":false,"IsBoot":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"MBR",
  "Partitions":{"PartitionNumber":1,"DriveLetter":"E","Size":31000000000,"Type":"IFS","Label":"USB","FileSystem":"FAT32","Free":30000000000}}
]}
""";

    private static ScriptRunner Fake(string output, string error = "") =>
        (_, _) => Task.FromResult(new PowerShellResult(0, output, error));

    [Fact]
    public void Parse_Reads_Disks_And_Tolerates_Single_Object_Partitions()
    {
        var scan = DiskListParser.Parse(Good);
        Assert.Equal("C", scan.SystemDriveLetter);
        Assert.Equal(2, scan.Disks.Count);
        var usb = scan.Disks[1];
        Assert.Equal("USB", usb.BusType);
        Assert.Single(usb.Partitions);
        Assert.Equal("E", usb.Partitions[0].DriveLetter);
        Assert.Equal(1_000_000_000L, usb.Partitions[0].UsedBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"systemDrive\":\"C\",\"disks\":[{\"FriendlyName\":\"x\"}]}")]
    public void Parse_Bad_Input_Throws_FormatException(string json) =>
        Assert.Throws<FormatException>(() => DiskListParser.Parse(json));

    [Fact]
    public async Task Scanner_Returns_Classified_Result()
    {
        var r = await new UsbDriveScanner(Fake(Good)).ScanAsync(new UsbScanContext(), CancellationToken.None);
        var c = Assert.Single(r.Candidates);
        Assert.Equal(1, c.Disk.Number);
        Assert.True(c.Eligible);
        Assert.Equal(1, r.HiddenNonUsbCount);
    }

    [Fact]
    public async Task Scanner_Script_Is_ReadOnly()
    {
        string? seen = null;
        await new UsbDriveScanner((s, _) => { seen = s; return Task.FromResult(new PowerShellResult(0, Good, "")); })
            .ScanAsync(new UsbScanContext(), CancellationToken.None);
        Assert.NotNull(seen);
        foreach (var bad in new[] { "Clear-Disk", "Remove-Partition", "Format-Volume", "New-Partition", "Set-Disk", "Initialize-Disk",
                     "Set-Partition", "Remove-Item", "diskpart", "format " })
            Assert.DoesNotContain(bad, seen!, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("$ErrorActionPreference", seen!.TrimStart());
    }

    [Fact]
    public async Task Scanner_Empty_Output_Throws_With_Error_Text()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UsbDriveScanner(Fake("", "Access denied")).ScanAsync(new UsbScanContext(), CancellationToken.None));
        Assert.Contains("Access denied", ex.Message);
    }

    [Fact]
    public async Task Scanner_Refuses_When_Windows_Drive_Not_Seen()
    {
        // Không ổ nào chứa ký tự ổ Windows: dữ liệu đọc hỏng, dừng thay vì tin rằng không có ổ hệ thống.
        const string noSys = """
{"systemDrive":"C","disks":[{"Number":1,"FriendlyName":"U","SerialNumber":"S","UniqueId":"U","BusType":"USB","Size":32000000000,"IsSystem":false,"IsBoot":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"MBR","Partitions":[]}]}
""";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UsbDriveScanner(Fake(noSys)).ScanAsync(new UsbScanContext(), CancellationToken.None));
    }

    [Fact]
    public async Task Scanner_Bad_Json_Throws_InvalidOperation() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UsbDriveScanner(Fake("{oops")).ScanAsync(new UsbScanContext(), CancellationToken.None));
}

public sealed class UsbReportTests
{
    private const long GB = 1_000_000_000L;

    private static UsbScanResult Scan(params DiskInfo[] usb) =>
        UsbClassifier.Classify(new DiskScan("C", new[]
        {
            new DiskInfo(0, "SSD", "W", "UW", "NVMe", 500 * GB, true, true, false, false, "GPT",
                new[] { new PartitionInfo(1, "C", 499 * GB, "Basic", "", "NTFS", 100 * GB) })
        }.Concat(usb).ToList()), new UsbScanContext());

    [Fact]
    public void No_Usb_Says_So()
    {
        var t = UsbReport.ToText(Scan());
        Assert.Contains("Không thấy ổ USB nào", t);
        Assert.Contains("C:", t);
    }

    [Fact]
    public void Eligible_Usb_Shows_Token_And_Notice_Nothing_Written()
    {
        var d = new DiskInfo(3, "SanDisk", "ABCD1234", "U3", "USB", 32 * GB, false, false, false, false, "MBR",
            new[] { new PartitionInfo(1, "E", 31 * GB, "IFS", "USB", "FAT32", 30 * GB) });
        var t = UsbReport.ToText(Scan(d));
        Assert.Contains("Ổ số 3", t);
        Assert.Contains("3-1234", t);
        Assert.Contains("Chưa có gì bị ghi hay xóa", t);
    }

    [Fact]
    public void Blocked_Usb_Shows_Reason_And_No_Token()
    {
        var d = new DiskInfo(3, "Small", "ABCD1234", "U3", "USB", 8 * GB, false, false, false, false, "MBR", Array.Empty<PartitionInfo>());
        var t = UsbReport.ToText(Scan(d));
        Assert.Contains("KHÔNG CHỌN ĐƯỢC", t);
        Assert.DoesNotContain("3-1234", t);
    }

    [Theory]
    [InlineData(32_000_000_000L, "32 GB")]
    [InlineData(31_900_000_000L, "31.9 GB")]
    [InlineData(500_000_000L, "500 MB")]
    public void Size_Format(long bytes, string expected) => Assert.Equal(expected, UsbReport.Size(bytes));
}
