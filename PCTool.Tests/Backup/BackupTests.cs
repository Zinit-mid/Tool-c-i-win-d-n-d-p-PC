using System.Text.Json;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Backup;

public sealed class BackupTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly DateTime T = new(2026, 11, 21, 14, 5, 0);

    // ------------------------------------------------------------ Đường dẫn

    [Theory]
    [InlineData(@"D:\SaoLuu", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"C:\Users\Nguyễn Văn A\Desktop\PCTool-SaoLuu", true)]
    [InlineData(@"E:\Backup (1)\máy_cũ-2", true)]
    [InlineData(@"D:\a'; calc; '", false)]
    [InlineData(@"D:\a$(calc)", false)]
    [InlineData("D:\\a`b", false)]
    [InlineData(@"D:\a\..\b", false)]
    [InlineData(@"D:\a\.\b", false)]
    [InlineData(@"D:\a.", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"D:a", false)]
    [InlineData(@"relative\path", false)]
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"c:\PROGRAM FILES\x", false)]
    [InlineData(@"C:\ProgramData\x", false)]
    [InlineData("D:\\a\r\nb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Path_Validation(string? path, bool ok) => Assert.Equal(ok, BackupPath.IsValid(path, out _));

    [Fact]
    public void Path_TooLong_Rejected() => Assert.False(BackupPath.IsValid(@"D:\" + new string('a', 200), out _));

    [Fact]
    public void Path_Combine_AddsStampAndSub()
    {
        Assert.Equal(@"D:\SaoLuu\2026-11-21_1405\Drivers", BackupPath.Combine(@"D:\SaoLuu\", T, "Drivers"));
        Assert.Throws<ArgumentException>(() => BackupPath.Combine(@"D:\a'b", T, "Drivers"));
        Assert.Throws<ArgumentException>(() => BackupPath.Combine(@"D:\SaoLuu", T, @"..\x"));
        Assert.Throws<ArgumentException>(() => BackupPath.Combine(@"D:\SaoLuu", T, ""));
    }

    [Fact]
    public void Fix_Constructor_RejectsBadFolder()
    {
        Assert.Throws<ArgumentException>(() => new DriverBackupFix(@"D:\x'; calc; '"));
        Assert.Throws<ArgumentException>(() => new WifiBackupFix(@"C:\Windows\x"));
    }

    // ------------------------------------------------------------ Driver / Wi-Fi

    private const string Dir = @"D:\SaoLuu\2026-11-21_1405\Drivers";

    [Fact]
    public void Scripts_ContainOnlyValidatedFolder_AndSafeCommands()
    {
        var s = new DriverBackupFix(Dir).BuildScript();
        Assert.Contains($"'{Dir}'", s);
        Assert.Contains("Export-WindowsDriver -Online", s);
        Assert.DoesNotContain("Remove-", s);
        Assert.DoesNotContain("Uninstall", s);

        var w = new WifiBackupFix(Dir).BuildScript();
        Assert.Contains("netsh wlan export profile key=clear", w);
        Assert.DoesNotContain("delete", w, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wifi_Actions_WarnAboutPlaintextPasswords() =>
        Assert.Contains(new WifiBackupFix(Dir).Actions, a => a.Contains("MẬT KHẨU") && a.Contains("chữ thường"));

    [Fact]
    public async Task Driver_Success_VerifiedAndReportsCount()
    {
        var fix = new DriverBackupFix(Dir, (s, _) => Task.FromResult(new PowerShellResult(0, "12\r\n", "")));

        var r = await new FixExecutor(new FakeRestorePoints()).RunAsync(fix, new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, r.Outcome);
        Assert.Contains("12 driver", r.Message);
        Assert.Contains(Dir, r.Message);
    }

    [Fact]
    public async Task Driver_ZeroFiles_Failed()
    {
        var fix = new DriverBackupFix(Dir, (_, _) => Task.FromResult(new PowerShellResult(0, "0", "")));

        var r = await new FixExecutor(new FakeRestorePoints()).RunAsync(fix, new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, r.Outcome);
    }

    [Fact]
    public async Task Wifi_NoProfiles_FailedWithClearMessage()
    {
        var fix = new WifiBackupFix(Dir, (_, _) => Task.FromResult(new PowerShellResult(0, "0", "")));

        var r = await new FixExecutor(new FakeRestorePoints()).RunAsync(fix, new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, r.Outcome);
        Assert.Contains("không có mạng Wi-Fi", r.Message);
    }

    [Fact]
    public async Task Wifi_ScriptError_ShowsError()
    {
        var fix = new WifiBackupFix(Dir, (_, _) => Task.FromResult(new PowerShellResult(1, "", "netsh wlan thất bại")));

        var r = await new FixExecutor(new FakeRestorePoints()).RunAsync(fix, new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, r.Outcome);
        Assert.Contains("netsh", r.Message);
    }

    [Fact]
    public async Task Driver_GarbageOutput_FailedNotSuccess()
    {
        var fix = new DriverBackupFix(Dir, (_, _) => Task.FromResult(new PowerShellResult(0, "xin chào", "")));

        var r = await new FixExecutor(new FakeRestorePoints()).RunAsync(fix, new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, r.Outcome);
    }

    [Fact]
    public async Task Verify_FailsIfFolderEmptyAfterRun()
    {
        // Lần chạy báo 5 file nhưng lúc kiểm tra lại không còn file nào.
        var fix = new DriverBackupFix(Dir, (s, _) => Task.FromResult(new PowerShellResult(0,
            s.Contains("Export-WindowsDriver") ? "5" : "0", "")));

        var r = await new FixExecutor(new FakeRestorePoints()).RunAsync(fix, new FixOptions { DryRun = false }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.StillPresent, r.Outcome);
    }

    [Fact]
    public async Task DryRun_And_SafeMode_NeverRunScript()
    {
        int calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "3", "")); };
        var ex = new FixExecutor(new FakeRestorePoints());

        var a = await ex.RunAsync(new DriverBackupFix(Dir, run), new FixOptions { DryRun = true }, null, CancellationToken.None);
        var b = await ex.RunAsync(new WifiBackupFix(Dir, run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Simulated, a.Outcome);
        Assert.Equal(FixOutcome.Blocked, b.Outcome);
        Assert.Equal(0, calls);
    }

    // ------------------------------------------------------------ BitLocker

    [Fact]
    public void BitLocker_NotEncrypted_NoFindings() =>
        Assert.Empty(BitLockerCheck.Evaluate(J("""{"available":true,"volumes":[{"Mount":"C:","Status":"FullyDecrypted","Protection":"Off","Percent":0,"Lock":"Unlocked"}]}""")));

    [Fact]
    public void BitLocker_Encrypted_WarnsAboutRecoveryKey_NoFix()
    {
        var f = Assert.Single(BitLockerCheck.Evaluate(J("""{"available":true,"volumes":[{"Mount":"C:","Status":"FullyEncrypted","Protection":"On","Percent":100,"Lock":"Unlocked"}]}""")));

        Assert.Equal("bitlocker.encrypted", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("C:", f.Title);
        Assert.Contains("khóa khôi phục", f.Cause);
        Assert.Empty(f.Fixes);
    }

    [Fact]
    public void BitLocker_Suspended_IsInfo()
    {
        var f = Assert.Single(BitLockerCheck.Evaluate(J("""{"available":true,"volumes":[{"Mount":"D:","Status":"FullyEncrypted","Protection":"Off","Percent":100,"Lock":"Unlocked"}]}""")));

        Assert.Equal("bitlocker.suspended", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
    }

    [Fact]
    public void BitLocker_SingleVolumeObject_AndMultiple()
    {
        var one = BitLockerCheck.Evaluate(J("""{"available":true,"volumes":{"Mount":"C:","Status":"FullyEncrypted","Protection":"On","Percent":100,"Lock":"Unlocked"}}"""));
        var two = BitLockerCheck.Evaluate(J("""{"available":true,"volumes":[{"Mount":"C:","Status":"FullyEncrypted","Protection":"On"},{"Mount":"D:","Status":"EncryptionInProgress","Protection":"On","Percent":40}]}"""));

        Assert.Single(one);
        Assert.Equal(2, two.Count);
    }

    [Fact]
    public void BitLocker_Unavailable_ExplainsWithoutClaimingDecrypted()
    {
        var f = Assert.Single(BitLockerCheck.Evaluate(J("""{"available":false,"volumes":[]}""")));

        Assert.Equal("bitlocker.unavailable", f.Id);
        Assert.Contains("Không đọc được", f.Title);
    }

    [Fact]
    public async Task BitLocker_ReadOnlyScript_AndRunsThroughRunner()
    {
        Assert.DoesNotContain("Disable-BitLocker", BitLockerCheck.ScriptText);
        Assert.DoesNotContain("Unlock-BitLocker", BitLockerCheck.ScriptText);
        Assert.DoesNotContain("Remove-", BitLockerCheck.ScriptText);

        var check = new BitLockerCheck((_, _) => Task.FromResult(new PowerShellResult(0,
            """{"available":true,"volumes":[{"Mount":"C:","Status":"FullyEncrypted","Protection":"On","Percent":100,"Lock":"Unlocked"}]}""", "")));

        Assert.Single(await check.RunAsync(CancellationToken.None));
    }
}
