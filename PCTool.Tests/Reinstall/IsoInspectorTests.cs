using PCTool.Core.Reinstall;
using PCTool.Core.Windows;

namespace PCTool.Tests.Reinstall;

public sealed class IsoInspectorTests
{
    private const long GB = 1_000_000_000L;

    private static IsoImage Img(string version = "10.0.22631.2861", string arch = "9", string name = "Windows 11 Pro", int idx = 1) =>
        new(idx, name, "Professional", version, arch, new[] { "en-US" }, 5 * GB);

    private static IsoInfo Info(bool wim = true, long wimSize = 5 * GB, bool esd = false, long esdSize = 0, bool efi = true,
        bool mounted = false, IsoImage[]? images = null) =>
        new(@"E:\Win11.iso", 6 * GB, mounted, wim, wimSize, esd, esdSize, efi, images is { Length: > 0 } ? images : new[] { Img() });

    private static ScriptRunner Fake(string output, string error = "") =>
        (_, _) => Task.FromResult(new PowerShellResult(0, output, error));

    // ---------------------------------------------------------------- Đường dẫn

    [Theory]
    [InlineData(@"E:\Win11.iso", true)]
    [InlineData(@"D:\Tải về\Win11_23H2 (1).ISO", true)]
    [InlineData(@"E:\x.iso'; calc; '", false)]
    [InlineData(@"E:\x$(calc).iso", false)]
    [InlineData(@"E:\x.img", false)]
    [InlineData(@"E:\iso\", false)]
    [InlineData(@"E:\.iso", false)]
    [InlineData(@"\\srv\share\a.iso", false)]
    [InlineData(@"a.iso", false)]
    [InlineData(@"C:\Windows\a.iso", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsoPath_Validation(string? p, bool ok) => Assert.Equal(ok, IsoInspector.IsValidIsoPath(p, out _));

    [Fact]
    public async Task Inspect_Rejects_Bad_Path_Without_Running_Script()
    {
        var ran = false;
        var insp = new IsoInspector((_, _) => { ran = true; return Task.FromResult(new PowerShellResult(0, "{}", "")); });
        await Assert.ThrowsAsync<ArgumentException>(() => insp.InspectAsync(@"E:\a'; calc; '.iso", CancellationToken.None));
        Assert.False(ran);
    }

    // ---------------------------------------------------------------- Script

    [Fact]
    public async Task Script_Embeds_Path_ReadOnly_And_Never_Writes()
    {
        string? seen = null;
        var insp = new IsoInspector((s, _) => { seen = s; return Task.FromResult(new PowerShellResult(0, GoodJson, "")); });
        await insp.InspectAsync(@"E:\Win11.iso", CancellationToken.None);
        Assert.NotNull(seen);
        Assert.Contains(@"$path = 'E:\Win11.iso'", seen);
        Assert.DoesNotContain("@@ISO_PATH@@", seen);
        Assert.Contains("-Access ReadOnly", seen);
        foreach (var bad in new[] { "Clear-Disk", "Remove-Partition", "Format-Volume", "New-Partition", "Set-Disk", "Initialize-Disk",
                     "Set-Partition", "Remove-Item", "diskpart", "Copy-Item", "Set-Content", "Out-File", "Mount-WindowsImage", "Expand-WindowsImage" })
            Assert.DoesNotContain(bad, seen!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_Only_Dismounts_When_It_Mounted()
    {
        var s = IsoInspector.ScriptTemplate;
        Assert.Contains("if (-not $o.wasMounted) { Dismount-DiskImage", s);
        Assert.Contains("} finally {", s);
    }

    // ---------------------------------------------------------------- Đọc kết quả

    private const string GoodJson = """
{"ok":true,"error":"","wasMounted":false,"isoSize":6500000000,"hasWim":true,"wimSize":5200000000,"hasEsd":false,"esdSize":0,"hasEfi":true,
 "images":[{"Index":1,"Name":"Windows 11 Home","EditionId":"Core","Version":"10.0.22631.2861","Architecture":"9","Languages":["en-US"],"Size":16000000000},
           {"Index":6,"Name":"Windows 11 Pro","EditionId":"Professional","Version":"10.0.22631.2861","Architecture":"9","Languages":"en-US","Size":16500000000}]}
""";

    [Fact]
    public void Parse_Good_Json()
    {
        var i = IsoInspector.Parse(@"E:\Win11.iso", GoodJson);
        Assert.True(i.HasInstallWim);
        Assert.True(i.HasEfiBoot);
        Assert.Equal(5_200_000_000L, i.InstallWimBytes);
        Assert.Equal(2, i.Images.Count);
        Assert.Equal("Windows 11", i.Images[0].Family);
        Assert.Equal("x64", i.Images[1].Arch);
        Assert.Equal(new[] { "en-US" }, i.Images[1].Languages); // chuỗi đơn cũng được chấp nhận
        Assert.Equal(22631, i.Images[0].Build);
    }

    [Fact]
    public void Parse_Single_Image_Object_Tolerated()
    {
        const string j = """{"ok":true,"hasWim":true,"wimSize":1,"hasEfi":true,"images":{"Index":1,"Name":"X","Version":"10.0.19045.1","Architecture":"x64","Languages":["vi-VN"],"Size":1}}""";
        var i = IsoInspector.Parse(@"E:\a.iso", j);
        Assert.Single(i.Images);
        Assert.Equal("Windows 10", i.Images[0].Family);
    }

    [Fact]
    public void Parse_Script_Error_Throws_With_Message()
    {
        var ex = Assert.Throws<FormatException>(() => IsoInspector.Parse(@"E:\a.iso", """{"ok":false,"error":"Không tìm thấy file ISO."}"""));
        Assert.Contains("Không tìm thấy file ISO", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("oops")]
    [InlineData("[1]")]
    public void Parse_Bad_Json_Throws(string j) => Assert.Throws<FormatException>(() => IsoInspector.Parse(@"E:\a.iso", j));

    [Fact]
    public async Task Inspect_Empty_Output_Throws_With_Stderr()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IsoInspector(Fake("", "Access denied")).InspectAsync(@"E:\a.iso", CancellationToken.None));
        Assert.Contains("Access denied", ex.Message);
    }

    [Fact]
    public async Task Inspect_Script_Failure_Becomes_InvalidOperation() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IsoInspector(Fake("""{"ok":false,"error":"x"}""")).InspectAsync(@"E:\a.iso", CancellationToken.None));

    // ---------------------------------------------------------------- Phán đoán

    [Fact]
    public void Healthy_Iso_Usable()
    {
        var v = IsoInspector.Evaluate(Info());
        Assert.True(v.UsableForUsb);
        Assert.Contains(v.Notes, n => n.Contains("tách bằng DISM"));
    }

    [Fact]
    public void Small_Wim_No_Split_Note() =>
        Assert.DoesNotContain(IsoInspector.Evaluate(Info(wimSize: 3 * GB)).Notes, n => n.Contains("tách"));

    [Fact]
    public void No_Install_File_Blocks() =>
        Assert.False(IsoInspector.Evaluate(Info(wim: false, images: new IsoImage[] { })).UsableForUsb);

    [Fact]
    public void No_Efi_Blocks() => Assert.False(IsoInspector.Evaluate(Info(efi: false)).UsableForUsb);

    [Fact]
    public void Big_Esd_Blocks_Small_Esd_Warns()
    {
        Assert.False(IsoInspector.Evaluate(Info(wim: false, esd: true, esdSize: 5 * GB)).UsableForUsb);
        var ok = IsoInspector.Evaluate(Info(wim: false, esd: true, esdSize: 3 * GB));
        Assert.True(ok.UsableForUsb);
        Assert.Contains(ok.Warnings, w => w.Contains("install.esd"));
    }

    [Fact]
    public void Wim_Present_Ignores_Esd_Size() =>
        Assert.True(IsoInspector.Evaluate(Info(wim: true, esd: true, esdSize: 9 * GB)).UsableForUsb);

    [Theory]
    [InlineData("0")]
    [InlineData("x86")]
    [InlineData("12")]
    [InlineData("arm64")]
    public void NonX64_Blocks(string arch) =>
        Assert.False(IsoInspector.Evaluate(Info(images: new[] { Img(arch: arch) })).UsableForUsb);

    [Theory]
    [InlineData("9")]
    [InlineData("x64")]
    [InlineData("AMD64")]
    public void X64_Accepted(string arch) =>
        Assert.True(IsoInspector.Evaluate(Info(images: new[] { Img(arch: arch) })).UsableForUsb);

    [Fact]
    public void Unknown_Arch_Warns_Not_Blocks()
    {
        var v = IsoInspector.Evaluate(Info(images: new[] { Img(arch: "") }));
        Assert.True(v.UsableForUsb);
        Assert.Contains(v.Warnings, w => w.Contains("kiến trúc"));
    }

    [Fact]
    public void No_Images_Listed_Blocks_As_Maybe_Corrupt() =>
        Assert.False(IsoInspector.Evaluate(new IsoInfo(@"E:\a.iso", 6 * GB, false, true, 5 * GB, false, 0, true, Array.Empty<IsoImage>())).UsableForUsb);

    [Fact]
    public void Mixed_Generations_Warn()
    {
        var v = IsoInspector.Evaluate(Info(images: new[] { Img("10.0.22631.1"), Img("10.0.19045.1", idx: 2) }));
        Assert.Contains(v.Warnings, w => w.Contains("lẫn lộn"));
    }

    [Fact]
    public void Already_Mounted_Is_Noted() =>
        Assert.Contains(IsoInspector.Evaluate(Info(mounted: true)).Notes, n => n.Contains("để nguyên"));

    [Theory]
    [InlineData("10.0.22631.2861", 22631, "Windows 11")]
    [InlineData("10.0.22000.1", 22000, "Windows 11")]
    [InlineData("10.0.19045.1", 19045, "Windows 10")]
    [InlineData("6.1.7601", 7601, "Windows (build 7601)")]
    [InlineData("", 0, "Windows (không rõ phiên bản)")]
    [InlineData("abc", 0, "Windows (không rõ phiên bản)")]
    public void Version_To_Family(string v, int build, string family)
    {
        var m = Img(version: v);
        Assert.Equal(build, m.Build);
        Assert.Equal(family, m.Family);
    }

    [Fact]
    public void Report_Text_Has_Editions_And_Verdict()
    {
        var i = Info();
        var t = IsoInspector.ToText(i, IsoInspector.Evaluate(i));
        Assert.Contains("Windows 11 Pro", t);
        Assert.Contains("x64", t);
        Assert.Contains("dùng được để làm USB cài", t);
        Assert.Contains("Không có gì bị ghi hay xóa", t);
    }

    [Fact]
    public void Report_Text_For_Bad_Iso_Says_Not_Usable()
    {
        var i = Info(efi: false);
        var t = IsoInspector.ToText(i, IsoInspector.Evaluate(i));
        Assert.Contains("KHÔNG DÙNG ĐƯỢC", t);
        Assert.Contains("CHƯA dùng được", t);
    }
}
