using PCTool.Core.AppInstaller;
using PCTool.Core.Video;
using Xunit;

namespace PCTool.Tests.Utilities;

public class VideoOpsTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();
        public int Exit { get; set; }
        public bool CreateOutput { get; set; } = true;
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add((fileName, args));
            if (Exit == 0 && CreateOutput) File.WriteAllBytes(args[^1], new byte[10]);
            return Task.FromResult(new ProcessResult(Exit, "", Exit == 0 ? "" : "boom"));
        }
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Vid(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("01:02:03", 3723)]
    [InlineData("00:01:30.5", 90.5)]
    [InlineData(" 0 ", 0)]
    public void TryParseTime_Accepts(string s, double expected)
    {
        Assert.Null(VideoInfo.TryParseTime(s, out var v));
        Assert.Equal(expected, v, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("-5")]
    [InlineData("1:30; calc")]
    public void TryParseTime_Rejects(string? s) => Assert.NotNull(VideoInfo.TryParseTime(s, out _));

    [Fact]
    public void FindFfmpeg_PathThenWingetLinksThenPackages()
    {
        Assert.Null(VideoInfo.FindFfmpeg(_ => false, _ => Array.Empty<string>(), @"C:\a;C:\b", @"C:\L", @"C:\PF"));
        Assert.Equal(@"C:\b\ffmpeg.exe", VideoInfo.FindFfmpeg(p => p == @"C:\b\ffmpeg.exe", _ => Array.Empty<string>(), @"C:\a;C:\b", @"C:\L", @"C:\PF"));
        Assert.Equal(@"C:\L\Microsoft\WinGet\Links\ffmpeg.exe",
            VideoInfo.FindFfmpeg(p => p.EndsWith(@"Links\ffmpeg.exe"), _ => Array.Empty<string>(), "", @"C:\L", @"C:\PF"));

        var pkgRoot = @"C:\L\Microsoft\WinGet\Packages";
        var pkg = pkgRoot + @"\Gyan.FFmpeg_x";
        var build = pkg + @"\ffmpeg-7-full_build";
        Func<string, IEnumerable<string>> dirs = d => d == pkgRoot ? new[] { pkg, pkgRoot + @"\Other.App" } : d == pkg ? new[] { build } : Array.Empty<string>();
        Assert.Equal(build + @"\bin\ffmpeg.exe", VideoInfo.FindFfmpeg(p => p == build + @"\bin\ffmpeg.exe", dirs, "", @"C:\L", @"C:\PF"));
    }

    [Fact]
    public void SortByName_IsNatural_NumbersByValue()
    {
        var sorted = VideoInfo.SortByName(new[] { "10.mp4", "2.mp4", "1.mp4", "Tap 9.mp4", "Tap 10.mp4", "Tap 09b.mp4" }.Select(n => Path.Combine("C:", "v", n)));
        Assert.Equal(new[] { "1.mp4", "2.mp4", "10.mp4", "Tap 9.mp4", "Tap 09b.mp4", "Tap 10.mp4" }, sorted.Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void SortByDate_OldestFirst()
    {
        var dir = TempDir();
        try
        {
            var a = Vid(dir, "a.mp4"); var b = Vid(dir, "b.mp4");
            File.SetLastWriteTimeUtc(a, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(b, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new[] { "b.mp4", "a.mp4" }, VideoInfo.SortByDate(new[] { a, b }).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merge_KeepsGivenOrder_InActionsAndArgs()
    {
        var dir = TempDir();
        try
        {
            var a = Vid(dir, "a.mp4"); var b = Vid(dir, "b.mp4"); var c = Vid(dir, "c.mp4");
            var fix = new VideoMergeFix("ffmpeg.exe", new[] { c, a, b }, false);
            Assert.Contains("c.mp4 → a.mp4 → b.mp4", fix.Actions[0]);
            var args = fix.BuildReencodeArgs();
            var inputs = args.Select((x, i) => (x, i)).Where(t => t.x == "-i").Select(t => Path.GetFileName(args[t.i + 1])).ToList();
            Assert.Equal(new[] { "c.mp4", "a.mp4", "b.mp4" }, inputs);
            Assert.Equal("file '" + c.Replace('\\', '/') + "'\nfile '" + a.Replace('\\', '/') + "'\nfile '" + b.Replace('\\', '/') + "'\n", VideoInfo.ConcatListText(new[] { c, a, b }));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InstallEntry_IsValidWinget() => Assert.Empty(AppEntryValidator.Validate(VideoInfo.InstallEntry));

    [Fact]
    public void ConcatList_EscapesQuotes_UsesForwardSlashes()
    {
        var t = VideoInfo.ConcatListText(new[] { @"C:\Video\a b'c.mp4" });
        Assert.Equal("file 'C:/Video/a b'\\''c.mp4'\n", t);
    }

    [Fact]
    public void Cut_Lossless_Args_NoOverwrite_SameExt()
    {
        var dir = TempDir();
        try
        {
            var f = Vid(dir, "phim.mkv");
            var fix = new VideoCutFix(@"C:\f\ffmpeg.exe", f, "00:01:00", "00:01:30", true);
            Assert.Equal(Path.Combine(dir, "phim-cat.mkv"), fix.OutputPath);
            var a = fix.BuildArgs();
            Assert.Contains("-n", a);
            Assert.DoesNotContain("-y", a);
            Assert.Equal("copy", a[a.ToList().IndexOf("-c") + 1]);
            Assert.Equal("60", a[a.ToList().IndexOf("-ss") + 1]);
            Assert.Equal("30", a[a.ToList().IndexOf("-t") + 1]);
            Assert.Equal(fix.OutputPath, a[^1]);
            Assert.False(fix.RequiresRestorePoint);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cut_Reencode_UsesMp4_AndNewNameIfExists()
    {
        var dir = TempDir();
        try
        {
            var f = Vid(dir, "a.avi");
            File.WriteAllText(Path.Combine(dir, "a-cat.mp4"), "da co");
            var fix = new VideoCutFix(@"C:\f\ffmpeg.exe", f, "0", "10", false);
            Assert.NotEqual(Path.Combine(dir, "a-cat.mp4"), fix.OutputPath);
            Assert.EndsWith(".mp4", fix.OutputPath);
            Assert.Contains("libx264", fix.BuildArgs());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cut_Validations()
    {
        var dir = TempDir();
        try
        {
            var f = Vid(dir, "a.mp4");
            Assert.Throws<ArgumentException>(() => new VideoCutFix("x", f, "10", "5", true));
            Assert.Throws<ArgumentException>(() => new VideoCutFix("x", f, "abc", "5", true));
            Assert.Throws<ArgumentException>(() => new VideoCutFix("x", Vid(dir, "a.exe"), "1", "5", true));
            Assert.Throws<ArgumentException>(() => new VideoCutFix("x", Path.Combine(dir, "khong-co.mp4"), "1", "5", true));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Cut_Runs_AndVerifies_AndReportsFailure()
    {
        var dir = TempDir();
        try
        {
            var f = Vid(dir, "a.mp4");
            var run = new FakeRunner();
            var fix = new VideoCutFix(@"C:\f\ffmpeg.exe", f, "1", "3", true, run);
            var res = await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
            Assert.True(res.IsSuccess);
            Assert.Equal(@"C:\f\ffmpeg.exe", run.Calls[0].File);
            Assert.True(await fix.VerifyAsync(CancellationToken.None));

            var bad = new FakeRunner { Exit = 1 };
            var r2 = await new VideoCutFix(@"C:\f\ffmpeg.exe", f, "1", "3", false, bad).ExecuteAsync(new Progress<string>(), CancellationToken.None);
            Assert.False(r2.IsSuccess);
            Assert.Contains("boom", r2.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Merge_Lossless_UsesConcatListFile_AndCleansIt()
    {
        var dir = TempDir();
        try
        {
            var a = Vid(dir, "1.mp4"); var b = Vid(dir, "2.mp4");
            var run = new FakeRunner();
            var fix = new VideoMergeFix("ffmpeg.exe", new[] { a, b }, true, run);
            Assert.Equal(Path.Combine(dir, "1-ghep.mp4"), fix.OutputPath);
            var res = await fix.ExecuteAsync(new Progress<string>(), CancellationToken.None);
            Assert.True(res.IsSuccess);
            var args = run.Calls[0].Args;
            Assert.Contains("concat", args);
            Assert.Contains("copy", args);
            var list = args[args.ToList().IndexOf("-i") + 1];
            Assert.False(File.Exists(list));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merge_Reencode_FilterHasEveryInput_AndLimits()
    {
        var dir = TempDir();
        try
        {
            var a = Vid(dir, "1.mp4"); var b = Vid(dir, "2.mkv"); var c = Vid(dir, "3.mov");
            var fix = new VideoMergeFix("ffmpeg.exe", new[] { a, b, c }, false);
            var args = fix.BuildReencodeArgs();
            Assert.Equal(3, args.Count(x => x == "-i"));
            var filter = args[args.ToList().IndexOf("-filter_complex") + 1];
            Assert.Contains("concat=n=3:v=1:a=1", filter);
            Assert.Contains("[2:a]", filter);
            Assert.EndsWith(".mp4", fix.OutputPath);
            Assert.Throws<ArgumentException>(() => new VideoMergeFix("x", new[] { a }, true));
            Assert.Throws<ArgumentException>(() => new VideoMergeFix("x", new[] { a, Path.Combine(dir, "khong.mp4") }, true));
        }
        finally { Directory.Delete(dir, true); }
    }
}
