using PCTool.Core.AppInstaller;

namespace PCTool.Tests.AppInstaller;

public sealed class AppCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pctool-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "apps.user.json");
    private AppCatalog Catalog() => new(FilePath);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static AppEntry Mine(string id = "7zip", string name = "7-Zip", string wingetId = "7zip.7zip") =>
        new() { Id = id, Name = name, Kind = AppKind.Winget, WingetId = wingetId, SelectedByDefault = false };

    [Fact]
    public void NoFile_ReturnsThreeDefaults_InOrder()
    {
        var s = Catalog().Load();

        Assert.Equal(new[] { "zalo", "chrome", "facebook" }, s.Items.Select(i => i.Id).ToArray());
        Assert.All(s.Items, i => Assert.True(i.BuiltIn));
        Assert.Equal(AppKind.WebShortcut, s.Items.Single(i => i.Id == "facebook").Kind);
        Assert.False(s.ReadOnly);
    }

    [Fact]
    public void Defaults_AreAllValid() =>
        Assert.All(AppCatalog.Defaults, d => Assert.Empty(AppEntryValidator.Validate(d)));

    [Fact]
    public void Add_PersistsAndAppearsAfterDefaults()
    {
        Assert.True(Catalog().AddOrUpdate(Mine()).Ok);

        var items = Catalog().Load().Items;
        Assert.Equal("7zip", items.Last().Id);
        Assert.False(items.Last().BuiltIn);
        Assert.Equal(4, items.Count);
    }

    [Fact]
    public void Add_Invalid_IsRejected_AndNothingWritten()
    {
        var bad = Mine(wingetId: "--source");

        var r = Catalog().AddOrUpdate(bad);

        Assert.False(r.Ok);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void UserEntry_WithSameId_OverridesDefault()
    {
        var over = Mine(id: "chrome", name: "Chrome của tôi", wingetId: "Google.Chrome.Beta");
        Catalog().AddOrUpdate(over);

        var chrome = Catalog().Load().Items.Single(i => i.Id == "chrome");

        Assert.Equal("Chrome của tôi", chrome.Name);
        Assert.Equal(3, Catalog().Load().Items.Count);   // không bị nhân đôi
    }

    [Fact]
    public void Remove_BuiltIn_OnlyHides_AndCanBeRestored()
    {
        Catalog().Remove("zalo");
        Assert.DoesNotContain(Catalog().Load().Items, i => i.Id == "zalo");

        Catalog().RestoreHiddenDefaults();
        Assert.Contains(Catalog().Load().Items, i => i.Id == "zalo");
    }

    [Fact]
    public void Remove_UserEntry_DeletesIt()
    {
        Catalog().AddOrUpdate(Mine());
        Catalog().Remove("7zip");

        Assert.DoesNotContain(Catalog().Load().Items, i => i.Id == "7zip");
    }

    [Fact]
    public void Remove_Override_RevealsDefaultAgain_WhenNotBuiltInHidden()
    {
        Catalog().AddOrUpdate(Mine(id: "chrome", wingetId: "Google.Chrome.Beta"));
        Catalog().RestoreHiddenDefaults();

        // Gỡ bản ghi đè = xóa mục người dùng cùng id và ẩn mặc định -> hành vi "ẩn"; khôi phục thì mặc định hiện lại.
        Catalog().Remove("chrome");
        Catalog().RestoreHiddenDefaults();

        var chrome = Catalog().Load().Items.Single(i => i.Id == "chrome");
        Assert.True(chrome.BuiltIn);
    }

    [Fact]
    public void CorruptFile_IsRenamed_NotDeleted_AndDefaultsUsed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ hỏng");

        var s = Catalog().Load();

        Assert.Equal(3, s.Items.Count);
        Assert.NotEmpty(s.Warnings);
        Assert.True(File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void NewerSchema_IsReadOnly_AndRefusesToWrite()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"schemaVersion":99,"items":[],"hiddenBuiltIn":[]}""");
        var before = File.ReadAllText(FilePath);

        var s = Catalog().Load();
        var r = Catalog().AddOrUpdate(Mine());

        Assert.True(s.ReadOnly);
        Assert.False(r.Ok);
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void InvalidItemsInsideFile_AreSkippedWithWarning()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
        {"schemaVersion":1,"items":[
          {"id":"ok","name":"OK","kind":"winget","wingetId":"A.B"},
          {"id":"bad","name":"Bad","kind":"winget","wingetId":"--evil"}
        ],"hiddenBuiltIn":[]}
        """);

        var s = Catalog().Load();

        Assert.Contains(s.Items, i => i.Id == "ok");
        Assert.DoesNotContain(s.Items, i => i.Id == "bad");
        Assert.Single(s.Warnings);
    }

    [Fact]
    public void Save_KeepsUpToThreeBackups()
    {
        for (int i = 0; i < 6; i++) Catalog().AddOrUpdate(Mine(id: "a" + i, name: "A" + i, wingetId: "A.B" + i));

        Assert.True(File.Exists(FilePath + ".bak1"));
        Assert.True(File.Exists(FilePath + ".bak2"));
        Assert.True(File.Exists(FilePath + ".bak3"));
        Assert.False(File.Exists(FilePath + ".bak4"));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Json_UsesDocumentedNames()
    {
        Catalog().AddOrUpdate(new AppEntry { Id = "fb2", Name = "FB2", Kind = AppKind.WebShortcut, Url = "https://example.com" });

        var text = File.ReadAllText(FilePath);

        Assert.Contains("\"kind\": \"webShortcut\"", text);
        Assert.Contains("\"schemaVersion\": 1", text);
    }

    // ------------------------------------------------------------------ xuất / nhập

    [Fact]
    public void Export_ContainsOnlyUserItems_AndRoundTrips()
    {
        Catalog().AddOrUpdate(Mine());
        var json = Catalog().Export(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));

        var other = new AppCatalog(Path.Combine(_dir, "other", "apps.user.json"));
        var result = other.Import(json, (_, _) => false);

        Assert.Equal(1, result.Added);
        Assert.Contains(other.Load().Items, i => i.Id == "7zip");
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.DoesNotContain("zalo", json);
    }

    [Fact]
    public void Import_SkipsInvalidItems_AndReportsThem()
    {
        var json = """
        {"schemaVersion":1,"items":[
          {"id":"good","name":"Good","kind":"winget","wingetId":"A.B"},
          {"id":"bad1","name":"Bad1","kind":"winget","wingetId":"a b"},
          {"id":"bad2","name":"Bad2","kind":"webShortcut","url":"http://x.com"},
          {"id":"bad3","name":"Bad3","kind":"khongco"}
        ]}
        """;

        var r = Catalog().Import(json, (_, _) => false);

        Assert.Equal(1, r.Added);
        Assert.Equal(3, r.Skipped.Count);
    }

    [Theory]
    [InlineData(true, 1, 0)]
    [InlineData(false, 0, 1)]
    public void Import_Conflict_FollowsDecision(bool replace, int replaced, int kept)
    {
        Catalog().AddOrUpdate(Mine(name: "Cũ"));
        var json = """{"schemaVersion":1,"items":[{"id":"7zip","name":"Mới","kind":"winget","wingetId":"7zip.7zip"}]}""";

        var r = Catalog().Import(json, (_, _) => replace);

        Assert.Equal(replaced, r.Replaced);
        Assert.Equal(kept, r.KeptExisting);
        Assert.Equal(replace ? "Mới" : "Cũ", Catalog().Load().Items.Single(i => i.Id == "7zip").Name);
    }

    [Fact]
    public void Import_CannotSmuggleBuiltInFlag()
    {
        var json = """{"schemaVersion":1,"items":[{"id":"x1","name":"X","kind":"winget","wingetId":"A.B","builtIn":true}]}""";

        Catalog().Import(json, (_, _) => false);

        Assert.False(Catalog().Load().Items.Single(i => i.Id == "x1").BuiltIn);
    }

    [Theory]
    [InlineData("không phải json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"schemaVersion":99,"items":[]}""")]
    public void Import_BadFile_Throws_AndChangesNothing(string json)
    {
        Assert.Throws<InvalidDataException>(() => Catalog().Import(json, (_, _) => false));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Import_TooBig_OrTooManyItems_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Catalog().Import(new string(' ', AppCatalog.MaxImportBytes + 1), (_, _) => false));

        var many = string.Join(",", Enumerable.Range(0, AppCatalog.MaxImportItems + 1)
            .Select(i => $$"""{"id":"a{{i}}","name":"A","kind":"winget","wingetId":"A.B"}"""));
        Assert.Throws<InvalidDataException>(() => Catalog().Import("{\"items\":[" + many + "]}", (_, _) => false));
    }
}
