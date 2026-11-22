using PCTool.Core.Framework;
using PCTool.Core.Stubborn;
using PCTool.Core.Windows;
using PCTool.Tests.Folders;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Stubborn;

public sealed class StubbornTests
{
    private static FixExecutor Exec() => new(new FakeRestorePoints());
    private static FixOptions Real() => new() { DryRun = false, SafeMode = false, CreateRestorePoint = false };
    private static ScriptRunner Fake(string output, int code = 0, string err = "") => (_, _) => Task.FromResult(new PowerShellResult(code, output, err));

    private static string Dir(TempDir t, string name) { var p = System.IO.Path.Combine(t.Path, name); Directory.CreateDirectory(p); return p; }

    [Fact]
    public void Guard_Allows_Temp_Dir_And_File_Rejects_Dangerous()
    {
        using var t = new TempDir();
        var d = Dir(t, "cu");
        var f = t.File(@"cu\a.txt");
        Assert.True(StubbornGuard.Check(d, out _));
        Assert.True(StubbornGuard.Check(f, out _));
        foreach (var bad in new[] { @"C:\Windows", @"C:\Windows\System32\cmd.exe", @"C:\Program Files", @"C:\", @"D:\", @"C:\ProgramData", @"\\srv\share", "relative", @"D:\a'; calc; '", @"D:\x\..\y", "", null })
            Assert.False(StubbornGuard.Check(bad, out var err), bad);
        Assert.False(StubbornGuard.Check(System.IO.Path.Combine(t.Path, "khong-co"), out var e2));
        Assert.Contains("Không thấy", e2);
    }

    [Fact]
    public void Grant_Needs_Code_And_Safe_Path()
    {
        using var t = new TempDir();
        var d = Dir(t, "cu");
        Assert.Throws<ArgumentException>(() => new GrantFullAccessFix(d, "sai"));
        Assert.Throws<ArgumentException>(() => new GrantFullAccessFix(@"C:\Windows", "CAP QUYEN"));
        var s = new GrantFullAccessFix(d, "CAP QUYEN").BuildScript();
        Assert.Contains("/save", s);                       // lưu quyền cũ TRƯỚC
        Assert.True(s.IndexOf("/save") < s.IndexOf("takeown /F"));
        Assert.Contains("S-1-5-32-544", s);                // SID, không phụ thuộc ngôn ngữ
        Assert.DoesNotContain("Remove-Item", s);
        Assert.DoesNotContain("Move-Item", s);
        Assert.DoesNotContain("/deny", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Everyone", s);
    }

    [Fact]
    public async Task Grant_Verifies_By_Reading_Acl()
    {
        using var t = new TempDir();
        var d = Dir(t, "cu");
        ScriptRunner Run(string verify) => (s, _) => Task.FromResult(new PowerShellResult(0, s.Contains("takeown") ? "OK:C:\\ProgramData\\PCTool\\acl\\x.acl" : verify, ""));
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(new GrantFullAccessFix(d, "CAP QUYEN", Run("True")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.StillPresent, (await Exec().RunAsync(new GrantFullAccessFix(d, "CAP QUYEN", Run("False")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new GrantFullAccessFix(d, "CAP QUYEN", Fake("", 1, "takeown lỗi")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new GrantFullAccessFix(d, "CAP QUYEN", Fake("rác")), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Move_Needs_Exact_Code_With_Name_And_Script_Never_Deletes()
    {
        using var t = new TempDir();
        var d = Dir(t, "Thu muc cu");
        Assert.Equal("CHUYEN Thu muc cu", StubbornGuard.MoveCode(d));
        Assert.Throws<ArgumentException>(() => new StubbornMoveToTrashFix(d, "CHUYEN"));
        Assert.Throws<ArgumentException>(() => new StubbornMoveToTrashFix(d, "chuyen thu muc cu"));
        var s = new StubbornMoveToTrashFix(d, "CHUYEN Thu muc cu").BuildScript();
        Assert.Contains("Move-Item", s);
        Assert.DoesNotContain("Remove-Item", s);
        Assert.DoesNotContain("rd /s", s);
        Assert.DoesNotContain("-Force:$true", s);
        Assert.Contains(@"PCTool\trash", s);
        Assert.Contains("vi-tri-cu.txt", s);
    }

    [Theory]
    [InlineData("""{"name":"x","trash":"t","originalGone":true,"inTrash":true}""", FixOutcome.Succeeded)]
    [InlineData("""{"name":"x","trash":"t","originalGone":false,"inTrash":true}""", FixOutcome.Failed)]
    [InlineData("""{"name":"x","trash":"t","originalGone":true,"inTrash":false}""", FixOutcome.Failed)]
    [InlineData("rác", FixOutcome.Failed)]
    public async Task Move_Result_Is_Checked(string json, FixOutcome expected)
    {
        using var t = new TempDir();
        var d = Dir(t, "cu");
        Assert.Equal(expected, (await Exec().RunAsync(new StubbornMoveToTrashFix(d, "CHUYEN cu", Fake(json)), Real(), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Move_Error_Says_Nothing_Deleted_And_DryRun_SafeMode_Skip()
    {
        using var t = new TempDir();
        var d = Dir(t, "cu");
        var res = await Exec().RunAsync(new StubbornMoveToTrashFix(d, "CHUYEN cu", Fake("", 1, "bị khóa")), Real(), null, CancellationToken.None);
        Assert.Equal(FixOutcome.Failed, res.Outcome);
        Assert.Contains("Chưa có gì bị xóa", res.Message);
        var calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(new StubbornMoveToTrashFix(d, "CHUYEN cu", run), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(new StubbornMoveToTrashFix(d, "CHUYEN cu", run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EmptyTrash_Fixed_Path_Code_And_Results()
    {
        Assert.Throws<ArgumentException>(() => new EmptyTrashFix("xoa thung"));
        Assert.Contains(@"PCTool\trash", EmptyTrashFix.Script);
        Assert.DoesNotContain("$args", EmptyTrashFix.Script);
        Assert.DoesNotContain("{{", EmptyTrashFix.Script);
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(new EmptyTrashFix("XOA THUNG", Fake("LEFT:0")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Succeeded, (await Exec().RunAsync(new EmptyTrashFix("XOA THUNG", Fake("EMPTY")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new EmptyTrashFix("XOA THUNG", Fake("LEFT:3")), Real(), null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Failed, (await Exec().RunAsync(new EmptyTrashFix("XOA THUNG", Fake("rác")), Real(), null, CancellationToken.None)).Outcome);
        var calls = 0;
        ScriptRunner run = (_, _) => { calls++; return Task.FromResult(new PowerShellResult(0, "", "")); };
        Assert.Equal(FixOutcome.Simulated, (await Exec().RunAsync(new EmptyTrashFix("XOA THUNG", run), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(FixOutcome.Blocked, (await Exec().RunAsync(new EmptyTrashFix("XOA THUNG", run), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, calls);
    }
}
