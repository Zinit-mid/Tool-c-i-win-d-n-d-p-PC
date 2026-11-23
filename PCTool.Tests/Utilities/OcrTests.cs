using PCTool.Core.AppInstaller;
using PCTool.Core.Ocr;
using Xunit;

namespace PCTool.Tests.Utilities;

public class OcrTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();
        public ProcessResult Result { get; set; } = new(0, "Xin chào", "");
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
        { Calls.Add((fileName, args)); return Task.FromResult(Result); }
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C:\khong-co\a.png")]
    public void CheckInput_RejectsMissing(string? p) => Assert.NotNull(OcrInfo.CheckInput(p));

    [Fact]
    public void CheckInput_RejectsWrongExtension_AndAcceptsImagesAndPdf()
    {
        var dir = TempDir();
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
        var dir = TempDir();
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
    public void Tesseract_InstallEntry_IsValidWinget()
    {
        Assert.Empty(AppEntryValidator.Validate(TesseractInfo.InstallEntry));
        Assert.Equal("UB-Mannheim.TesseractOCR", TesseractInfo.InstallEntry.WingetId);
    }

    [Fact]
    public void Tesseract_Modes_AreFixed_AndSpecChecked()
    {
        Assert.Equal(new[] { "vie+eng", "vie", "eng" }, TesseractInfo.Modes.Select(m => m.Spec));
        Assert.False(TesseractInfo.IsSpecAllowed("vie; calc"));
        Assert.False(TesseractInfo.IsSpecAllowed(null));
        Assert.Throws<ArgumentException>(() => TesseractInfo.BuildArgs(@"C:\a.png", "xx", @"C:\t"));
    }

    [Fact]
    public void Tesseract_BuildArgs_RequireRootedImage_SeparateArgs()
    {
        var a = TesseractInfo.BuildArgs(@"C:\Anh\a b.png", "vie+eng", @"C:\Program Files\Tesseract-OCR\tessdata");
        Assert.Equal(@"C:\Anh\a b.png", a[0]);
        Assert.Equal("stdout", a[1]);
        Assert.Contains("vie+eng", a);
        Assert.Throws<ArgumentException>(() => TesseractInfo.BuildArgs("-l", "vie", @"C:\t"));
    }

    [Fact]
    public void Tesseract_DataUrl_OnlyKnownCodes_FromOfficialRepo()
    {
        Assert.StartsWith("https://github.com/tesseract-ocr/tessdata_fast/raw/main/", TesseractInfo.DataUrl("vie"));
        Assert.Throws<ArgumentException>(() => TesseractInfo.DataUrl("../x"));
        Assert.True(TesseractDataFix.HostAllowed(new Uri("https://raw.githubusercontent.com/a/b")));
        Assert.False(TesseractDataFix.HostAllowed(new Uri("https://evilgithub.com/a")));
        Assert.False(TesseractDataFix.HostAllowed(new Uri("http://github.com/a")));
    }

    [Fact]
    public void Tesseract_FindExe_UsesFakeExists()
    {
        Assert.Null(TesseractInfo.FindExe(_ => false));
        Assert.EndsWith(@"Tesseract-OCR\tesseract.exe", TesseractInfo.FindExe(_ => true));
    }

    [Fact]
    public async Task Tesseract_Recognize_PassesArgs_AndReturnsText()
    {
        var run = new FakeRunner();
        var r = await TesseractInfo.RecognizeAsync(run, @"C:\T\tesseract.exe", @"C:\a.png", "vie", CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("Xin chào", r.Text);
        Assert.Equal(@"C:\T\tesseract.exe", run.Calls[0].File);
        Assert.Contains(@"C:\T\tessdata", run.Calls[0].Args);

        run.Result = new ProcessResult(1, "", "lỗi");
        var bad = await TesseractInfo.RecognizeAsync(run, @"C:\T\tesseract.exe", @"C:\a.png", "vie", CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.Equal("lỗi", bad.Error);
    }

    [Fact]
    public async Task DataFix_DownloadsMissing_SkipsExisting_RejectsTinyFile()
    {
        var dir = TempDir();
        try
        {
            var exe = Path.Combine(dir, "tesseract.exe");
            var calls = new List<string>();
            Func<string, string, CancellationToken, Task> ok = (url, dest, _) =>
            { calls.Add(url); File.WriteAllBytes(dest, new byte[200_000]); return Task.CompletedTask; };

            Assert.Throws<ArgumentException>(() => new TesseractDataFix(exe, new[] { "xx" }));
            var fix = new TesseractDataFix(exe, new[] { "vie", "eng" }, ok);
            Directory.CreateDirectory(TesseractInfo.TessdataDir(exe));
            File.WriteAllBytes(TesseractInfo.DataFile(exe, "eng"), new byte[200_000]);

            var res = await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
            Assert.True(res.IsSuccess);
            Assert.Single(calls);
            Assert.EndsWith("vie.traineddata", calls[0]);
            Assert.True(await fix.VerifyAsync(CancellationToken.None));
            Assert.False(File.Exists(TesseractInfo.DataFile(exe, "vie") + ".part"));

            File.Delete(TesseractInfo.DataFile(exe, "vie"));
            Func<string, string, CancellationToken, Task> tiny = (_, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; };
            var bad = await new TesseractDataFix(exe, new[] { "vie" }, tiny).ExecuteAsync(new Progress<string>(), CancellationToken.None);
            Assert.False(bad.IsSuccess);
            Assert.False(File.Exists(TesseractInfo.DataFile(exe, "vie")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
