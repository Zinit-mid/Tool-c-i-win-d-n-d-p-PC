using System.Text.Json;
using System.Text.Json.Serialization;
using PCTool.Core.Framework;

namespace PCTool.Core.AppInstaller;

public sealed record CatalogOpResult(bool Ok, string Message);

public sealed record ImportResult(int Added, int Replaced, int KeptExisting, IReadOnlyList<string> Skipped);

public sealed class CatalogState
{
    public IReadOnlyList<AppEntry> Items { get; init; } = Array.Empty<AppEntry>();
    public IReadOnlyList<string> HiddenBuiltIn { get; init; } = Array.Empty<string>();

    /// <summary>true nếu file do bản app MỚI HƠN tạo: chỉ đọc, không ghi để khỏi làm hỏng.</summary>
    public bool ReadOnly { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Danh sách cài nhanh = mục mặc định (nhúng trong app) + mục người dùng (apps.user.json). Cùng id thì bản người dùng thắng.
/// Quy tắc ghi file: ghi nguyên tử, giữ 3 bản .bak, file hỏng thì đổi tên .corrupt chứ không xóa.
/// </summary>
public sealed class AppCatalog
{
    public const int CurrentSchema = 1;
    public const int MaxImportItems = 500;
    public const int MaxImportBytes = 1_000_000;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static IReadOnlyList<AppEntry> Defaults { get; } = new[]
    {
        new AppEntry { Id = "zalo", Name = "Zalo", Kind = AppKind.Winget, WingetId = "VNGCorp.Zalo", Group = "Liên lạc", SelectedByDefault = true, BuiltIn = true },
        new AppEntry { Id = "chrome", Name = "Google Chrome", Kind = AppKind.Winget, WingetId = "Google.Chrome", Group = "Trình duyệt", SelectedByDefault = true, BuiltIn = true },
        new AppEntry { Id = "facebook", Name = "Facebook", Kind = AppKind.WebShortcut, Url = "https://www.facebook.com", Group = "Liên lạc", SelectedByDefault = true, BuiltIn = true }
    };

    /// <summary>
    /// Bộ app gợi ý (mã winget đã đối chiếu với kho winget-pkgs của Microsoft). KHÔNG tự hiện; bấm "Thêm app gợi ý" mới thêm,
    /// mặc định không tick. Chưa có: Notepad++ và VC++ 2015+ (mã chứa dấu +, bộ kiểm tra mã của PCTool không nhận),
    /// WhatsApp (chỉ có trên Microsoft Store), EVKey (không thấy trong kho).
    /// </summary>
    public static IReadOnlyList<AppEntry> Suggested { get; } = new[]
    {
        S("unikey", "UniKey", "UniKey.UniKey", "Tiện ích"),
        S("7zip", "7-Zip", "7zip.7zip", "Tiện ích"),
        S("winrar", "WinRAR", "RARLab.WinRAR", "Tiện ích"),
        S("hibit-uninstaller", "HiBit Uninstaller", "HiBitSoftware.HiBitUninstaller", "Tiện ích"),
        S("google-drive", "Google Drive", "Google.GoogleDrive", "Lưu trữ"),
        S("onedrive", "OneDrive", "Microsoft.OneDrive", "Lưu trữ"),
        S("firefox", "Firefox", "Mozilla.Firefox", "Trình duyệt"),
        S("coccoc", "Cốc Cốc", "CocCoc.CocCoc", "Trình duyệt"),
        S("brave", "Brave", "Brave.Brave", "Trình duyệt"),
        S("klite", "K-Lite Codec Pack", "CodecGuide.K-LiteCodecPack.Standard", "Xem phim"),
        S("vlc", "VLC", "VideoLAN.VLC", "Xem phim"),
        S("videomass", "Videomass (cắt/ghép video)", "GianlucaPernigotto.Videomass", "Cắt ghép video"),
        S("foxit", "Foxit Reader", "Foxit.FoxitReader", "Đọc PDF"),
        S("sumatrapdf", "SumatraPDF", "SumatraPDF.SumatraPDF", "Đọc PDF"),
        S("ultraviewer", "UltraViewer", "DucFabulous.UltraViewer", "Điều khiển từ xa"),
        S("teamviewer", "TeamViewer", "TeamViewer.TeamViewer", "Điều khiển từ xa"),
        S("anydesk", "AnyDesk", "AnyDesk.AnyDesk", "Điều khiển từ xa"),
        S("telegram", "Telegram", "Telegram.TelegramDesktop", "Liên lạc"),
        S("discord", "Discord", "Discord.Discord", "Liên lạc"),
        S("zoom", "Zoom", "Zoom.Zoom", "Liên lạc"),
        S("viber", "Viber", "Rakuten.Viber", "Liên lạc"),
        S("teams", "Microsoft Teams", "Microsoft.Teams", "Liên lạc")
    };

    private static AppEntry S(string id, string name, string wingetId, string group) =>
        new() { Id = id, Name = name, Kind = AppKind.Winget, WingetId = wingetId, Group = group, SelectedByDefault = false, BuiltIn = false };

    /// <summary>Thêm các app gợi ý còn thiếu. Mục đã có (cùng id) giữ nguyên, không ghi đè.</summary>
    public CatalogOpResult AddSuggested()
    {
        int added = 0;
        var r = Mutate(file =>
        {
            foreach (var e in Suggested)
            {
                if (file.Items.Any(i => i.Id == e.Id) || Defaults.Any(d => d.Id == e.Id)) continue;
                if (AppEntryValidator.Validate(e).Count > 0) continue;
                file.Items.Add(e.Clone());
                added++;
            }
        }, "");
        return r.Ok ? new(true, added == 0 ? "Các app gợi ý đã có đủ trong danh sách." : $"Đã thêm {added} app gợi ý (chưa tick, bạn tự chọn).") : r;
    }

    internal sealed class UserFile
    {
        public int SchemaVersion { get; set; } = CurrentSchema;
        public List<AppEntry> Items { get; set; } = new();
        public List<string> HiddenBuiltIn { get; set; } = new();
    }

    private readonly string _path;

    public AppCatalog() : this(Path.Combine(AppPaths.DataDir, "apps.user.json")) { }

    public AppCatalog(string userFilePath) => _path = userFilePath;

    // ------------------------------------------------------------------ đọc

    public CatalogState Load()
    {
        var (file, readOnly, warnings) = ReadUserFile();

        var hidden = new HashSet<string>(file.HiddenBuiltIn, StringComparer.Ordinal);
        var merged = new List<AppEntry>();
        var userById = new Dictionary<string, AppEntry>(StringComparer.Ordinal);

        foreach (var u in file.Items)
        {
            var errors = AppEntryValidator.Validate(u);
            if (errors.Count > 0) { warnings.Add($"Bỏ qua mục \"{u.Name}\": {errors[0]}"); continue; }
            userById[u.Id] = u;
        }

        foreach (var d in Defaults)
        {
            if (userById.TryGetValue(d.Id, out var over)) { merged.Add(over); userById.Remove(d.Id); }
            else if (!hidden.Contains(d.Id)) merged.Add(d.Clone());
        }
        merged.AddRange(file.Items.Where(u => userById.ContainsKey(u.Id) && userById[u.Id] == u));

        return new CatalogState
        {
            Items = merged,
            HiddenBuiltIn = hidden.ToList(),
            ReadOnly = readOnly,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Như <see cref="Load"/> nhưng thêm sẵn toàn bộ app gợi ý (chưa có trong danh sách và chưa bị ẩn) để giao diện hiện đủ mà
    /// không cần bấm nút. Không ghi file. Mục gợi ý coi như mục mặc định: bấm ẩn thì chỉ ẩn, "hiện lại mục mặc định" để hiện lại.
    /// </summary>
    public CatalogState LoadAll()
    {
        var s = Load();
        var have = new HashSet<string>(s.Items.Select(i => i.Id), StringComparer.Ordinal);
        var hidden = new HashSet<string>(s.HiddenBuiltIn, StringComparer.Ordinal);
        var items = s.Items.ToList();
        foreach (var e in Suggested)
        {
            if (have.Contains(e.Id) || hidden.Contains(e.Id)) continue;
            var c = e.Clone();
            c.BuiltIn = true;
            items.Add(c);
        }

        return new CatalogState { Items = items, HiddenBuiltIn = s.HiddenBuiltIn, ReadOnly = s.ReadOnly, Warnings = s.Warnings };
    }

    // ------------------------------------------------------------------ sửa

    public CatalogOpResult AddOrUpdate(AppEntry entry)
    {
        var copy = entry.Clone();
        copy.Name = (copy.Name ?? "").Trim();
        copy.BuiltIn = false;
        var errors = AppEntryValidator.Validate(copy);
        if (errors.Count > 0) return new(false, errors[0]);

        return Mutate(file =>
        {
            file.Items.RemoveAll(i => i.Id == copy.Id);
            file.Items.Add(copy);
            file.HiddenBuiltIn.Remove(copy.Id);
        }, $"Đã lưu \"{copy.Name}\".");
    }

    /// <summary>Mục tự thêm: xóa hẳn. Mục mặc định: chỉ ẩn (không xóa được).</summary>
    public CatalogOpResult Remove(string id)
    {
        bool isDefault = Defaults.Any(d => d.Id == id) || Suggested.Any(d => d.Id == id);
        return Mutate(file =>
        {
            file.Items.RemoveAll(i => i.Id == id);
            if (isDefault && !file.HiddenBuiltIn.Contains(id)) file.HiddenBuiltIn.Add(id);
        }, isDefault ? "Đã ẩn mục mặc định (có thể hiện lại)." : "Đã xóa mục.");
    }

    public CatalogOpResult RestoreHiddenDefaults() =>
        Mutate(file => file.HiddenBuiltIn.Clear(), "Đã hiện lại các mục mặc định.");

    // ------------------------------------------------------------------ xuất / nhập

    /// <summary>File xuất chỉ chứa mục người dùng tự thêm (mục mặc định đã có sẵn trong app).</summary>
    public string Export(DateTimeOffset now)
    {
        var (file, _, _) = ReadUserFile();
        var items = file.Items.Where(i => AppEntryValidator.Validate(i).Count == 0).ToList();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = CurrentSchema,
            exportedAt = now,
            items
        }, JsonOptions);
    }

    /// <summary>Mục sai bị bỏ qua và báo lại. Mục trùng id thì hỏi <paramref name="replaceOnConflict"/>.</summary>
    public ImportResult Import(string json, Func<AppEntry, AppEntry, bool> replaceOnConflict)
    {
        if (json.Length > MaxImportBytes) throw new InvalidDataException("File quá lớn, không phải danh sách app.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidDataException("File không đúng định dạng JSON."); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Đây không phải file danh sách app của PCTool.");
            if (root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var ver) && ver > CurrentSchema)
                throw new InvalidDataException("File do bản PCTool mới hơn tạo ra. Hãy cập nhật PCTool rồi nhập lại.");
            if (itemsEl.GetArrayLength() > MaxImportItems)
                throw new InvalidDataException($"File có quá nhiều mục (tối đa {MaxImportItems}).");

            var skipped = new List<string>();
            var incoming = new List<AppEntry>();
            foreach (var el in itemsEl.EnumerateArray())
            {
                AppEntry? e;
                try { e = el.Deserialize<AppEntry>(JsonOptions); }
                catch (JsonException) { skipped.Add("Một mục có định dạng sai."); continue; }
                if (e == null) { skipped.Add("Một mục trống."); continue; }

                e.BuiltIn = false;   // mục nhập vào không bao giờ là mục mặc định
                e.Name = (e.Name ?? "").Trim();
                var errors = AppEntryValidator.Validate(e);
                if (errors.Count > 0) { skipped.Add($"\"{e.Name}\": {errors[0]}"); continue; }
                incoming.Add(e);
            }

            int added = 0, replaced = 0, kept = 0;
            var current = Load().Items.ToDictionary(i => i.Id, StringComparer.Ordinal);

            var write = Mutate(file =>
            {
                foreach (var e in incoming)
                {
                    if (current.TryGetValue(e.Id, out var existing))
                    {
                        if (replaceOnConflict(existing, e))
                        {
                            file.Items.RemoveAll(i => i.Id == e.Id);
                            file.Items.Add(e);
                            file.HiddenBuiltIn.Remove(e.Id);
                            current[e.Id] = e;
                            replaced++;
                        }
                        else kept++;
                    }
                    else
                    {
                        file.Items.Add(e);
                        current[e.Id] = e;
                        added++;
                    }
                }
            }, "");

            if (!write.Ok) throw new InvalidOperationException(write.Message);
            return new ImportResult(added, replaced, kept, skipped);
        }
    }

    // ------------------------------------------------------------------ file

    private (UserFile File, bool ReadOnly, List<string> Warnings) ReadUserFile()
    {
        var warnings = new List<string>();
        if (!File.Exists(_path)) return (new UserFile(), false, warnings);

        try
        {
            var file = JsonSerializer.Deserialize<UserFile>(File.ReadAllText(_path), JsonOptions) ?? new UserFile();
            file.Items ??= new();
            file.HiddenBuiltIn ??= new();
            bool newer = file.SchemaVersion > CurrentSchema;
            if (newer) warnings.Add("Danh sách app do bản PCTool mới hơn tạo ra nên đang ở chế độ chỉ đọc.");
            return (file, newer, warnings);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try { File.Move(_path, _path + ".corrupt", true); } catch (IOException) { }
            Logger.Warn("apps.user.json bị hỏng, đã đổi tên thành .corrupt.");
            warnings.Add("File danh sách app của bạn bị hỏng. Đã giữ lại bản hỏng (.corrupt) và dùng danh sách mặc định.");
            return (new UserFile(), false, warnings);
        }
    }

    private CatalogOpResult Mutate(Action<UserFile> change, string okMessage)
    {
        var (file, readOnly, _) = ReadUserFile();
        if (readOnly) return new(false, "Danh sách app do bản PCTool mới hơn tạo ra nên không ghi được. Hãy cập nhật PCTool.");

        change(file);
        file.SchemaVersion = CurrentSchema;

        try
        {
            WriteAtomic(JsonSerializer.Serialize(file, JsonOptions));
            return new(true, okMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "Không ghi được danh sách app: " + ex.Message);
        }
    }

    private void WriteAtomic(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        if (File.Exists(_path))
        {
            // Giữ 3 bản .bak gần nhất: .bak1 (mới nhất), .bak2, .bak3.
            File.Delete(_path + ".bak3");
            if (File.Exists(_path + ".bak2")) File.Move(_path + ".bak2", _path + ".bak3");
            if (File.Exists(_path + ".bak1")) File.Move(_path + ".bak1", _path + ".bak2");
            File.Copy(_path, _path + ".bak1", true);
        }

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, _path, true);
    }
}
