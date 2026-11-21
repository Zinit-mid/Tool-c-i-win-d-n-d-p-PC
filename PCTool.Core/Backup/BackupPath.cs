using System.Globalization;
using System.Text.RegularExpressions;

namespace PCTool.Core.Backup;

/// <summary>
/// Kiểm tra và ghép đường dẫn thư mục sao lưu. Chỉ nhận đường dẫn kiểu "D:\Thư mục\Con" với ký tự an toàn
/// (chữ, số, khoảng trắng, _ . ( ) -), nên có thể đặt trong dấu nháy đơn của PowerShell mà không lo chèn lệnh.
/// </summary>
public static class BackupPath
{
    private static readonly Regex Safe = new(@"^[A-Za-z]:\\(?:[\p{L}\p{N} _.()\-]+(?:\\[\p{L}\p{N} _.()\-]+)*\\?)?$", RegexOptions.Compiled);
    private static readonly string[] BlockedFirst = { "windows", "program files", "program files (x86)", "programdata" };

    public static bool IsValid(string? path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "Chưa chọn thư mục lưu."; return false; }
        if (path.Length > 180) { error = "Đường dẫn quá dài (tối đa 180 ký tự)."; return false; }
        if (!Safe.IsMatch(path))
        {
            error = "Đường dẫn chỉ được có chữ, số, khoảng trắng và các ký tự _ . ( ) - , ví dụ D:\\SaoLuu. Hãy chọn thư mục khác.";
            return false;
        }

        var segs = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segs)
        {
            if (s == "." || s == ".." || s.EndsWith('.') || s.EndsWith(' ') || s.StartsWith(' '))
            {
                error = "Đường dẫn có thư mục không hợp lệ.";
                return false;
            }
        }
        if (segs.Length > 0 && BlockedFirst.Contains(segs[0].ToLowerInvariant()))
        {
            error = "Không lưu vào thư mục hệ thống của Windows. Hãy chọn thư mục khác (ví dụ trên ổ D hoặc USB).";
            return false;
        }
        return true;
    }

    /// <summary>Ghép "gốc\yyyy-MM-dd_HHmm\tên-con". Ném lỗi nếu gốc không hợp lệ.</summary>
    public static string Combine(string root, DateTime now, string sub)
    {
        if (!IsValid(root, out var err)) throw new ArgumentException(err, nameof(root));
        if (sub.Length == 0 || sub.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Tên thư mục con không hợp lệ.", nameof(sub));
        var stamp = now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        return root.TrimEnd('\\') + "\\" + stamp + "\\" + sub;
    }
}
