using PCTool.Core.Ocr;
using PCTool.Core.Windows;
using Xunit;

namespace PCTool.Tests.Utilities;

public class OcrTests
{
    [Fact]
    public void Languages_AreVietnameseAndEnglish_WithFixedCapabilityNames()
    {
        Assert.Equal(new[] { "vi", "en" }, OcrInfo.Languages.Select(l => l.Tag));
        Assert.All(OcrInfo.Languages, l => Assert.Matches(@"^Language\.OCR~~~[a-z]{2}-[A-Z]{2}~0\.0\.1\.0$", l.Capability));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C:\khong-co\a.png")]
    public void CheckInput_RejectsMissing(string? p) => Assert.NotNull(OcrInfo.CheckInput(p));

    [Fact]
    public void CheckInput_RejectsWrongExtension_AndAcceptsImagesAndPdf()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ext in new[] { ".png", ".JPG", ".tiff", ".pdf" })
            {
                var f = Path.Combine(dir, "a" + ext);
                File.WriteAllText(f, "x");
                Assert.Null(OcrInfo.CheckInput(f));
            }

            var bad = Path.Combine(dir, "a.exe");
            File.WriteAllText(bad, "x");
            Assert.NotNull(OcrInfo.CheckInput(bad));
            Assert.NotNull(OcrInfo.CheckInput(@"\\server\share\a.png"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OutputPath_IsBesideInput_NeverOverwrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, "bien-lai.pdf");
            var p1 = OcrInfo.OutputPath(input);
            Assert.Equal(Path.Combine(dir, "bien-lai-chu.txt"), p1);
            File.WriteAllText(p1, "x");
            var p2 = OcrInfo.OutputPath(input);
            Assert.NotEqual(p1, p2);
            Assert.Equal(dir, Path.GetDirectoryName(p2));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void JoinPages_SinglePageIsPlain_ManyPagesHaveSeparators()
    {
        Assert.Equal("abc", OcrInfo.JoinPages(new[] { " abc " }));
        var many = OcrInfo.JoinPages(new[] { "a", "b" });
        Assert.Contains("--- Trang 1 ---", many);
        Assert.Contains("--- Trang 2 ---", many);
        Assert.Equal("", OcrInfo.JoinPages(Array.Empty<string>()));
    }

    [Fact]
    public async Task InstallFix_OnlyKnownLanguage_AndRunsFixedScript()
    {
        Assert.Throws<ArgumentException>(() => new OcrLanguageInstallFix("xx'; calc"));
        var scripts = new List<string>();
        ScriptRunner run = (s, _) => { scripts.Add(s); return Task.FromResult(new PowerShellResult(0, s.Contains("Get-WindowsCapability") ? "Installed" : "", "")); };
        var fix = new OcrLanguageInstallFix("vi", run);
        Assert.Contains("Language.OCR~~~vi-VN~0.0.1.0", fix.InstallScript());
        Assert.False(fix.RequiresRestorePoint);
        var res = await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
        Assert.True(res.IsSuccess);
        Assert.True(await fix.VerifyAsync(CancellationToken.None));
        Assert.Equal(2, scripts.Count);
    }

    [Fact]
    public async Task InstallFix_NotInstalledAfter_FailsVerify()
    {
        ScriptRunner run = (_, _) => Task.FromResult(new PowerShellResult(0, "NotPresent", ""));
        Assert.False(await new OcrLanguageInstallFix("en", run).VerifyAsync(CancellationToken.None));
    }
}
