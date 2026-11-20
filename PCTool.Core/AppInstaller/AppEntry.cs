using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PCTool.Core.AppInstaller;

public enum AppKind
{
    /// <summary>Cài im lặng bằng winget theo ID.</summary>
    Winget,

    /// <summary>Tạo lối tắt (.url) ra Desktop để mở trang web bằng trình duyệt.</summary>
    WebShortcut,

    /// <summary>Mở trang tải bằng trình duyệt, người dùng tự tải và cài.</summary>
    DownloadPage
}

/// <summary>Một mục trong danh sách cài nhanh (xem DU-LIEU-PCTool.md mục 2.2).</summary>
public sealed class AppEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AppKind Kind { get; set; }
    public string? WingetId { get; set; }
    public string? Url { get; set; }
    public string? Group { get; set; }
    public bool SelectedByDefault { get; set; }
    public bool BuiltIn { get; set; }

    public AppEntry Clone() => (AppEntry)MemberwiseClone();
}

/// <summary>
/// Kiểm tra dữ liệu người dùng nhập. Quan trọng vì app chạy quyền Admin:
/// ID winget và link chỉ nhận định dạng chặt, người dùng KHÔNG được nhập tham số cài đặt tùy ý.
/// </summary>
public static class AppEntryValidator
{
    // Phải bắt đầu bằng chữ/số để không thể thành tham số kiểu "--source" của winget.
    private static readonly Regex WingetIdRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex IdRx = new(@"^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate(AppEntry e)
    {
        var errors = new List<string>();

        if (!IdRx.IsMatch(e.Id ?? "")) errors.Add("Mã mục không hợp lệ (chỉ chữ thường, số, gạch ngang, tối đa 40 ký tự).");

        var name = (e.Name ?? "").Trim();
        if (name.Length is < 1 or > 80) errors.Add("Tên phải từ 1 đến 80 ký tự.");
        else if (HasControlChars(name)) errors.Add("Tên chứa ký tự không hợp lệ.");

        if (e.Group is { } g && (g.Length > 40 || HasControlChars(g))) errors.Add("Nhóm tối đa 40 ký tự và không chứa ký tự lạ.");

        if (!Enum.IsDefined(typeof(AppKind), e.Kind))
        {
            errors.Add("Kiểu cài đặt không hợp lệ.");
            return errors;
        }

        if (e.Kind == AppKind.Winget)
        {
            if (string.IsNullOrEmpty(e.WingetId)) errors.Add("Cần nhập ID winget.");
            else if (!WingetIdRx.IsMatch(e.WingetId))
                errors.Add("ID winget chỉ gồm chữ, số, dấu chấm, gạch ngang, gạch dưới (tối đa 128 ký tự, bắt đầu bằng chữ hoặc số).");
        }
        else
        {
            var urlError = ValidateUrl(e.Url);
            if (urlError != null) errors.Add(urlError);
        }

        return errors;
    }

    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Cần nhập link.";
        if (url.Length > 2048) return "Link quá dài.";
        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "Link không được chứa khoảng trắng hay ký tự điều khiển.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Link phải bắt đầu bằng https://";
        if (string.IsNullOrEmpty(uri.Host)) return "Link thiếu tên miền.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Link không được chứa tên đăng nhập/mật khẩu.";
        return null;
    }

    /// <summary>Tạo mã mục từ tên (bỏ dấu tiếng Việt), đảm bảo không trùng với <paramref name="existing"/>.</summary>
    public static string MakeId(string name, IEnumerable<string> existing)
    {
        var decomposed = (name ?? "").Trim().Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(lower);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var baseId = sb.ToString().Trim('-');
        if (baseId.Length == 0) baseId = "app";
        if (baseId.Length > 34) baseId = baseId[..34].Trim('-');

        var used = new HashSet<string>(existing, StringComparer.Ordinal);
        var id = baseId;
        for (int i = 2; used.Contains(id); i++) id = $"{baseId}-{i}";
        return id;
    }

    private static bool HasControlChars(string s) => s.Any(char.IsControl);
}
