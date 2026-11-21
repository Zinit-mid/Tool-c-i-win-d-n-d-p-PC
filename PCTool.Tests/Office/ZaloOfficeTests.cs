using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Office;
using PCTool.Core.Windows;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Office;

public sealed class ZaloOfficeTests
{
    private const string Dir = @"D:\SaoLuu\2026-11-21_1430\Zalo";
    private static ScriptRunner Returns(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));
    private static Task<FixResult> RunReal(IFix fix) =>
        new FixExecutor(new FakeRestorePoints()).RunAsync(fix, new FixOptions { DryRun = false }, null, CancellationToken.None);

    // ------------------------------------------------------------ Zalo

    [Fact]
    public void Zalo_Script_OnlyCopies_NeverDeletes_AndUsesValidatedFolder()
    {
        var s = new ZaloBackupFix(Dir).BuildScript();

        Assert.Contains($"'{Dir}'", s);
        Assert.Contains("Copy-Item", s);
        Assert.DoesNotContain("Remove-Item", s);
        Assert.DoesNotContain("Move-Item", s);
        Assert.DoesNotContain("Rename-Item", s);
        Assert.Contains("[0-9]{6,}", s); // chỉ thư mục tài khoản dạng số
        Assert.Contains("Get-Process -Name 'Zalo'", s);
    }

    [Fact]
    public void Zalo_Actions_WarnAboutPrivateData_AndClosingZalo()
    {
        var a = new ZaloBackupFix(Dir).Actions;

        Assert.Contains(a, x => x.Contains("TIN NHẮN"));
        Assert.Contains(a, x => x.Contains("ĐÓNG HẲN"));
    }

    [Fact]
    public async Task Zalo_Running_FailsWithCloseInstruction()
    {
        var r = await RunReal(new ZaloBackupFix(Dir, Returns("RUNNING")));

        Assert.Equal(FixOutcome.Failed, r.Outcome);
        Assert.Contains("đóng Zalo", r.Message);
    }

    [Fact]
    public async Task Zalo_NoData_PointsToBuiltInExport()
    {
        var r = await RunReal(new ZaloBackupFix(Dir, Returns("NODATA")));

        Assert.Equal(FixOutcome.Failed, r.Outcome);
        Assert.Contains("Xuất dữ liệu", r.Message);
    }

    [Fact]
    public async Task Zalo_Success_ReportsCountAndRestoreHint()
    {
        var r = await RunReal(new ZaloBackupFix(Dir, Returns("1234")));

        Assert.Equal(FixOutcome.Succeeded, r.Outcome);
        Assert.Contains("1234", r.Message);
        Assert.Contains("khôi phục", r.Message);
    }

    [Fact]
    public async Task Zalo_ZeroFiles_NotSuccess()
    {
        var r = await RunReal(new ZaloBackupFix(Dir, Returns("0")));

        Assert.Equal(FixOutcome.Failed, r.Outcome);
    }

    [Fact]
    public async Task Zalo_DryRun_And_SafeMode_NeverRun()
    {
        int calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "5", "")); };
        var ex = new FixExecutor(new FakeRestorePoints());

        var a = await ex.RunAsync(new ZaloBackupFix(Dir, run), new FixOptions { DryRun = true }, null, CancellationToken.None);
        var b = await ex.RunAsync(new ZaloBackupFix(Dir, run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Simulated, a.Outcome);
        Assert.Equal(FixOutcome.Blocked, b.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Zalo_RejectsBadFolder() => Assert.Throws<ArgumentException>(() => new ZaloBackupFix(@"C:\Windows\x"));

    [Fact]
    public async Task Existing_Backups_StillUseFileOnlyVerification()
    {
        // BuildVerifyScript giờ chỉ đếm file (không đếm thư mục).
        Assert.Contains("-File", new DriverBackupFix(@"D:\a").BuildVerifyScript());
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------ Word / Excel

    [Fact]
    public void SafeMode_FixedCommands()
    {
        Assert.Equal(("winword.exe", "/safe"), OfficeSafeMode.Command(OfficeApp.Word));
        Assert.Equal(("excel.exe", "/safe"), OfficeSafeMode.Command(OfficeApp.Excel));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfficeSafeMode.Command((OfficeApp)99));
    }

    [Fact]
    public void Template_Script_RenamesOnly_NeverDeletes()
    {
        Assert.Contains("Rename-Item", ResetWordTemplateFix.ExecuteScript);
        Assert.DoesNotContain("Remove-Item", ResetWordTemplateFix.ExecuteScript);
        Assert.DoesNotContain("Delete", ResetWordTemplateFix.ExecuteScript);
        Assert.Contains("WINWORD", ResetWordTemplateFix.ExecuteScript);
    }

    [Fact]
    public void Template_Actions_ExplainRenameNotDelete_AndWhatIsLost()
    {
        var a = new ResetWordTemplateFix().Actions;

        Assert.Contains(a, x => x.Contains("KHÔNG xóa"));
        Assert.Contains(a, x => x.Contains("macro"));
    }

    [Fact]
    public async Task Template_Success_Verified()
    {
        ScriptRunner run = (s, _) => Task.FromResult(new PowerShellResult(0,
            s == ResetWordTemplateFix.VerifyScript ? "True" : "OK:Normal.dotm.bak-20261121-1430", ""));

        var r = await RunReal(new ResetWordTemplateFix(run));

        Assert.Equal(FixOutcome.Succeeded, r.Outcome);
        Assert.Contains("Normal.dotm.bak-20261121-1430", r.Message);
    }

    [Theory]
    [InlineData("RUNNING", "đóng Word")]
    [InlineData("NOFILE", "không cần làm gì")]
    [InlineData("lạ", "Không đọc được")]
    public async Task Template_NotSuccess_Cases(string output, string expectedText)
    {
        var r = await RunReal(new ResetWordTemplateFix(Returns(output)));

        Assert.Equal(FixOutcome.Failed, r.Outcome);
        Assert.Contains(expectedText, r.Message);
    }

    [Fact]
    public async Task Template_FileStillThere_IsStillPresent()
    {
        ScriptRunner run = (s, _) => Task.FromResult(new PowerShellResult(0, s == ResetWordTemplateFix.VerifyScript ? "False" : "OK:Normal.dotm.bak-x", ""));

        var r = await RunReal(new ResetWordTemplateFix(run));

        Assert.Equal(FixOutcome.StillPresent, r.Outcome);
    }

    [Fact]
    public async Task Template_DryRun_And_SafeMode_NeverRun()
    {
        int calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "OK:x", "")); };
        var ex = new FixExecutor(new FakeRestorePoints());

        var a = await ex.RunAsync(new ResetWordTemplateFix(run), new FixOptions { DryRun = true }, null, CancellationToken.None);
        var b = await ex.RunAsync(new ResetWordTemplateFix(run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Simulated, a.Outcome);
        Assert.Equal(FixOutcome.Blocked, b.Outcome);
        Assert.Equal(0, calls);
    }
}
