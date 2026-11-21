using PCTool.Core.Framework;
using PCTool.Core.Reinstall;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Reinstall;

public sealed class UsbWriteFixTests
{
    private const long GB = 1_000_000_000L;
    private const string GoodVerify = """{"style":"GPT","bootDisk":1,"dataDisk":1,"bootLabel":"PCTOOL-BOOT","bootFs":"FAT32","dataLabel":"PCTOOL-DATA","dataFs":"NTFS","efi":true,"bootWim":true,"hasSwm":true,"hasWim":false,"hasEsd":false,"installBytes":5200000000}""";

    private static string ScanJson(string uid = "U1", string serial = "4C530001", long size = 32_000_000_000, string usbLetter = "E", string bus = "USB") => $$"""
{"systemDrive":"C","disks":[
 {"Number":0,"FriendlyName":"SSD","SerialNumber":"W1","UniqueId":"UW","BusType":"NVMe","Size":500000000000,"IsSystem":true,"IsBoot":true,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"GPT",
  "Partitions":[{"PartitionNumber":1,"DriveLetter":"C","Size":499000000000,"Type":"Basic","Label":"","FileSystem":"NTFS","Free":100000000000}]},
 {"Number":1,"FriendlyName":"SanDisk","SerialNumber":"{{serial}}","UniqueId":"{{uid}}","BusType":"{{bus}}","Size":{{size}},"IsSystem":false,"IsBoot":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"MBR",
  "Partitions":[{"PartitionNumber":1,"DriveLetter":"{{usbLetter}}","Size":31000000000,"Type":"IFS","Label":"USB","FileSystem":"FAT32","Free":30000000000}]}
]}
""";

    private sealed class Runner
    {
        public string Scan = ScanJson();
        public List<string> Scripts { get; } = new();
        public PowerShellResult Partition = new(0, "OK|E|F", "");
        public PowerShellResult Copy = new(0, "OK", "");
        public PowerShellResult Verify = new(0, GoodVerify, "");

        public Task<PowerShellResult> Run(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            if (script.Contains("Clear-Disk")) return Task.FromResult(Partition);
            if (script.Contains("robocopy")) return Task.FromResult(Copy);
            if (script.Contains("installBytes")) return Task.FromResult(Verify);
            return Task.FromResult(new PowerShellResult(0, Scan, ""));
        }

        public int Count(string needle) => Scripts.Count(s => s.Contains(needle));
        public bool Wiped => Count("Clear-Disk") > 0;
    }

    private static IsoInfo Iso(long wim = 5_200_000_000L, bool esd = false, long esdSize = 0, bool efi = true) =>
        new(@"D:\Win11.iso", 6_500_000_000L, false, !esd || wim > 0, wim, esd, esdSize, efi,
            new[] { new IsoImage(1, "Windows 11 Pro", "Professional", "10.0.22631.1", "9", new[] { "en-US" }, 5 * GB) });

    private static UsbCandidate Candidate(string json) =>
        UsbClassifier.Classify(DiskListParser.Parse(json), new UsbScanContext(@"D:\Win11.iso", null)).Candidates.Single(c => c.Disk.Number == 1);

    private static string Token(UsbCandidate c) => c.ConfirmationToken!;

    private static UsbWriteFix Make(Runner r, IsoInfo? iso = null, string? typed = null)
    {
        var c = Candidate(r.Scan);
        return new UsbWriteFix(c, typed ?? Token(c), iso ?? Iso(), null, r.Run);
    }

    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };

    // ---------------------------------------------------------------- Không tạo được nếu thiếu điều kiện

    [Fact]
    public void Wrong_Confirmation_Rejected()
    {
        var r = new Runner();
        var c = Candidate(r.Scan);
        Assert.Throws<ArgumentException>(() => new UsbWriteFix(c, "1-0000", Iso(), null, r.Run));
        Assert.Throws<ArgumentException>(() => new UsbWriteFix(c, "", Iso(), null, r.Run));
        Assert.Throws<ArgumentException>(() => new UsbWriteFix(c, "0001", Iso(), null, r.Run));
    }

    [Fact]
    public void Confirmation_Case_Insensitive_Accepted()
    {
        var r = new Runner { Scan = ScanJson(serial: "abcd1234wxyz") };
        Assert.NotNull(Make(r, typed: "1-wxyz"));
    }

    [Fact]
    public void Blocked_Candidate_Rejected()
    {
        var r = new Runner { Scan = ScanJson(size: 8 * GB) };
        var c = Candidate(r.Scan);
        Assert.False(c.Eligible);
        Assert.Throws<ArgumentException>(() => new UsbWriteFix(c, "1-1234", Iso(), null, r.Run));
    }

    [Fact]
    public void Non_Usb_Rejected()
    {
        var r = new Runner { Scan = ScanJson(bus: "SATA") };
        Assert.Throws<InvalidOperationException>(() => Candidate(r.Scan)); // không có ứng viên: ổ không phải USB không hề được liệt kê
    }

    [Fact]
    public void Unusable_Iso_Rejected()
    {
        var r = new Runner();
        Assert.Throws<ArgumentException>(() => Make(r, Iso(efi: false)));
    }

    [Fact]
    public void Serial_With_Quote_Rejected()
    {
        var r = new Runner { Scan = ScanJson(serial: "AB'; calc; '1234") };
        var c = Candidate(r.Scan);
        Assert.Throws<ArgumentException>(() => new UsbWriteFix(c, Token(c), Iso(), null, r.Run));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("USBSTOR\\DISK&VEN_SANDISK{123}", true)]
    [InlineData("a'b", false)]
    [InlineData("a\nb", false)]
    [InlineData("tiếng việt", false)]
    [InlineData("", false)]
    public void EmbedSafe(string s, bool ok) => Assert.Equal(ok, UsbWriteFix.EmbedSafe(s));

    [Fact]
    public void Too_Small_For_Layout_Rejected()
    {
        // 28 GB đủ ngưỡng chung nhưng ISO quá lớn: phân vùng cài 30 GiB không chừa chỗ cho dữ liệu.
        var r = new Runner { Scan = ScanJson(size: 28 * GB) };
        var c = Candidate(r.Scan);
        var iso = Iso() with { IsoSizeBytes = 40 * GB };
        Assert.Throws<ArgumentException>(() => new UsbWriteFix(c, Token(c), iso, null, r.Run));
    }

    // ---------------------------------------------------------------- Cách chia

    [Fact]
    public void Layout_Typical_32GB_With_6_5GB_Iso()
    {
        var l = UsbLayoutPlanner.Plan(31_900_000_000L, 6_500_000_000L, out var err)!;
        Assert.Equal("", err);
        Assert.Equal(8L << 30, l.BootBytes);
        Assert.True(l.DataBytes >= 2L << 30);
    }

    [Theory]
    [InlineData(0L, 6_500_000_000L)]
    [InlineData(32_000_000_000L, 0L)]
    public void Layout_Invalid_Input_Null(long disk, long iso) => Assert.Null(UsbLayoutPlanner.Plan(disk, iso, out _));

    [Fact]
    public void Layout_Boot_Capped_At_30GiB_And_Min_8GiB()
    {
        Assert.Equal(30L << 30, UsbLayoutPlanner.Plan(1_000_000_000_000L, 100L << 30, out _)!.BootBytes);
        Assert.Equal(8L << 30, UsbLayoutPlanner.Plan(64_000_000_000L, 1_000_000_000L, out _)!.BootBytes);
    }

    [Fact]
    public void Layout_Never_Exceeds_Disk()
    {
        var l = UsbLayoutPlanner.Plan(32_000_000_000L, 6_500_000_000L, out _)!;
        Assert.True(l.BootBytes + l.DataBytes < 32_000_000_000L);
    }

    // ---------------------------------------------------------------- Chạy thử / Chế độ an toàn

    [Fact]
    public async Task DryRun_Runs_No_Script_At_All()
    {
        var r = new Runner();
        var res = await Exec().RunAsync(Make(r), new FixOptions { DryRun = true }, null, CancellationToken.None);
        Assert.Equal(FixOutcome.Simulated, res.Outcome);
        Assert.Empty(r.Scripts);
    }

    [Fact]
    public async Task SafeMode_Blocks_Even_Real_Run()
    {
        var r = new Runner();
        var res = await Exec().RunAsync(Make(r), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.Equal(FixOutcome.Blocked, res.Outcome);
        Assert.Empty(r.Scripts);
    }

    [Fact]
    public void Actions_State_The_Deletion_Clearly()
    {
        var a = string.Join("\n", Make(new Runner()).Actions);
        Assert.Contains("XÓA TOÀN BỘ", a);
        Assert.Contains("4C530001", a);
        Assert.Contains("ổ USB số 1", a);
        Assert.Contains("HỦY", a);
    }

    [Fact]
    public void Fix_Is_Not_Undoable_And_Needs_No_RestorePoint()
    {
        var f = Make(new Runner());
        Assert.False(f.IsUndoable);
        Assert.False(f.RequiresRestorePoint);
    }

    // ---------------------------------------------------------------- Đường chính

    [Fact]
    public async Task Happy_Path_Scans_Then_Wipes_Then_Copies_Then_Verifies()
    {
        var r = new Runner();
        var res = await Exec().RunAsync(Make(r), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);

        var wipeIdx = r.Scripts.FindIndex(s => s.Contains("Clear-Disk"));
        var copyIdx = r.Scripts.FindIndex(s => s.Contains("robocopy"));
        var verIdx = r.Scripts.FindIndex(s => s.Contains("installBytes"));
        Assert.True(wipeIdx > 0, "phải quét lại ổ TRƯỚC khi xóa");
        Assert.Contains("systemDrive", r.Scripts[0]);
        Assert.True(wipeIdx < copyIdx && copyIdx < verIdx);
        Assert.Contains("'E'", r.Scripts[copyIdx]);    // đích chép là ký tự ổ phân vùng khởi động do bước 1 trả về
        Assert.Contains("$boot = 'E'", r.Scripts[copyIdx]);
        Assert.Contains("$b = 'E'", r.Scripts[verIdx]);
        Assert.Contains("$dl = 'F'", r.Scripts[verIdx]);
    }

    [Fact]
    public async Task Partition_Script_Guards_And_Targets_Only_Chosen_Disk()
    {
        var r = new Runner();
        var f = Make(r);
        var s = f.BuildPartitionScript();
        Assert.Contains("$n = 1", s);
        Assert.Contains("-ne 'USB'", s);
        Assert.Contains("$disk.IsSystem -or $disk.IsBoot", s);
        Assert.Contains("-ne 32000000000", s);
        Assert.Contains("-ne 'U1'", s);
        Assert.Contains("-ne '4C530001'", s);
        Assert.Contains("$env:SystemDrive", s);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(s, "Clear-Disk").Count);
        Assert.Contains("Clear-Disk -Number $n", s);
        Assert.Contains("Initialize-Disk -Number $n -PartitionStyle GPT", s);
        Assert.Contains("New-Partition -DiskNumber $n", s);
        Assert.Contains("'PCTOOL-BOOT'", s);
        Assert.Contains("'PCTOOL-DATA'", s);
        // Không có lệnh nào nhắm ổ khác hoặc "tất cả ổ".
        Assert.DoesNotContain("Get-Disk |", s);
        Assert.DoesNotContain("Get-Disk)", s.Replace("(Get-Disk -Number", ""));
        Assert.DoesNotContain("diskpart", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-DriveLetter C", s);
        // Kiểm tra bảo vệ nằm TRƯỚC lệnh xóa.
        Assert.True(s.IndexOf("-ne 'USB'", StringComparison.Ordinal) < s.IndexOf("Clear-Disk", StringComparison.Ordinal));
        Assert.True(s.IndexOf("'4C530001'", StringComparison.Ordinal) < s.IndexOf("Clear-Disk", StringComparison.Ordinal));
    }

    [Fact]
    public void Copy_Script_Splits_Big_Wim_And_Excludes_It_From_Robocopy()
    {
        var f = Make(new Runner(), Iso(wim: 5_200_000_000L));
        Assert.True(f.WillSplitWim);
        var s = f.BuildCopyScript("E");
        Assert.Contains("$split = $true", s);
        Assert.Contains("'/XF', 'install.wim'", s);
        Assert.Contains("Split-WindowsImage", s);
        Assert.Contains("-Access ReadOnly", s);
        Assert.Contains("if (-not $wasMounted) { Dismount-DiskImage", s);
        Assert.Contains("if ((Get-Partition -DriveLetter $boot).DiskNumber -ne $n)", s);
    }

    [Fact]
    public void Copy_Script_No_Split_For_Small_Wim()
    {
        var f = Make(new Runner(), Iso(wim: 3_000_000_000L));
        Assert.False(f.WillSplitWim);
        Assert.Contains("$split = $false", f.BuildCopyScript("E"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("EE")]
    [InlineData("e")]
    [InlineData("1")]
    [InlineData("C'; calc; '")]
    public void Copy_Script_Rejects_Bad_Letter(string letter) =>
        Assert.Throws<ArgumentException>(() => Make(new Runner()).BuildCopyScript(letter));

    // ---------------------------------------------------------------- Ổ đổi trước khi xóa: HỦY, không xóa

    [Fact]
    public async Task Disk_UniqueId_Changed_Aborts_Without_Wipe()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Scan = ScanJson(uid: "OTHER-DEVICE"); // sau khi chọn, ổ số 1 thành thiết bị khác
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("CHƯA xóa", res.Message);
        Assert.False(r.Wiped);
    }

    [Fact]
    public async Task Disk_Serial_Changed_Aborts_Without_Wipe()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Scan = ScanJson(serial: "DIFFERENT99");
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.False(r.Wiped);
    }

    [Fact]
    public async Task Disk_Size_Changed_Aborts_Without_Wipe()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Scan = ScanJson(size: 64 * GB);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(fix, Real(), null, CancellationToken.None)).Outcome);
        Assert.False(r.Wiped);
    }

    [Fact]
    public async Task Disk_Removed_Aborts_Without_Wipe()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Scan = """{"systemDrive":"C","disks":[{"Number":0,"FriendlyName":"SSD","SerialNumber":"W1","UniqueId":"UW","BusType":"NVMe","Size":500000000000,"IsSystem":true,"IsBoot":true,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"GPT","Partitions":[{"PartitionNumber":1,"DriveLetter":"C","Size":499000000000,"Type":"Basic","Label":"","FileSystem":"NTFS","Free":100000000000}]}]}""";
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(fix, Real(), null, CancellationToken.None)).Outcome);
        Assert.False(r.Wiped);
    }

    [Fact]
    public async Task Disk_Now_Holds_Iso_Drive_Aborts_Without_Wipe()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Scan = ScanJson(usbLetter: "D"); // ISO nằm trên D: ổ này bị chặn
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(fix, Real(), null, CancellationToken.None)).Outcome);
        Assert.False(r.Wiped);
    }

    [Fact]
    public async Task Disk_Now_Holds_Windows_Drive_Aborts_Without_Wipe()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Scan = ScanJson(usbLetter: "C");
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(fix, Real(), null, CancellationToken.None)).Outcome);
        Assert.False(r.Wiped);
    }

    [Fact]
    public async Task Rescan_Failure_Aborts_Without_Wipe()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Scan = "";
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("CHƯA xóa", res.Message);
        Assert.False(r.Wiped);
    }

    // ---------------------------------------------------------------- Lỗi giữa chừng

    [Fact]
    public async Task Partition_Failure_Stops_Before_Copy()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Partition = new PowerShellResult(1, "", "Ổ đã chọn không còn là ổ USB.");
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("không còn là ổ USB", res.Message);
        Assert.Equal(0, r.Count("robocopy"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("done")]
    [InlineData("OK|E|E")]
    [InlineData("OK|EE|F")]
    [InlineData("OK||F")]
    [InlineData("OK|e|f")]
    public async Task Partition_Output_Without_Valid_Letters_Stops(string output)
    {
        var r = new Runner();
        var fix = Make(r);
        r.Partition = new PowerShellResult(0, output, "");
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Equal(0, r.Count("robocopy"));
    }

    [Fact]
    public async Task Copy_Failure_Reports_Usb_Unusable_And_Skips_Verify()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Copy = new PowerShellResult(1, "", "robocopy báo lỗi (mã 8)");
        var res = await Exec().RunAsync(fix, Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("chưa dùng được", res.Message);
        Assert.Equal(0, r.Count("installBytes"));
    }

    [Fact]
    public async Task Copy_Without_Ok_Marker_Fails()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Copy = new PowerShellResult(0, "something else", "");
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(fix, Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Failed_Verification_Reported_As_StillPresent()
    {
        var r = new Runner();
        var fix = Make(r);
        r.Verify = new PowerShellResult(0, GoodVerify.Replace("\"efi\":true", "\"efi\":false"), "");
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(fix, Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Verify_Before_Execute_Is_False() => Assert.False(await Make(new Runner()).VerifyAsync(CancellationToken.None));

    // ---------------------------------------------------------------- Bộ kiểm tra lại

    private static IReadOnlyList<string> Problems(string json, IsoInfo? iso = null, bool split = true) =>
        UsbWriteVerifier.Problems(json, 1, iso ?? Iso(), split);

    [Fact]
    public void Verifier_Good_No_Problems() => Assert.Empty(Problems(GoodVerify));

    [Theory]
    [InlineData("\"style\":\"GPT\"", "\"style\":\"MBR\"")]
    [InlineData("\"bootDisk\":1", "\"bootDisk\":2")]
    [InlineData("\"dataDisk\":1", "\"dataDisk\":0")]
    [InlineData("\"bootLabel\":\"PCTOOL-BOOT\"", "\"bootLabel\":\"X\"")]
    [InlineData("\"bootFs\":\"FAT32\"", "\"bootFs\":\"NTFS\"")]
    [InlineData("\"dataLabel\":\"PCTOOL-DATA\"", "\"dataLabel\":\"\"")]
    [InlineData("\"dataFs\":\"NTFS\"", "\"dataFs\":\"FAT32\"")]
    [InlineData("\"efi\":true", "\"efi\":false")]
    [InlineData("\"bootWim\":true", "\"bootWim\":false")]
    [InlineData("\"hasSwm\":true", "\"hasSwm\":false")]
    [InlineData("\"hasWim\":false", "\"hasWim\":true")]
    [InlineData("\"installBytes\":5200000000", "\"installBytes\":1000000000")]
    [InlineData("\"installBytes\":5200000000", "\"installBytes\":9000000000")]
    public void Verifier_Detects_Each_Problem(string from, string to)
    {
        Assert.Contains(from, GoodVerify);
        Assert.NotEmpty(Problems(GoodVerify.Replace(from, to)));
    }

    [Fact]
    public void Verifier_Tolerates_Small_Swm_Overhead() =>
        Assert.Empty(Problems(GoodVerify.Replace("5200000000", "5210000000")));

    [Fact]
    public void Verifier_NoSplit_Requires_Exact_Size()
    {
        var iso = Iso(wim: 3_000_000_000L);
        var j = GoodVerify.Replace("\"hasSwm\":true", "\"hasSwm\":false").Replace("\"hasWim\":false", "\"hasWim\":true").Replace("5200000000", "3000000000");
        Assert.Empty(Problems(j, iso, split: false));
        Assert.NotEmpty(Problems(j.Replace("3000000000", "3000000001"), iso, split: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("oops")]
    [InlineData("[1]")]
    [InlineData("{}")]
    public void Verifier_Bad_Json_Is_A_Problem(string j) => Assert.NotEmpty(Problems(j));
}
