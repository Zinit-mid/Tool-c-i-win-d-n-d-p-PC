using PCTool.Core.Framework;
using PCTool.Core.Reinstall;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Reinstall;

public sealed class DirectReinstallTests
{
    private const long GB = 1L << 30;
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };
    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));

    private static SystemFacts Sys(int build = 22631, string ed = "Professional", string arch = "64-bit", string ui = "vi-VN", long free = 100 * GB,
        bool battery = false, bool onBat = false, int pct = 0, bool blKnown = true, bool blOn = false, bool net = true) =>
        new(build, "Windows 11 Pro", ed, arch, ui, free, 0, battery, onBat, pct, blKnown, blOn, net);

    private static IsoImage Img(int build = 22631, string ed = "Professional", string arch = "x64", string lang = "vi-VN") =>
        new(1, "Windows 11 Pro", ed, $"10.0.{build}.1", arch, new[] { lang }, 5 * GB);

    private static IsoInfo Iso(params IsoImage[] images) =>
        new(@"D:\win.iso", 6 * GB, false, true, 5 * GB, false, 0, true, images.Length == 0 ? new[] { Img() } : images);

    private static BackupFacts Good() => new(true, 1, 0, true, true, true, true, true);

    private static DirectPlan Plan(DirectMode m, SystemFacts? s = null, IsoInfo? iso = null, BackupFacts? b = null, bool useDefaultIso = true) =>
        DirectPlanner.Evaluate(m, s ?? Sys(), DirectModes.NeedsIso(m) ? (iso ?? (useDefaultIso ? Iso() : null)) : null, b);

    // ------------------------------------------------------------ Điều kiện chung

    [Fact]
    public void C1_Healthy_Without_Backup_Can_Run_With_Warning()
    {
        var p = Plan(DirectMode.RepairKeepAll);
        Assert.True(p.CanRun);
        Assert.Contains(p.Warnings, w => w.Contains("sao lưu"));
    }

    [Theory]
    [InlineData(DirectMode.KeepFilesOnly)]
    [InlineData(DirectMode.CleanAll)]
    [InlineData(DirectMode.ResetPc)]
    public void Destructive_Modes_Require_Backup(DirectMode m)
    {
        Assert.False(Plan(m).CanRun);
        Assert.True(Plan(m, b: Good()).CanRun);
    }

    [Fact]
    public void On_Battery_Blocks_And_Low_Charge_Warns()
    {
        Assert.False(Plan(DirectMode.RepairKeepAll, Sys(battery: true, onBat: true, pct: 80)).CanRun);
        var p = Plan(DirectMode.RepairKeepAll, Sys(battery: true, pct: 30));
        Assert.True(p.CanRun);
        Assert.Contains(p.Warnings, w => w.Contains("30%"));
    }

    [Fact]
    public void Free_Space_Thresholds()
    {
        Assert.False(Plan(DirectMode.RepairKeepAll, Sys(free: 10 * GB)).CanRun);
        var p = Plan(DirectMode.RepairKeepAll, Sys(free: 25 * GB));
        Assert.True(p.CanRun);
        Assert.Contains(p.Warnings, w => w.Contains("hơi ít"));
        Assert.True(Plan(DirectMode.RepairKeepAll, Sys(free: -1)).CanRun); // không đọc được: chỉ cảnh báo, setup tự kiểm tra
    }

    [Fact]
    public void BitLocker_On_Needs_Ack_Unknown_Only_Warns()
    {
        var on = Plan(DirectMode.RepairKeepAll, Sys(blOn: true));
        Assert.True(on.NeedsBitLockerAck);
        Assert.Contains(on.Warnings, w => w.Contains("KHÓA KHÔI PHỤC"));
        var unknown = Plan(DirectMode.RepairKeepAll, Sys(blKnown: false));
        Assert.False(unknown.NeedsBitLockerAck);
        Assert.Contains(unknown.Warnings, w => w.Contains("BitLocker"));
    }

    // ------------------------------------------------------------ ISO

    [Fact]
    public void Iso_Missing_Or_Empty_Blocks()
    {
        Assert.False(Plan(DirectMode.RepairKeepAll, useDefaultIso: false).CanRun);
        var noImages = new IsoInfo(@"D:\a.iso", 1, false, true, 1, false, 0, true, Array.Empty<IsoImage>());
        Assert.False(Plan(DirectMode.RepairKeepAll, iso: noImages).CanRun);
        var noWim = new IsoInfo(@"D:\a.iso", 1, false, false, 0, false, 0, true, new[] { Img() });
        Assert.False(Plan(DirectMode.RepairKeepAll, iso: noWim).CanRun);
    }

    [Fact]
    public void Non_X64_Iso_Or_Os_Blocks()
    {
        Assert.False(Plan(DirectMode.CleanAll, iso: Iso(Img(arch: "arm64")), b: Good()).CanRun);
        Assert.False(Plan(DirectMode.RepairKeepAll, Sys(arch: "32-bit")).CanRun);
        Assert.True(Plan(DirectMode.RepairKeepAll, Sys(arch: "64 bits")).CanRun);
    }

    [Fact]
    public void Older_Iso_Blocks_Keep_Modes_But_Only_Warns_For_Clean()
    {
        var old = Iso(Img(build: 19045));
        Assert.False(Plan(DirectMode.RepairKeepAll, iso: old).CanRun);
        Assert.False(Plan(DirectMode.KeepFilesOnly, iso: old, b: Good()).CanRun);
        var clean = Plan(DirectMode.CleanAll, iso: old, b: Good());
        Assert.True(clean.CanRun);
        Assert.Contains(clean.Warnings, w => w.Contains("cũ hơn"));
    }

    [Fact]
    public void Language_Mismatch_Blocks_Keep_Modes()
    {
        var en = Iso(Img(lang: "en-US"));
        Assert.False(Plan(DirectMode.RepairKeepAll, iso: en).CanRun);
        Assert.True(Plan(DirectMode.CleanAll, iso: en, b: Good()).CanRun);
        Assert.True(Plan(DirectMode.RepairKeepAll, iso: Iso(Img(lang: "VI-vn"))).CanRun); // không phân biệt hoa thường
    }

    [Fact]
    public void Edition_Mismatch_Blocks_Keep_Modes()
    {
        var home = Iso(Img(ed: "Core"));
        Assert.False(Plan(DirectMode.RepairKeepAll, iso: home).CanRun);
        Assert.True(Plan(DirectMode.RepairKeepAll, iso: Iso(Img(ed: "Core"), Img(ed: "Professional"))).CanRun);
        Assert.True(Plan(DirectMode.RepairKeepAll, Sys(ed: ""), home).CanRun); // không đọc được edition hiện tại: không chặn
    }

    [Fact]
    public void ResetPc_Ignores_Iso_And_Warns_Without_Network()
    {
        var p = Plan(DirectMode.ResetPc, Sys(net: false), b: Good());
        Assert.True(p.CanRun);
        Assert.Contains(p.Warnings, w => w.Contains("Cloud download"));
    }

    // ------------------------------------------------------------ Sao lưu

    [Theory]
    [InlineData(false, 1, 0, true, true, true, true, true)]   // không tồn tại
    [InlineData(true, 0, 0, true, true, true, true, true)]    // cùng ổ vật lý
    [InlineData(true, -1, 0, true, true, true, true, true)]   // không xác định ổ
    [InlineData(true, 1, -1, true, true, true, true, true)]
    [InlineData(true, 1, 0, false, true, true, true, true)]   // thiếu userdata
    [InlineData(true, 1, 0, true, false, true, true, true)]   // thiếu manifest
    [InlineData(true, 1, 0, true, true, false, true, true)]   // thiếu userdata.json
    [InlineData(true, 1, 0, true, true, true, false, true)]   // thiếu restore-plan
    [InlineData(true, 1, 0, true, true, true, true, false)]   // thiếu PCTool.exe
    public void Bad_Backup_Blocks_Destructive_Modes(bool ex, int bd, int sd, bool ud, bool mf, bool uj, bool pl, bool kit)
    {
        var b = new BackupFacts(ex, bd, sd, ud, mf, uj, pl, kit);
        Assert.False(Plan(DirectMode.KeepFilesOnly, b: b).CanRun);
        Assert.False(Plan(DirectMode.CleanAll, b: b).CanRun);
        Assert.False(Plan(DirectMode.ResetPc, b: b).CanRun);
        var c1 = Plan(DirectMode.RepairKeepAll, b: b);
        Assert.True(c1.CanRun);                 // C1 chỉ khuyến nghị
        Assert.NotEmpty(c1.Warnings);
    }

    [Fact]
    public void Backup_On_Same_Disk_Message_Is_Clear() =>
        Assert.Contains(Plan(DirectMode.CleanAll, b: new BackupFacts(true, 0, 0, true, true, true, true, true)).Blockers, x => x.Contains("CÙNG ổ vật lý"));

    // ------------------------------------------------------------ Đọc dữ liệu & script

    [Fact]
    public void System_Script_Is_ReadOnly()
    {
        foreach (var bad in new[] { "Remove-", "Set-", "Clear-", "Format-", "Start-Process", "Mount-", "Disable-", "Suspend-", "New-Item", "robocopy" })
            Assert.DoesNotContain(bad, DirectFactsScanner.SystemScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backup_Script_Is_ReadOnly_And_Validates_Path()
    {
        var s = DirectFactsScanner.BackupScript(@"E:\SaoLuu\2026-11-21_1405");
        Assert.Contains(@"$root = 'E:\SaoLuu\2026-11-21_1405'", s);
        foreach (var bad in new[] { "Remove-", "Set-", "Clear-", "Format-", "Start-Process", "New-Item", "robocopy", "Copy-Item", "Move-Item" })
            Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => DirectFactsScanner.BackupScript(@"C:\Windows\x"));
        Assert.Throws<ArgumentException>(() => DirectFactsScanner.BackupScript(@"E:\a'; calc; '"));
    }

    [Fact]
    public void Parsers_Read_Fields_And_Reject_Garbage()
    {
        var s = DirectFactsParser.ParseSystem("""{"build":22631,"caption":"Win","edition":"Professional","arch":"64-bit","ui":"vi-VN","free":5000,"systemDisk":0,"battery":true,"onBattery":false,"percent":77,"blKnown":true,"blOn":true,"net":true}""");
        Assert.Equal(22631, s.Build);
        Assert.True(s.BitLockerOn);
        Assert.Equal(77, s.BatteryPercent);
        var b = DirectFactsParser.ParseBackup("""{"exists":true,"backupDisk":1,"systemDisk":0,"userdata":true,"manifest":true,"userdataJson":true,"plan":true,"kit":false}""");
        Assert.True(b.HasAppPlan);
        Assert.False(b.HasKit);
        foreach (var bad in new[] { "", "oops", "[1]" })
        {
            Assert.Throws<FormatException>(() => DirectFactsParser.ParseSystem(bad));
            Assert.Throws<FormatException>(() => DirectFactsParser.ParseBackup(bad));
        }
    }

    [Fact]
    public async Task Scanner_Surfaces_Script_Errors()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DirectFactsScanner(Fake("", 1, "lỗi")).ScanSystemAsync(CancellationToken.None));
        var ok = await new DirectFactsScanner(Fake("""{"build":1}""")).ScanSystemAsync(CancellationToken.None);
        Assert.Equal(1, ok.Build);
    }

    [Fact]
    public void ToText_Says_Nothing_Changed_And_Lists_Guide()
    {
        var t = DirectPlanner.ToText(Plan(DirectMode.CleanAll, b: Good()));
        Assert.Contains("Chưa mở gì", t);
        Assert.Contains("Nothing", t);
        Assert.Contains("XOA SACH", t);
    }

    // ------------------------------------------------------------ Fix mở công cụ

    private static LaunchReinstallFix Launch(DirectMode m, DirectAcks? acks = null, SystemFacts? s = null, ScriptRunner? run = null, Action<string>? open = null, string iso = @"D:\win.iso")
    {
        var plan = Plan(m, s, b: Good());
        return new LaunchReinstallFix(plan, acks ?? new DirectAcks(true, true, DirectModes.CleanPhrase), DirectModes.NeedsIso(m) ? iso : null, run ?? Fake("OK|E|123"), open ?? (_ => { }));
    }

    [Fact]
    public void Launch_Rejects_Unfit_Plan_Missing_Acks_Wrong_Phrase_And_Bad_Iso()
    {
        var bad = DirectPlanner.Evaluate(DirectMode.CleanAll, Sys(), Iso(), null);
        Assert.Throws<ArgumentException>(() => new LaunchReinstallFix(bad, new DirectAcks(true, true, DirectModes.CleanPhrase), @"D:\win.iso"));
        Assert.Throws<ArgumentException>(() => Launch(DirectMode.KeepFilesOnly, new DirectAcks(false, true, "")));
        Assert.Throws<ArgumentException>(() => Launch(DirectMode.CleanAll, new DirectAcks(true, true, "xoa sach")));
        Assert.Throws<ArgumentException>(() => Launch(DirectMode.CleanAll, new DirectAcks(true, true, "")));
        Assert.Throws<ArgumentException>(() => Launch(DirectMode.KeepFilesOnly, new DirectAcks(true, false, ""), Sys(blOn: true)));
        Assert.Throws<ArgumentException>(() => Launch(DirectMode.KeepFilesOnly, new DirectAcks(true, true, ""), iso: @"D:\a'; calc; '.iso"));
        Assert.Throws<ArgumentException>(() => Launch(DirectMode.KeepFilesOnly, new DirectAcks(true, true, ""), iso: @"D:\win.zip"));
    }

    [Fact]
    public void Launch_Script_Mounts_ReadOnly_And_Runs_Setup_Without_Arguments()
    {
        var s = Launch(DirectMode.RepairKeepAll).BuildScript();
        Assert.Contains("-Access ReadOnly", s);
        Assert.Contains(@"$path = 'D:\win.iso'", s);
        Assert.Contains("Start-Process -FilePath $setup -WorkingDirectory", s);
        Assert.DoesNotContain("-ArgumentList", s);
        foreach (var bad in new[] { "/auto", "/quiet", "/noreboot", "Restart-Computer", "Stop-Computer", "shutdown", "Dismount", "Remove-Item", "Format-", "Clear-Disk", "systemreset", "Windows.old" })
            Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DirectMode.RepairKeepAll)]
    [InlineData(DirectMode.KeepFilesOnly)]
    [InlineData(DirectMode.CleanAll)]
    public async Task Iso_Modes_Launch_And_Verify(DirectMode m)
    {
        var scripts = new List<string>();
        ScriptRunner run = (s, _) => { scripts.Add(s); return Task.FromResult(new PowerShellResult(0, s.Contains("Get-Process") ? "1" : "OK|E|4321", "")); };
        var res = await Exec().RunAsync(Launch(m, run: run), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Contains("E:", res.Message);
        Assert.Equal(2, scripts.Count);
    }

    [Fact]
    public async Task Setup_Not_Seen_After_Launch_Is_StillPresent()
    {
        ScriptRunner run = (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("Get-Process") ? "0" : "OK|E|1", ""));
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(Launch(DirectMode.RepairKeepAll, run: run), Real(), null, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("OK|EE|1")]
    [InlineData("OK|1|1")]
    public async Task Garbage_Launch_Output_Fails(string output) =>
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(Launch(DirectMode.RepairKeepAll, run: Fake(output)), Real(), null, CancellationToken.None)).Outcome);

    [Fact]
    public async Task Launch_Script_Error_Fails()
    {
        var res = await Exec().RunAsync(Launch(DirectMode.RepairKeepAll, run: Fake("", 1, "Không thấy setup.exe")), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("setup.exe", res.Message);
    }

    [Fact]
    public async Task ResetPc_Opens_Settings_Recovery_Only()
    {
        var opened = new List<string>();
        var ran = 0;
        ScriptRunner run = (_, _) => { ran++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        var res = await Exec().RunAsync(Launch(DirectMode.ResetPc, run: run, open: opened.Add), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Equal(new[] { "ms-settings:recovery" }, opened);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task ResetPc_Open_Failure_Fails()
    {
        var res = await Exec().RunAsync(Launch(DirectMode.ResetPc, open: _ => throw new System.ComponentModel.Win32Exception("x")), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
    }

    [Theory]
    [InlineData(DirectMode.RepairKeepAll)]
    [InlineData(DirectMode.CleanAll)]
    [InlineData(DirectMode.ResetPc)]
    public async Task DryRun_And_SafeMode_Launch_Nothing(DirectMode m)
    {
        var ran = 0; var opened = 0;
        ScriptRunner run = (_, _) => { ran++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        await Exec().RunAsync(Launch(m, run: run, open: _ => opened++), new FixOptions { DryRun = true }, null, CancellationToken.None);
        await Exec().RunAsync(Launch(m, run: run, open: _ => opened++), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);
        Assert.Equal(0, ran);
        Assert.Equal(0, opened);
    }

    [Fact]
    public void Launch_Does_Not_Use_Restore_Point_Or_Claim_Undo()
    {
        var f = Launch(DirectMode.CleanAll);
        Assert.False(f.RequiresRestorePoint);
        Assert.False(f.IsUndoable);
        Assert.Contains(f.Actions, a => a.Contains("không khởi động lại"));
    }

    // ------------------------------------------------------------ Bộ khôi phục

    private sealed class FakeFs
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string p) => Files.ContainsKey(p);
        public void Copy(string a, string b) { if (Files.ContainsKey(b)) throw new IOException("exists"); Files[b] = Files[a]; }
        public string Hash(string p) => Files[p];
        public void Write(string p, string t) => Files[p] = t;
        public RestoreKitFix Make(string exe = @"C:\PCTool\PCTool.exe") => new(@"E:\SaoLuu\2026-11-21_1405", exe, Exists, Copy, Hash, Write);
    }

    [Fact]
    public async Task Kit_Copies_Exe_And_Readme_And_Verifies()
    {
        var fs = new FakeFs();
        fs.Files[@"C:\PCTool\PCTool.exe"] = "BIN";
        var res = await Exec().RunAsync(fs.Make(), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Succeeded, res.Outcome);
        Assert.Equal("BIN", fs.Files[@"E:\SaoLuu\2026-11-21_1405\PCTool.exe"]);
        Assert.Contains("Khôi phục sau cài Win", fs.Files[@"E:\SaoLuu\2026-11-21_1405\" + RestoreKitFix.ReadmeName]);
    }

    [Fact]
    public async Task Kit_Never_Overwrites_A_Different_Exe_But_Accepts_Identical()
    {
        var fs = new FakeFs();
        fs.Files[@"C:\PCTool\PCTool.exe"] = "NEW";
        fs.Files[@"E:\SaoLuu\2026-11-21_1405\PCTool.exe"] = "OLD";
        var res = await Exec().RunAsync(fs.Make(), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Equal("OLD", fs.Files[@"E:\SaoLuu\2026-11-21_1405\PCTool.exe"]);

        fs.Files[@"E:\SaoLuu\2026-11-21_1405\PCTool.exe"] = "NEW";
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(fs.Make(), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Kit_Missing_Source_Fails_And_Bad_Args_Rejected()
    {
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new FakeFs().Make(), Real(), null, CancellationToken.None)).Outcome);
        Assert.Throws<ArgumentException>(() => new RestoreKitFix(@"C:\Windows\x", @"C:\a\PCTool.exe"));
        Assert.Throws<ArgumentException>(() => new RestoreKitFix(@"E:\B", "notexe.txt"));
        Assert.Throws<ArgumentException>(() => new RestoreKitFix(@"E:\B", ""));
    }

    [Fact]
    public async Task Kit_DryRun_Writes_Nothing()
    {
        var fs = new FakeFs();
        fs.Files[@"C:\PCTool\PCTool.exe"] = "BIN";
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(fs.Make(), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Single(fs.Files);
    }
}
