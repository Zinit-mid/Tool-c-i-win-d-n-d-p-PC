using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PCTool.Core.Framework;

namespace PCTool.Core.Folders;

/// <summary>
/// Kiểm tra thư mục người dùng chọn để dọn: phải là thư mục có thật, không phải gốc ổ đĩa, không thuộc thư mục hệ thống
/// (Windows, Program Files, ProgramData), không phải thư mục người dùng gốc, không nằm trong AppData, không phải đường dẫn mạng
/// và không phải liên kết (junction/symlink).
/// </summary>
public static class FolderGuard
{
    public static bool Check(string? path, out string error) => Check(path, SystemRoots(), out error);

    public static bool Check(string? path, IReadOnlyList<string> blockedRoots, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "Chưa chọn thư mục."; return false; }
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        { error = "Chỉ nhận đường dẫn kiểu D:\\Thư mục trên máy này (không nhận đường dẫn mạng)."; return false; }

        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception) { error = "Đường dẫn không hợp lệ."; return false; }

        if (full.Length <= 2) { error = "Không dọn cả ổ đĩa. Hãy chọn một thư mục cụ thể, ví dụ Downloads."; return false; }
        if (full.Split('\\').Any(s => s.Equals("AppData", StringComparison.OrdinalIgnoreCase)))
        { error = "Không dọn trong AppData (dữ liệu của chương trình)."; return false; }

        foreach (var b in blockedRoots)
        {
            if (string.IsNullOrWhiteSpace(b)) continue;
            var bb = b.TrimEnd('\\');
            if (full.Equals(bb, StringComparison.OrdinalIgnoreCase) || full.StartsWith(bb + "\\", StringComparison.OrdinalIgnoreCase))
            { error = "Không dọn trong thư mục hệ thống của Windows/chương trình."; return false; }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (profile.Length > 0 && full.Equals(profile, StringComparison.OrdinalIgnoreCase))
        { error = "Đừng chọn cả thư mục người dùng. Hãy chọn Downloads, Desktop hoặc một thư mục con."; return false; }

        if (!Directory.Exists(full)) { error = "Thư mục không tồn tại."; return false; }
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        { error = "Thư mục này là liên kết (junction/symlink), không dọn để tránh đụng nhầm nơi khác."; return false; }
        return true;
    }

    private static IReadOnlyList<string> SystemRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    };

    /// <summary>true nếu file nằm bên trong thư mục gốc (so theo ranh giới thư mục).</summary>
    public static bool IsInside(string root, string file) =>
        file.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

public sealed record FileEntry(string Path, long Length, DateTime Created, DateTime Modified);

public sealed record DuplicateGroup(string Hash, long Length, IReadOnlyList<FileEntry> Files)
{
    /// <summary>Bản được giữ lại: bản cũ nhất (tạo sớm nhất, rồi đường dẫn ngắn nhất).</summary>
    public FileEntry Keep => Files[0];
    public IEnumerable<FileEntry> Extras => Files.Skip(1);
    public long WastedBytes => Length * (Files.Count - 1);
}

public static class FolderNames
{
    /// <summary>Tiền tố thư mục/file do PCTool tạo; bị bỏ qua khi quét để không tự quét lại chính mình.</summary>
    public const string Prefix = "_PCTool_";
}

/// <summary>Tìm file trùng nội dung (cùng dung lượng, rồi so SHA256). Chỉ ĐỌC.</summary>
public static class DuplicateScanner
{
    public const long MinBytes = 1024;      // bỏ qua file nhỏ hơn 1 KB cho đỡ nhiễu
    public const int MaxFiles = 100_000;

    public static IReadOnlyList<DuplicateGroup> Scan(string root, CancellationToken ct, Func<string, string>? hash = null)
    {
        hash ??= Sha256;
        var all = new List<FileEntry>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = new DirectoryInfo(stack.Pop());
            try
            {
                foreach (var d in dir.EnumerateDirectories())
                {
                    if (d.Name.StartsWith(FolderNames.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    stack.Push(d.FullName);
                }
                foreach (var f in dir.EnumerateFiles())
                {
                    if ((f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (f.Length < MinBytes) continue;
                    all.Add(new FileEntry(f.FullName, f.Length, f.CreationTimeUtc, f.LastWriteTimeUtc));
                    if (all.Count >= MaxFiles) goto done;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* bỏ qua thư mục không đọc được */ }
        }
        done:

        var groups = new List<DuplicateGroup>();
        foreach (var bySize in all.GroupBy(f => f.Length).Where(g => g.Count() > 1))
        {
            var hashed = new List<(string h, FileEntry f)>();
            foreach (var f in bySize)
            {
                ct.ThrowIfCancellationRequested();
                try { hashed.Add((hash(f.Path), f)); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* file đang mở/khóa: bỏ qua */ }
            }
            foreach (var g in hashed.GroupBy(x => x.h).Where(g => g.Count() > 1))
            {
                var files = g.Select(x => x.f)
                    .OrderBy(f => f.Created).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                groups.Add(new DuplicateGroup(g.Key, bySize.Key, files));
            }
        }
        return groups.OrderByDescending(g => g.WastedBytes).ThenBy(g => g.Hash, StringComparer.Ordinal).ToList();
    }

    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}

public static class SizeText
{
    public static string Of(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B"
    };
}

/// <summary>
/// Chuyển các bản TRÙNG (trừ 1 bản giữ lại) sang thư mục _PCTool_Trung_&lt;ngày giờ&gt; ngay trong thư mục đã chọn. KHÔNG XÓA gì.
/// Ghi vi-tri-cu.csv để bạn kéo lại nếu cần. File đã đổi/mất kể từ lúc quét, hoặc bản giữ lại đã mất, thì bỏ qua.
/// </summary>
public sealed class DuplicateMoveFix : IFix
{
    public const int MaxFilesPerRun = 5000;

    private readonly string _root;
    private readonly IReadOnlyList<DuplicateGroup> _groups;
    private readonly string _folder;
    private int _moved;
    private readonly List<string> _movedTo = new();
    private readonly List<string> _keepers = new();

    public DuplicateMoveFix(string root, IReadOnlyList<DuplicateGroup> groups, DateTime? now = null)
    {
        if (!FolderGuard.Check(root, out var err)) throw new ArgumentException(err, nameof(root));
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0) throw new ArgumentException("Không có file trùng để chuyển.", nameof(groups));
        if (groups.Sum(g => g.Files.Count - 1) > MaxFilesPerRun)
            throw new ArgumentException($"Quá nhiều file trùng trong một lần (tối đa {MaxFilesPerRun}). Hãy chọn thư mục nhỏ hơn.", nameof(groups));
        foreach (var g in groups)
        {
            if (g.Files.Count < 2) throw new ArgumentException("Nhóm trùng phải có ít nhất 2 file.", nameof(groups));
            if (g.Files.Any(f => !FolderGuard.IsInside(root, f.Path)))
                throw new ArgumentException("Có file nằm ngoài thư mục đã chọn.", nameof(groups));
        }

        _root = Path.GetFullPath(root).TrimEnd('\\');
        _groups = groups;
        _folder = _root + "\\" + FolderNames.Prefix + "Trung_" + (now ?? DateTime.Now).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
    }

    public string QuarantineFolder => _folder;
    public string Title => $"Chuyển các file trùng trong {_root}";

    public IReadOnlyList<string> Actions
    {
        get
        {
            int extras = _groups.Sum(g => g.Files.Count - 1);
            long bytes = _groups.Sum(g => g.WastedBytes);
            return new[]
            {
                $"Giữ lại 1 bản (bản cũ nhất) của mỗi nhóm trùng: {_groups.Count} nhóm",
                $"Chuyển {extras} file trùng ({SizeText.Of(bytes)}) sang thư mục {_folder}",
                "KHÔNG xóa file nào; ghi vi-tri-cu.csv để bạn kéo file về chỗ cũ nếu cần",
                "File đã bị sửa hoặc mất kể từ lúc quét sẽ được bỏ qua"
            };
        }
    }

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;   // kéo lại bằng tay theo vi-tri-cu.csv

    public Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct) => Task.Run(() =>
    {
        Directory.CreateDirectory(_folder);
        var csv = new StringBuilder("vi tri cu,vi tri moi\r\n");
        int skipped = 0, i = 0;
        foreach (var g in _groups)
        {
            ct.ThrowIfCancellationRequested();
            if (!Unchanged(g.Keep)) { skipped += g.Files.Count - 1; continue; }   // bản giữ lại đã mất/đổi: không đụng bản nào
            foreach (var f in g.Extras)
            {
                ct.ThrowIfCancellationRequested();
                if (!Unchanged(f)) { skipped++; continue; }
                var dest = Path.Combine(_folder, $"{++i:0000}_{Path.GetFileName(f.Path)}");
                File.Move(f.Path, dest);
                _moved++;
                _movedTo.Add(dest);
                csv.Append('"').Append(f.Path).Append("\",\"").Append(dest).Append("\"\r\n");
            }
            _keepers.Add(g.Keep.Path);
            progress.Report($"Đã chuyển {_moved} file trùng...");
        }
        File.WriteAllText(Path.Combine(_folder, "vi-tri-cu.csv"), csv.ToString(), new UTF8Encoding(true));
        return _moved == 0
            ? FixResult.Failed("Không chuyển được file nào (file đã đổi hoặc mất kể từ lúc quét). Hãy quét lại.")
            : FixResult.Succeeded($"Đã chuyển {_moved} file trùng sang {_folder}" + (skipped > 0 ? $", bỏ qua {skipped} file đã thay đổi" : "") + ".");
    }, ct);

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.Run(() =>
        _moved > 0
        && _movedTo.All(File.Exists)
        && _keepers.All(File.Exists)
        && File.Exists(Path.Combine(_folder, "vi-tri-cu.csv")), ct);

    private static bool Unchanged(FileEntry f)
    {
        var fi = new FileInfo(f.Path);
        return fi.Exists && fi.Length == f.Length && fi.LastWriteTimeUtc == f.Modified;
    }
}

public enum FileCategory { Images, Documents, Videos, Music, Archives, Installers, Other }

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) d[e] = c; }
        Add(FileCategory.Images, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff");
        Add(FileCategory.Documents, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".csv");
        Add(FileCategory.Videos, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm");
        Add(FileCategory.Music, ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma");
        Add(FileCategory.Archives, ".zip", ".rar", ".7z", ".tar", ".gz");
        Add(FileCategory.Installers, ".exe", ".msi", ".msix", ".iso");
        return d;
    }

    public static FileCategory Of(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out var c) ? c : FileCategory.Other;

    public static string FolderName(FileCategory c) => c switch
    {
        FileCategory.Images => "Ảnh",
        FileCategory.Documents => "Tài liệu",
        FileCategory.Videos => "Video",
        FileCategory.Music => "Nhạc",
        FileCategory.Archives => "File nén",
        FileCategory.Installers => "Bộ cài",
        _ => ""
    };
}

public sealed record MoveItem(string Source, string DestFolder, long Length, DateTime Modified)
{
    public string FileName => Path.GetFileName(Source);
}

/// <summary>Lập kế hoạch sắp xếp file nằm TRỰC TIẾP trong thư mục (không đụng thư mục con, lối tắt, file ẩn/hệ thống, file đang tải dở).</summary>
public static class OrganizePlanner
{
    public static readonly TimeSpan RecentGuard = TimeSpan.FromMinutes(10);
    private static readonly string[] Skip = { ".lnk", ".url", ".ini", ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

    public static IReadOnlyList<MoveItem> Plan(string root, DateTime now)
    {
        var list = new List<MoveItem>();
        var dir = new DirectoryInfo(root);
        foreach (var f in dir.EnumerateFiles())
        {
            if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
            if (f.Name.StartsWith(FolderNames.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (Skip.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) continue;
            if (now - f.LastWriteTime < RecentGuard) continue;    // có thể đang tải/đang sửa
            var cat = FileCategories.Of(f.Name);
            if (cat == FileCategory.Other) continue;
            list.Add(new MoveItem(f.FullName, Path.Combine(root.TrimEnd('\\'), FileCategories.FolderName(cat)), f.Length, f.LastWriteTimeUtc));
        }
        return list.OrderBy(m => m.Source, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Chuyển file vào thư mục theo loại (Ảnh, Tài liệu, Video, ...). Không ghi đè, không xóa; ghi vi-tri-cu.csv.</summary>
public sealed class OrganizeFix : IFix
{
    public const int MaxFilesPerRun = 5000;

    private readonly string _root;
    private readonly IReadOnlyList<MoveItem> _plan;
    private readonly string _csvPath;
    private readonly List<string> _movedTo = new();
    private readonly List<string> _movedFrom = new();

    public OrganizeFix(string root, IReadOnlyList<MoveItem> plan, DateTime? now = null)
    {
        if (!FolderGuard.Check(root, out var err)) throw new ArgumentException(err, nameof(root));
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count == 0) throw new ArgumentException("Không có file nào cần sắp xếp.", nameof(plan));
        if (plan.Count > MaxFilesPerRun) throw new ArgumentException($"Quá nhiều file trong một lần (tối đa {MaxFilesPerRun}).", nameof(plan));
        _root = Path.GetFullPath(root).TrimEnd('\\');
        foreach (var m in plan)
        {
            if (!FolderGuard.IsInside(_root, m.Source) || !string.Equals(Path.GetDirectoryName(m.Source), _root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Chỉ sắp xếp file nằm trực tiếp trong thư mục đã chọn.", nameof(plan));
            if (!string.Equals(Path.GetDirectoryName(m.DestFolder), _root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Thư mục đích không hợp lệ.", nameof(plan));
        }
        _plan = plan;
        _csvPath = _root + "\\" + FolderNames.Prefix + "SapXep_" + (now ?? DateTime.Now).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".csv";
    }

    public string CsvPath => _csvPath;
    public string Title => $"Sắp xếp file theo loại trong {_root}";

    public IReadOnlyList<string> Actions
    {
        get
        {
            var byFolder = _plan.GroupBy(m => Path.GetFileName(m.DestFolder)).OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} file vào thư mục \"{g.Key}\" ({SizeText.Of(g.Sum(m => m.Length))})");
            return byFolder.Concat(new[]
            {
                "Chỉ chuyển file nằm trực tiếp trong thư mục này; không đụng thư mục con, lối tắt, file ẩn, file đang tải dở, file mới sửa trong 10 phút",
                "KHÔNG xóa, KHÔNG ghi đè (trùng tên thì thêm số); ghi " + Path.GetFileName(_csvPath) + " để kéo về chỗ cũ nếu cần"
            }).ToList();
        }
    }

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct) => Task.Run(() =>
    {
        var csv = new StringBuilder("vi tri cu,vi tri moi\r\n");
        int skipped = 0;
        foreach (var m in _plan)
        {
            ct.ThrowIfCancellationRequested();
            var fi = new FileInfo(m.Source);
            if (!fi.Exists || fi.Length != m.Length || fi.LastWriteTimeUtc != m.Modified) { skipped++; continue; }
            Directory.CreateDirectory(m.DestFolder);
            var dest = UniqueName(m.DestFolder, m.FileName);
            File.Move(m.Source, dest);
            _movedFrom.Add(m.Source);
            _movedTo.Add(dest);
            csv.Append('"').Append(m.Source).Append("\",\"").Append(dest).Append("\"\r\n");
            if (_movedTo.Count % 50 == 0) progress.Report($"Đã chuyển {_movedTo.Count} file...");
        }
        if (_movedTo.Count == 0) return FixResult.Failed("Không chuyển được file nào (file đã đổi hoặc mất kể từ lúc quét). Hãy xem lại gợi ý.");
        File.WriteAllText(_csvPath, csv.ToString(), new UTF8Encoding(true));
        return FixResult.Succeeded($"Đã sắp xếp {_movedTo.Count} file" + (skipped > 0 ? $", bỏ qua {skipped} file đã thay đổi" : "") + $". Vị trí cũ ghi trong {Path.GetFileName(_csvPath)}.");
    }, ct);

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.Run(() =>
        _movedTo.Count > 0 && _movedTo.All(File.Exists) && _movedFrom.All(p => !File.Exists(p)) && File.Exists(_csvPath), ct);

    public static string UniqueName(string folder, string fileName)
    {
        var dest = Path.Combine(folder, fileName);
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 2; n < 10_000; n++)
        {
            dest = Path.Combine(folder, $"{name} ({n}){ext}");
            if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        }
        throw new IOException("Không tìm được tên file trống.");
    }
}
