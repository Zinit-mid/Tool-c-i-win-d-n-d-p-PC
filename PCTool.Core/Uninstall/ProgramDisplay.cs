using System.Globalization;

namespace PCTool.Core.Uninstall;

/// <summary>Tìm file chứa biểu tượng (icon) của chương trình, giống cách Programs and Features của Windows lấy icon.</summary>
public static class ProgramIcon
{
    private static readonly string[] Allowed = { ".exe", ".ico", ".dll" };

    /// <summary>
    /// Lấy đường dẫn file icon từ DisplayIcon (dạng <c>"C:\a\b.exe",0</c> hoặc <c>C:\a\b.ico</c>); không có thì thử file .exe của trình gỡ.
    /// Chỉ trả đường dẫn đầy đủ, không phải đường dẫn mạng, đuôi .exe/.ico/.dll. Không tìm được thì trả null.
    /// </summary>
    public static string? PathFrom(string? displayIcon, string? uninstallString, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var p = Clean(displayIcon);
        if (p != null && fileExists(p)) return p;

        var u = (uninstallString ?? "").Trim();
        if (u.Length == 0 || u.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) return null;
        string exe;
        if (u[0] == '"')
        {
            int close = u.IndexOf('"', 1);
            if (close < 2) return null;
            exe = u[1..close];
        }
        else
        {
            int i = u.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            exe = u[..(i + 4)];
        }

        var c = Clean(exe);
        return c != null && fileExists(c) ? c : null;
    }

    private static string? Clean(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0 || s.Length > 520) return null;
        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            if (close < 2) return null;
            s = s[1..close];
        }
        else
        {
            int comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) s = s[..comma];
        }

        s = Environment.ExpandEnvironmentVariables(s.Trim());
        if (s.Any(c => c < 0x20) || s.Contains("..")) return null;
        if (!Path.IsPathFullyQualified(s) || s.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        return Allowed.Contains(Path.GetExtension(s), StringComparer.OrdinalIgnoreCase) ? s : null;
    }
}

public static class ProgramDates
{
    /// <summary>InstallDate trong Registry có dạng yyyyMMdd. Trả null nếu không hiểu.</summary>
    public static DateTime? Parse(string? s) =>
        DateTime.TryParseExact((s ?? "").Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Text(string? s) => Parse(s)?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "";
}

/// <summary>Chọn các chương trình sẽ hiện trong danh sách: bỏ trùng, và (mặc định) chỉ giữ cái gỡ được, như Programs and Features.</summary>
public static class ProgramList
{
    /// <summary>Hai mục cùng tên + cùng phiên bản coi là một; giữ mục gỡ được (rồi mục dung lượng lớn hơn).</summary>
    public static IReadOnlyList<InstalledProgram> Dedupe(IEnumerable<InstalledProgram> list) =>
        list.GroupBy(p => (p.Name.Trim().ToLowerInvariant(), p.Version.Trim()))
            .Select(g => g.OrderByDescending(p => UninstallPolicy.Evaluate(p).Allowed).ThenByDescending(p => p.SizeKb).First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static IReadOnlyList<InstalledProgram> Visible(IEnumerable<InstalledProgram> list, bool showAll)
    {
        var d = Dedupe(list);
        return showAll ? d : d.Where(p => UninstallPolicy.Evaluate(p).Allowed).ToList();
    }
}
