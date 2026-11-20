using System.IO;
using System.Runtime.InteropServices;
using PCTool.Core.Framework;

namespace PCTool.Core.Optimize;

/// <summary>Một loại rác có thể dọn (thư mục tạm, cache...).</summary>
public sealed class CleanupItem
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string[] Paths { get; init; }
    public bool IsRecycleBin { get; init; }
    public bool Selected { get; set; } = true;
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    public string SizeText => FormatSize(SizeBytes);

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}

public sealed class CleanupResult
{
    public long FreedBytes { get; set; }
    public int DeletedFiles { get; set; }
    public int SkippedFiles { get; set; }
}

public static class CleanupScanner
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Danh sách loại rác an toàn. Chỉ gồm thư mục tạm/cache, không đụng dữ liệu người dùng.</summary>
    public static List<CleanupItem> BuildItems() => new()
    {
        new CleanupItem
        {
            Name = "File tạm của người dùng",
            Description = "%TEMP% của tài khoản hiện tại",
            Paths = new[] { Path.GetTempPath() }
        },
        new CleanupItem
        {
            Name = "File tạm của Windows",
            Description = @"C:\Windows\Temp",
            Paths = new[] { Path.Combine(Win, "Temp") }
        },
        new CleanupItem
        {
            Name = "Cache Windows Update đã tải",
            Description = @"SoftwareDistribution\Download (Windows tự tải lại khi cần)",
            Paths = new[] { Path.Combine(Win, "SoftwareDistribution", "Download") }
        },
        new CleanupItem
        {
            Name = "Báo cáo lỗi / crash dump",
            Description = "WER, Minidump, CrashDumps",
            Paths = new[]
            {
                Path.Combine(Local, "CrashDumps"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(Win, "Minidump")
            }
        },
        new CleanupItem
        {
            Name = "Ảnh thu nhỏ (thumbnail cache)",
            Description = "Explorer tự tạo lại",
            Paths = new[] { Path.Combine(Local, "Microsoft", "Windows", "Explorer") },
            // chỉ xóa các file thumbcache_*.db / iconcache_*.db, xử lý trong Clean()
        },
        new CleanupItem
        {
            Name = "Cache Google Chrome",
            Description = "Không xóa mật khẩu, lịch sử hay cookie",
            Paths = new[]
            {
                Path.Combine(Local, "Google", "Chrome", "User Data", "Default", "Cache"),
                Path.Combine(Local, "Google", "Chrome", "User Data", "Default", "Code Cache"),
                Path.Combine(Local, "Google", "Chrome", "User Data", "Default", "GPUCache")
            }
        },
        new CleanupItem
        {
            Name = "Cache Microsoft Edge",
            Description = "Không xóa mật khẩu, lịch sử hay cookie",
            Paths = new[]
            {
                Path.Combine(Local, "Microsoft", "Edge", "User Data", "Default", "Cache"),
                Path.Combine(Local, "Microsoft", "Edge", "User Data", "Default", "Code Cache"),
                Path.Combine(Local, "Microsoft", "Edge", "User Data", "Default", "GPUCache")
            }
        },
        new CleanupItem
        {
            Name = "Thùng rác",
            Description = "Toàn bộ thùng rác trên mọi ổ (không thể khôi phục sau khi dọn)",
            Paths = Array.Empty<string>(),
            IsRecycleBin = true,
            Selected = false
        }
    };

    public static void Scan(CleanupItem item, CancellationToken ct)
    {
        long size = 0;
        int count = 0;

        if (item.IsRecycleBin)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0)
            {
                size = info.i64Size;
                count = (int)Math.Min(info.i64NumItems, int.MaxValue);
            }
        }
        else
        {
            foreach (var file in EnumerateFiles(item, ct))
            {
                try { size += new FileInfo(file).Length; count++; }
                catch { /* bỏ qua file không đọc được */ }
            }
        }

        item.SizeBytes = size;
        item.FileCount = count;
    }

    /// <param name="dryRun">true = chỉ ghi log, không xóa gì cả.</param>
    public static CleanupResult Clean(CleanupItem item, bool dryRun, CancellationToken ct)
    {
        var result = new CleanupResult();

        if (item.IsRecycleBin)
        {
            Logger.Info($"{(dryRun ? "[CHẠY THỬ] " : "")}Dọn thùng rác ({item.SizeText})");
            if (!dryRun)
            {
                // NOCONFIRMATION | NOPROGRESSUI | NOSOUND
                var hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
                if (hr == 0) { result.FreedBytes += item.SizeBytes; result.DeletedFiles += item.FileCount; }
                else Logger.Warn($"Không dọn được thùng rác (mã {hr:X})");
            }
            else
            {
                result.FreedBytes += item.SizeBytes;
                result.DeletedFiles += item.FileCount;
            }
            return result;
        }

        foreach (var file in EnumerateFiles(item, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long len = new FileInfo(file).Length;
                if (dryRun)
                {
                    Logger.Info($"[CHẠY THỬ] Sẽ xóa: {file}");
                }
                else
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                result.FreedBytes += len;
                result.DeletedFiles++;
            }
            catch
            {
                // File đang được dùng hoặc không đủ quyền: bỏ qua, không làm gián đoạn.
                result.SkippedFiles++;
            }
        }

        if (!dryRun)
        {
            // Dọn thư mục rỗng còn lại (không đụng vào thư mục gốc đã khai báo)
            foreach (var root in item.Paths)
                RemoveEmptyDirs(root, isRoot: true);
        }

        Logger.Info($"{(dryRun ? "[CHẠY THỬ] " : "")}{item.Name}: {result.DeletedFiles} file, {CleanupItem.FormatSize(result.FreedBytes)}, bỏ qua {result.SkippedFiles}");
        return result;
    }

    private static IEnumerable<string> EnumerateFiles(CleanupItem item, CancellationToken ct)
    {
        bool thumbOnly = item.Name.StartsWith("Ảnh thu nhỏ", StringComparison.Ordinal);

        foreach (var root in item.Paths)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = !thumbOnly,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint // không đi theo symlink/junction
            };

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, thumbOnly ? "*cache_*.db" : "*", options);
            }
            catch { continue; }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return f;
            }
        }
    }

    private static void RemoveEmptyDirs(string dir, bool isRoot)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                RemoveEmptyDirs(sub, false);
            }
            if (!isRoot && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* bỏ qua */ }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
