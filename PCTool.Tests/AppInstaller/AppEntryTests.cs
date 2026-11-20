using PCTool.Core.AppInstaller;

namespace PCTool.Tests.AppInstaller;

public sealed class AppEntryTests
{
    private static AppEntry Winget(string id = "x", string wingetId = "Vendor.App") =>
        new() { Id = id, Name = "App", Kind = AppKind.Winget, WingetId = wingetId };

    [Theory]
    [InlineData("VNGCorp.Zalo")]
    [InlineData("Google.Chrome")]
    [InlineData("7zip.7zip")]
    [InlineData("Microsoft.VisualStudioCode_1-x")]
    public void WingetId_Valid(string id) => Assert.Empty(AppEntryValidator.Validate(Winget(wingetId: id)));

    [Theory]
    [InlineData("")]
    [InlineData("--source")]
    [InlineData("-h")]
    [InlineData("a b")]
    [InlineData("a;calc")]
    [InlineData("a&b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData(".hidden")]
    [InlineData("Ứng.Dụng")]
    public void WingetId_Invalid(string id) => Assert.NotEmpty(AppEntryValidator.Validate(Winget(wingetId: id)));

    [Fact]
    public void WingetId_TooLong_Invalid() =>
        Assert.NotEmpty(AppEntryValidator.Validate(Winget(wingetId: "a" + new string('b', 128))));

    [Theory]
    [InlineData("https://www.facebook.com")]
    [InlineData("https://example.com/download?x=1")]
    public void Url_Valid(string url) => Assert.Null(AppEntryValidator.ValidateUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://exa mple.com")]
    [InlineData("https://example.com\r\nURL=http://evil")]
    [InlineData("example.com")]
    public void Url_Invalid(string? url) => Assert.NotNull(AppEntryValidator.ValidateUrl(url));

    [Fact]
    public void Url_TooLong_Invalid() =>
        Assert.NotNull(AppEntryValidator.ValidateUrl("https://example.com/" + new string('a', 2100)));

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("có dấu")]
    [InlineData("-bat-dau-gach")]
    public void Id_Invalid(string id) => Assert.NotEmpty(AppEntryValidator.Validate(Winget(id: id)));

    [Fact]
    public void Name_Empty_OrTooLong_OrControlChars_Invalid()
    {
        var e = Winget(); e.Name = "  ";
        Assert.NotEmpty(AppEntryValidator.Validate(e));
        e.Name = new string('a', 81);
        Assert.NotEmpty(AppEntryValidator.Validate(e));
        e.Name = "a\nb";
        Assert.NotEmpty(AppEntryValidator.Validate(e));
    }

    [Fact]
    public void WebShortcut_RequiresHttpsUrl_NotWingetId()
    {
        var ok = new AppEntry { Id = "fb", Name = "FB", Kind = AppKind.WebShortcut, Url = "https://www.facebook.com" };
        var bad = new AppEntry { Id = "fb", Name = "FB", Kind = AppKind.WebShortcut, Url = "http://www.facebook.com" };

        Assert.Empty(AppEntryValidator.Validate(ok));
        Assert.NotEmpty(AppEntryValidator.Validate(bad));
    }

    [Fact]
    public void UnknownKind_Invalid()
    {
        var e = Winget(); e.Kind = (AppKind)99;
        Assert.NotEmpty(AppEntryValidator.Validate(e));
    }

    [Theory]
    [InlineData("Phần mềm Đồ Họa", "phan-mem-do-hoa")]
    [InlineData("  Zalo!!  ", "zalo")]
    [InlineData("Google Chrome", "google-chrome")]
    [InlineData("!!!", "app")]
    [InlineData("", "app")]
    public void MakeId_RemovesVietnameseMarks(string name, string expected) =>
        Assert.Equal(expected, AppEntryValidator.MakeId(name, Array.Empty<string>()));

    [Fact]
    public void MakeId_AvoidsDuplicates_AndStaysValid()
    {
        var id = AppEntryValidator.MakeId("Zalo", new[] { "zalo", "zalo-2" });

        Assert.Equal("zalo-3", id);
        Assert.Empty(AppEntryValidator.Validate(Winget(id: AppEntryValidator.MakeId(new string('a', 100), Array.Empty<string>()))));
    }
}
