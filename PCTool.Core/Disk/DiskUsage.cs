using System.IO;

namespace PCTool.Core.Disk;

public sealed record FolderSize(string Name, string Path, long Bytes, bool IsFolder);

/// <summary>Chỉ ĐỌC: cộng dung lượng từng thư mục con để biết cái gì chiếm chỗ. Không xóa, không sửa gì.</summary>
public static class DiskUsage
{
    public static string? CheckRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Chưa chọn thư mục.";
        string full;
        try { full = Path.GetFullPath(path); } catch { return "Đường dẫn không hợp lệ."; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "Chưa hỗ trợ đường dẫn mạng.";
        if (!Directory.Exists(full)) return "Thư mục không tồn tại.";
        return null;
    }

    public static long SizeOf(string dir, CancellationToken ct)
    {
        long total = 0;
        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false
        };
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opt))
            {
                ct.ThrowIfCancellationRequested();
                try { total += f.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Dung lượng từng thư mục con trực tiếp (và gộp file nằm thẳng ở gốc), sắp xếp lớn nhất trước.</summary>
    public static IReadOnlyList<FolderSize> Scan(string root, CancellationToken ct, int top = 40)
    {
        var err = CheckRoot(root);
        if (err != null) throw new ArgumentException(err, nameof(root));
        var full = Path.GetFullPath(root);
        var di = new DirectoryInfo(full);
        var list = new List<FolderSize>();

        var opt = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var d in di.EnumerateDirectories("*", opt))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new FolderSize(d.Name, d.FullName, SizeOf(d.FullName, ct), true));
        }
        long files = 0;
        foreach (var f in di.EnumerateFiles("*", opt))
            try { files += f.Length; } catch (IOException) { }
        if (files > 0) list.Add(new FolderSize("(các file nằm ngay trong thư mục này)", full, files, false));

        return list.OrderByDescending(x => x.Bytes).Take(Math.Max(1, top)).ToList();
    }
}
