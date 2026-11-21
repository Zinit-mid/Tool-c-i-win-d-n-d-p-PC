using System.Security.Cryptography;
using PCTool.Core.Update;
using Xunit;

namespace PCTool.Tests.Update;

public class UpdateValidatorTests
{
    private const string O = "Zinit-mid", R = "Tool-c-i-win-d-n-d-p-PC";

    [Theory]
    [InlineData("https://github.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/releases/download/v0.1.23/PCTool.exe", true)]
    [InlineData("https://github.com/zinit-mid/tool-c-i-win-d-n-d-p-pc/releases/download/v1/PCTool.exe", true)]
    [InlineData("http://github.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/releases/download/v1/PCTool.exe", false)]
    [InlineData("https://evil.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/releases/download/v1/PCTool.exe", false)]
    [InlineData("https://github.com.evil.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/releases/download/v1/PCTool.exe", false)]
    [InlineData("https://user@github.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/releases/download/v1/PCTool.exe", false)]
    [InlineData("https://github.com/other/repo/releases/download/v1/PCTool.exe", false)]
    [InlineData("https://github.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/archive/main.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("https://github.com/Zinit-mid/x y", false)]
    public void InitialUrl(string? url, bool ok) => Assert.Equal(ok, UpdateValidator.IsAllowedInitialUrl(url, O, R));

    [Theory]
    [InlineData("https://github.com/a/b", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://githubusercontent.com.evil.com/x", false)]
    [InlineData("https://evilgithubusercontent.com/x", false)]
    [InlineData("http://objects.githubusercontent.com/x", false)]
    [InlineData("https://evil.com/x", false)]
    public void FinalUri(string url, bool ok) => Assert.Equal(ok, UpdateValidator.IsAllowedFinalUri(new Uri(url)));

    [Fact] public void FinalUri_NullRejected() => Assert.False(UpdateValidator.IsAllowedFinalUri(null));

    [Fact]
    public void ParseSha256()
    {
        var h = new string('A', 64);
        Assert.Equal(new string('a', 64), UpdateValidator.ParseSha256("sha256:" + h));
        Assert.Null(UpdateValidator.ParseSha256(null));
        Assert.Null(UpdateValidator.ParseSha256("sha1:" + h));
        Assert.Null(UpdateValidator.ParseSha256("sha256:abc"));
        Assert.Null(UpdateValidator.ParseSha256("sha256:" + new string('g', 64)));
    }

    private static string MakeFile(byte[] data)
    {
        var p = Path.Combine(Path.GetTempPath(), "pct-upd-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(p, data);
        return p;
    }

    private static byte[] Exe(int size = 1_200_000)
    {
        var b = new byte[size]; b[0] = (byte)'M'; b[1] = (byte)'Z'; new Random(1).NextBytes(b.AsSpan(2)); return b;
    }

    [Fact]
    public async Task Verify_Ok()
    {
        var d = Exe(); var p = MakeFile(d);
        try { await UpdateValidator.VerifyFileAsync(p, d.Length, Convert.ToHexString(SHA256.HashData(d)), default); }
        finally { File.Delete(p); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Verify_MissingHash_Rejected(string? sha)
    {
        var d = Exe(); var p = MakeFile(d);
        try { await Assert.ThrowsAsync<InvalidDataException>(() => UpdateValidator.VerifyFileAsync(p, d.Length, sha, default)); }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task Verify_WrongHash_Rejected()
    {
        var d = Exe(); var p = MakeFile(d);
        try { await Assert.ThrowsAsync<InvalidDataException>(() => UpdateValidator.VerifyFileAsync(p, d.Length, new string('0', 64), default)); }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task Verify_NotExe_Rejected()
    {
        var d = Exe(); d[0] = (byte)'X'; var p = MakeFile(d);
        try { await Assert.ThrowsAsync<InvalidDataException>(() => UpdateValidator.VerifyFileAsync(p, d.Length, Convert.ToHexString(SHA256.HashData(d)), default)); }
        finally { File.Delete(p); }
    }

    [Fact]
    public async Task Verify_SizeMismatch_And_TooSmall_Rejected()
    {
        var d = Exe(); var p = MakeFile(d);
        var small = Exe(1000); var p2 = MakeFile(small);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateValidator.VerifyFileAsync(p, d.Length + 1, Convert.ToHexString(SHA256.HashData(d)), default));
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateValidator.VerifyFileAsync(p2, small.Length, Convert.ToHexString(SHA256.HashData(small)), default));
        }
        finally { File.Delete(p); File.Delete(p2); }
    }
}
