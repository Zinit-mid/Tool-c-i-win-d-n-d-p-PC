using System.IO;
using System.Text.Json;
using PCTool.Core.Framework;

namespace PCTool.Core.Checklist;

public sealed record ChecklistItem(string Id, string Text, string Hint);

/// <summary>Danh sách việc nên làm sau khi cài lại Windows, có tick và nhớ lại giữa các lần mở app. Chỉ ghi file của PCTool.</summary>
public sealed class PostInstallChecklist
{
    public static IReadOnlyList<ChecklistItem> Items { get; } = new ChecklistItem[]
    {
        new("update", "Chạy Windows Update cho đến khi hết bản cập nhật", "Cài xong có thể cần khởi động lại nhiều lần"),
        new("driver", "Quét và cài driver còn thiếu", "Mở ô \"Quét driver thiếu\""),
        new("wifi", "Nạp lại Wi-Fi đã sao lưu", "Ô \"Khôi phục sau cài Win\""),
        new("apps", "Cài các ứng dụng cần dùng", "Ô \"Cài nhanh ứng dụng\", có thể chọn theo bộ"),
        new("data", "Chép lại dữ liệu cá nhân đã sao lưu", "Chỉ chép file chưa có, không ghi đè"),
        new("zalo", "Khôi phục dữ liệu Zalo (nếu dùng)", "Cài Zalo, đăng nhập một lần, đóng hẳn rồi mới khôi phục"),
        new("license", "Kiểm tra Windows/Office đã kích hoạt chưa", "Ô \"Báo cáo & bản quyền\""),
        new("startup", "Tắt bớt app khởi động cùng Windows", "Ô \"Chương trình khởi động\""),
        new("clean", "Dọn rác và xem ổ còn trống bao nhiêu", "Ô \"Dọn dẹp rác\" và \"Dung lượng ổ\""),
        new("restore", "Tạo điểm khôi phục khi máy đã ổn định", "Để có mốc quay lại nếu sau này lỗi"),
    };

    private sealed class FileModel
    {
        public int SchemaVersion { get; set; } = 1;
        public List<string> Done { get; set; } = new();
    }

    private readonly string _path;
    public PostInstallChecklist() : this(Path.Combine(AppPaths.DataDir, "checklist.json")) { }
    public PostInstallChecklist(string path) => _path = path;

    public HashSet<string> LoadDone()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path));
            return (m?.Done ?? new()).Where(id => Items.Any(i => i.Id == id)).ToHashSet();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Move(_path, _path + ".corrupt", true); } catch { }
            return new();
        }
    }

    public bool Set(string id, bool done)
    {
        if (!Items.Any(i => i.Id == id)) return false;
        var set = LoadDone();
        if (done) set.Add(id); else set.Remove(id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Done = set.OrderBy(x => x).ToList() }));
            File.Move(tmp, _path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Reset()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
