using System.ComponentModel;
using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;
using PCTool.Tests.Framework;

namespace PCTool.Tests.AppInstaller;

public sealed class InstallFixTests : IDisposable
{
    private readonly string _desktop = Path.Combine(Path.GetTempPath(), "pctool-desk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_desktop, true); } catch (IOException) { }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = new();
        public Func<string[], ProcessResult> Respond { get; set; } = _ => new(0, "", "");
        public Exception? Throw { get; set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add((fileName, args.ToArray()));
            if (Throw != null) throw Throw;
            return Task.FromResult(Respond(args.ToArray()));
        }
    }

    private static readonly AppEntry Zalo = new() { Id = "zalo", Name = "Zalo", Kind = AppKind.Winget, WingetId = "VNGCorp.Zalo" };
    private static readonly AppEntry Facebook = new() { Id = "facebook", Name = "Facebook", Kind = AppKind.WebShortcut, Url = "https://www.facebook.com" };
    private static readonly AppEntry Page = new() { Id = "pg", Name = "Trang tải", Kind = AppKind.DownloadPage, Url = "https://example.com/get" };

    private InstallAppFix Make(AppEntry e, FakeRunner r, List<string>? opened = null) =>
        new(e, r, _desktop, u => opened?.Add(u));

    private static FixExecutor Executor() => new(new FakeRestorePoints());
    private static readonly FixOptions Real = new() { DryRun = false };

    [Fact]
    public void WingetArgs_AreSeparateArguments_WithExactId()
    {
        var fix = Make(Zalo, new FakeRunner());

        var args = fix.InstallArgs();

        Assert.Equal(new[] { "install", "--id", "VNGCorp.Zalo", "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" }, args);
        Assert.Equal(new[] { "list", "--id", "VNGCorp.Zalo", "--exact", "--accept-source-agreements" }, fix.ListArgs());
    }

    [Fact]
    public void Constructor_RejectsInvalidEntry_EvenIfCallerForgotToValidate()
    {
        var evil = new AppEntry { Id = "x", Name = "X", Kind = AppKind.Winget, WingetId = "--source" };

        Assert.Throws<ArgumentException>(() => Make(evil, new FakeRunner()));
    }

    [Fact]
    public async Task DryRun_RunsNothing_ForAllKinds()
    {
        var r = new FakeRunner();
        var opened = new List<string>();

        foreach (var e in new[] { Zalo, Facebook, Page })
            Assert.Equal(FixOutcome.Simulated,
                (await Executor().RunAsync(Make(e, r, opened), new FixOptions { DryRun = true }, null, CancellationToken.None)).Outcome);

        Assert.Empty(r.Calls);
        Assert.Empty(opened);
        Assert.False(Directory.Exists(_desktop));
    }

    [Fact]
    public async Task SafeMode_BlocksInstall()
    {
        var r = new FakeRunner();

        var result = await Executor().RunAsync(Make(Zalo, r), new FixOptions { DryRun = false, SafeMode = true }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Blocked, result.Outcome);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Winget_Success_InstallsThenVerifies()
    {
        var r = new FakeRunner();

        var result = await Executor().RunAsync(Make(Zalo, r), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal("install", r.Calls[0].Args[0]);
        Assert.Equal("list", r.Calls[1].Args[0]);
        Assert.All(r.Calls, c => Assert.Equal("winget", c.File));
    }

    [Theory]
    [InlineData(InstallAppFix.WingetAlreadyInstalled)]
    [InlineData(InstallAppFix.WingetNoApplicableUpgrade)]
    public async Task Winget_AlreadyInstalled_IsSuccess(int code)
    {
        var r = new FakeRunner { Respond = a => a[0] == "install" ? new(code, "", "") : new(0, "", "") };

        var result = await Executor().RunAsync(Make(Zalo, r), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Contains("đã có sẵn", result.Message);
    }

    [Fact]
    public async Task Winget_OtherError_IsFailed_WithHexCode_AndNoVerify()
    {
        var r = new FakeRunner { Respond = _ => new(unchecked((int)0x8A150014), "No package found", "") };

        var result = await Executor().RunAsync(Make(Zalo, r), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Contains("0x8A150014", result.Message);
        Assert.Single(r.Calls);
    }

    [Fact]
    public async Task Winget_Missing_GivesGuidance()
    {
        var r = new FakeRunner { Throw = new Win32Exception("not found") };

        var result = await Executor().RunAsync(Make(Zalo, r), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Contains("App Installer", result.Message);
    }

    [Fact]
    public async Task Winget_InstalledButNotListed_IsStillPresent()
    {
        var r = new FakeRunner { Respond = a => a[0] == "list" ? new(unchecked((int)0x8A150014), "", "") : new(0, "", "") };

        var result = await Executor().RunAsync(Make(Zalo, r), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.StillPresent, result.Outcome);
    }

    [Fact]
    public async Task WebShortcut_CreatesUrlFileOnDesktop_WithExactContent()
    {
        var result = await Executor().RunAsync(Make(Facebook, new FakeRunner()), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal("[InternetShortcut]\r\nURL=https://www.facebook.com\r\n",
            File.ReadAllText(Path.Combine(_desktop, "Facebook.url")));
    }

    [Fact]
    public async Task WebShortcut_Twice_DoesNotOverwrite()
    {
        await Executor().RunAsync(Make(Facebook, new FakeRunner()), Real, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(_desktop, "Facebook.url"), "người dùng đã sửa");

        var result = await Executor().RunAsync(Make(Facebook, new FakeRunner()), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal("người dùng đã sửa", File.ReadAllText(Path.Combine(_desktop, "Facebook.url")));
    }

    [Fact]
    public async Task WebShortcut_NameWithPathCharacters_StaysInsideDesktop()
    {
        var e = new AppEntry { Id = "sneaky", Name = @"..\..\evil/name", Kind = AppKind.WebShortcut, Url = "https://example.com" };

        await Executor().RunAsync(Make(e, new FakeRunner()), Real, null, CancellationToken.None);

        var files = Directory.GetFiles(_desktop);
        Assert.Single(files);
        Assert.Equal(Path.GetFullPath(_desktop), Path.GetFullPath(Path.GetDirectoryName(files[0])!));
    }

    [Fact]
    public async Task DownloadPage_OpensUrl_Only()
    {
        var opened = new List<string>();
        var r = new FakeRunner();

        var result = await Executor().RunAsync(Make(Page, r, opened), Real, null, CancellationToken.None);

        Assert.Equal(FixOutcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "https://example.com/get" }, opened);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task WingetCheck_NoFinding_WhenWingetWorks()
    {
        var findings = await new WingetCheck(new FakeRunner { Respond = _ => new(0, "v1.8", "") }).RunAsync(CancellationToken.None);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WingetCheck_ReportsMissing(bool throwsInsteadOfExitCode)
    {
        var r = throwsInsteadOfExitCode
            ? new FakeRunner { Throw = new Win32Exception() }
            : new FakeRunner { Respond = _ => new(1, "", "") };

        var f = Assert.Single(await new WingetCheck(r).RunAsync(CancellationToken.None));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Empty(f.Fixes);
    }
}
