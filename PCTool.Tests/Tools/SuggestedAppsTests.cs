using PCTool.Core.AppInstaller;
using PCTool.Core.Tools;
using Xunit;

namespace PCTool.Tests.Tools;

public class SuggestedAppsTests
{
    [Fact]
    public void Suggested_AllValid_UniqueIds_NoClashWithDefaults()
    {
        foreach (var e in AppCatalog.Suggested) Assert.Empty(AppEntryValidator.Validate(e));
        var ids = AppCatalog.Suggested.Select(e => e.Id).Concat(AppCatalog.Defaults.Select(d => d.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(AppCatalog.Suggested, e => Assert.False(e.SelectedByDefault));
    }

    [Fact]
    public void AddSuggested_AddsOnce_KeepsExisting()
    {
        var path = Path.Combine(Path.GetTempPath(), "pct-" + Guid.NewGuid().ToString("N"), "apps.user.json");
        try
        {
            var c = new AppCatalog(path);
            var r1 = c.AddSuggested();
            Assert.True(r1.Ok);
            Assert.Equal(3 + AppCatalog.Suggested.Count, c.Load().Items.Count);
            var r2 = c.AddSuggested();
            Assert.Contains("đủ", r2.Message);
            Assert.Equal(3 + AppCatalog.Suggested.Count, c.Load().Items.Count);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    private static AppCatalog Temp(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "pct-" + Guid.NewGuid().ToString("N"));
        return new AppCatalog(Path.Combine(dir, "apps.user.json"));
    }

    [Fact]
    public void LoadAll_ShowsEverything_WithoutWritingFile()
    {
        var c = Temp(out var dir);
        try
        {
            var s = c.LoadAll();
            Assert.Equal(3 + AppCatalog.Suggested.Count, s.Items.Count);
            Assert.Equal("zalo", s.Items[0].Id);
            Assert.Equal(s.Items.Count, s.Items.Select(i => i.Id).Distinct().Count());
            Assert.False(Directory.Exists(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void LoadAll_AfterAddSuggested_NoDuplicates()
    {
        var c = Temp(out var dir);
        try
        {
            c.AddSuggested();
            var s = c.LoadAll();
            Assert.Equal(3 + AppCatalog.Suggested.Count, s.Items.Count);
            Assert.Equal(s.Items.Count, s.Items.Select(i => i.Id).Distinct().Count());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void LoadAll_HideSuggested_ThenRestore()
    {
        var c = Temp(out var dir);
        try
        {
            Assert.True(c.Remove("vlc").Ok);
            Assert.DoesNotContain(c.LoadAll().Items, i => i.Id == "vlc");
            c.RestoreHiddenDefaults();
            Assert.Contains(c.LoadAll().Items, i => i.Id == "vlc");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void IntelRst_OnlyWhitelistedHttpsIntel()
    {
        Assert.All(IntelRstPages.All, p => Assert.StartsWith("https://www.intel.com/content/www/us/en/download/", p.Url));
        Assert.False(IntelRstPages.IsAllowed("https://evil.com/x"));
        Assert.False(IntelRstPages.IsAllowed("https://www.intel.com/content/www/us/en/download/930427?x=1"));
        Assert.False(IntelRstPages.IsAllowed(null));
        string? opened = null;
        IntelRstPages.Open(IntelRstPages.All[0].Url, u => opened = u);
        Assert.Equal(IntelRstPages.All[0].Url, opened);
        Assert.Throws<ArgumentException>(() => IntelRstPages.Open("http://www.intel.com/x", _ => { }));
    }
}
