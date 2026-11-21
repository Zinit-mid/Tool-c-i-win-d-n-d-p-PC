using PCTool.Core.Framework;
using PCTool.Tests.Framework;

namespace PCTool.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pctool-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void NoFile_UsesSafeDefaults()
    {
        var s = new SettingsService(FilePath);

        Assert.True(s.DryRunDefault);
        Assert.False(s.SafeMode);
        Assert.Equal(30, s.DefenderOffMinutes);
        Assert.False(s.ReadOnly);
        Assert.False(File.Exists(FilePath));   // đọc không tự tạo file
    }

    [Fact]
    public void SafeMode_IsPersisted_AcrossInstances()
    {
        Assert.True(new SettingsService(FilePath).SetSafeMode(true));

        Assert.True(new SettingsService(FilePath).SafeMode);

        new SettingsService(FilePath).SetSafeMode(false);
        Assert.False(new SettingsService(FilePath).SafeMode);
    }

    [Fact]
    public void Json_UsesDocumentedNames()
    {
        new SettingsService(FilePath).SetSafeMode(true);

        var text = File.ReadAllText(FilePath);

        Assert.Contains("\"safeMode\": true", text);
        Assert.Contains("\"dryRunDefault\": true", text);
        Assert.Contains("\"confirmBeforeFix\": true", text);
        Assert.Contains("\"schemaVersion\": 1", text);
    }

    [Fact]
    public void HandEditedBadValues_AreNormalized()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
        {"schemaVersion":1,"defenderOffMinutes":9999,"logRetentionDays":1,"confirmBeforeFix":false,"safeMode":true}
        """);

        var s = new SettingsService(FilePath);

        Assert.Equal(30, s.DefenderOffMinutes);
        Assert.True(s.SafeMode);
        s.SetDryRunDefault(false);
        var saved = File.ReadAllText(FilePath);
        Assert.Contains("\"logRetentionDays\": 30", saved);
        Assert.Contains("\"confirmBeforeFix\": true", saved);   // không cho tắt xác nhận
    }

    [Fact]
    public void CorruptFile_IsRenamed_NotDeleted_AndDefaultsUsed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ hỏng");

        var s = new SettingsService(FilePath);

        Assert.True(s.DryRunDefault);
        Assert.NotNull(s.Warning);
        Assert.True(File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void NewerSchema_IsReadOnly_NotOverwritten_ButStillAppliesInMemory()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"schemaVersion":99,"safeMode":false}""");
        var before = File.ReadAllText(FilePath);
        var s = new SettingsService(FilePath);

        var saved = s.SetSafeMode(true);

        Assert.True(s.ReadOnly);
        Assert.False(saved);
        Assert.True(s.SafeMode);                       // phiên này vẫn an toàn
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Changed_IsRaised()
    {
        var s = new SettingsService(FilePath);
        int n = 0;
        s.Changed += () => n++;

        s.SetSafeMode(true);

        Assert.Equal(1, n);
    }

    [Fact]
    public void Save_KeepsUpToThreeBackups_NoTempLeft()
    {
        var s = new SettingsService(FilePath);
        for (int i = 0; i < 6; i++) s.SetSafeMode(i % 2 == 0);

        Assert.True(File.Exists(FilePath + ".bak3"));
        Assert.False(File.Exists(FilePath + ".bak4"));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void BuildOptions_CarriesSafeModeAndDryRun()
    {
        var s = new SettingsService(FilePath);

        var off = s.BuildOptions(dryRun: false);
        s.SetSafeMode(true);
        var on = s.BuildOptions(dryRun: true);

        Assert.False(off.SafeMode);
        Assert.False(off.DryRun);
        Assert.True(on.SafeMode);
        Assert.True(on.DryRun);
        Assert.False(on.CreateRestorePoint);
    }

    [Fact]
    public async Task SafeModeOn_ThroughBuildOptions_BlocksARealRun()
    {
        var s = new SettingsService(FilePath);
        s.SetSafeMode(true);
        var fix = new FakeFix();

        var result = await new FixExecutor(new FakeRestorePoints())
            .RunAsync(fix, s.BuildOptions(dryRun: false), null, CancellationToken.None);

        Assert.Equal(FixOutcome.Blocked, result.Outcome);
        Assert.Equal(0, fix.ExecuteCalls);
    }
}
