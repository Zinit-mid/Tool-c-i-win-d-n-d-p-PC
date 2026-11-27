using PCTool.Core.Uninstall;

namespace PCTool.Tests.Uninstall;

public sealed class ProgramDisplayTests
{
    private static InstalledProgram P(string name, string version = "1.0", string uninstall = "\"C:\\Program Files\\X\\unins000.exe\"", bool system = false,
        string parent = "", string publisher = "X Inc", long size = 1000, string key = "k") =>
        new("HKLM", false, key, name, version, publisher, uninstall, "", @"C:\Program Files\X", size, system, parent, false);

    private static bool Exists(string p) => p.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Icon_From_DisplayIcon_With_Quotes_And_Index()
    {
        Assert.Equal(@"C:\Program Files\X\x.exe", ProgramIcon.PathFrom("\"C:\\Program Files\\X\\x.exe\",0", null, Exists));
        Assert.Equal(@"C:\Program Files\X\x.exe", ProgramIcon.PathFrom(@"C:\Program Files\X\x.exe,-3", null, Exists));
        Assert.Equal(@"C:\Program Files\X\x.ico", ProgramIcon.PathFrom(@"C:\Program Files\X\x.ico", null, Exists));
    }

    [Fact]
    public void Icon_Falls_Back_To_Uninstaller_Exe_But_Not_Msiexec()
    {
        Assert.Equal(@"C:\Program Files\X\unins000.exe", ProgramIcon.PathFrom("", "\"C:\\Program Files\\X\\unins000.exe\" /S", Exists));
        Assert.Null(ProgramIcon.PathFrom("", "MsiExec.exe /X{11111111-1111-1111-1111-111111111111}", Exists));
        Assert.Null(ProgramIcon.PathFrom("", "", Exists));
    }

    [Fact]
    public void Icon_Rejects_Bad_Paths()
    {
        Assert.Null(ProgramIcon.PathFrom(@"\\server\share\x.exe", null, _ => true));
        Assert.Null(ProgramIcon.PathFrom(@"x.exe", null, _ => true));                      // không đầy đủ
        Assert.Null(ProgramIcon.PathFrom(@"C:\a\..\b\x.exe", null, _ => true));            // có ..
        Assert.Null(ProgramIcon.PathFrom(@"C:\a\x.bat", null, _ => true));                 // đuôi lạ
        Assert.Null(ProgramIcon.PathFrom(@"C:\Program Files\X\missing.exe", null, _ => false));
        Assert.Null(ProgramIcon.PathFrom("\"C:\\a\\x.exe", null, _ => true));              // dấu nháy sai
    }

    [Fact]
    public void Dates_Parse_And_Format()
    {
        Assert.Equal(new DateTime(2026, 11, 15), ProgramDates.Parse("20261115"));
        Assert.Equal("15/11/2026", ProgramDates.Text("20261115"));
        Assert.Null(ProgramDates.Parse("15/11/2026"));
        Assert.Null(ProgramDates.Parse(null));
        Assert.Equal("", ProgramDates.Text("abc"));
    }

    [Fact]
    public void Dedupe_Keeps_Removable_Copy_Of_Same_Name_And_Version()
    {
        var good = P("GameThuanViet", "3.3.2", size: 199000, key: "a");
        var dup = P("GameThuanViet", "3.3.2", system: true, size: 198900, key: "b");
        var list = ProgramList.Dedupe(new[] { dup, good });
        Assert.Single(list);
        Assert.Equal("a", list[0].Key);
        // đảo thứ tự vẫn giữ bản gỡ được
        Assert.Equal("a", ProgramList.Dedupe(new[] { good, dup })[0].Key);
    }

    [Fact]
    public void Dedupe_Keeps_Different_Versions_And_Names_Sorted()
    {
        var list = ProgramList.Dedupe(new[] { P("b app", "1"), P("A app", "1"), P("b app", "2", key: "z") });
        Assert.Equal(new[] { "A app", "b app", "b app" }, list.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Visible_Hides_System_Microsoft_Updates_And_NoUninstaller_Unless_ShowAll()
    {
        var all = new[]
        {
            P("Epic Games Launcher", key: "1"),
            P("Microsoft Visual C++ 2015-2022 Redistributable (x64)", publisher: "Microsoft Corporation", key: "2"),
            P("Microsoft .NET Runtime - 8.0.21 (x64)", publisher: "Microsoft Corporation", key: "3"),
            P("Hidden helper", system: true, key: "4"),
            P("Update for Windows (KB5001234)", key: "5"),
            P("No uninstaller app", uninstall: "", key: "6"),
        };
        Assert.Equal(new[] { "Epic Games Launcher" }, ProgramList.Visible(all, false).Select(p => p.Name).ToArray());
        Assert.Equal(6, ProgramList.Visible(all, true).Count);
    }

    [Fact]
    public void Scanner_Parses_Icon_And_Date()
    {
        var json = """{"hive":"HKLM","wow":false,"key":"k","name":"N","version":"1","publisher":"P","uninstall":"u","quiet":"","location":"","sizeKb":0,"system":false,"parent":"","msi":false,"icon":" C:\\x.exe,0 ","date":"20261115"}""";
        var p = InstalledProgramsScanner.Parse(json).Single();
        Assert.Equal(@"C:\x.exe,0", p.DisplayIcon);
        Assert.Equal("20261115", p.InstallDate);
    }
}
