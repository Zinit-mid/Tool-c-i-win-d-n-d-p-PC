using System.Text.Json;

namespace PCTool.Core.Framework;

/// <summary>Nội dung settings.json (xem DU-LIEU-PCTool.md mục 2.1).</summary>
public sealed class SettingsData
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>Ô "Chạy thử" bật sẵn ở mọi màn hình.</summary>
    public bool DryRunDefault { get; set; } = true;

    /// <summary>Chế độ an toàn (máy công ty): khóa mọi thao tác thay đổi máy, chỉ cho quét và xem.</summary>
    public bool SafeMode { get; set; }

    public string BackupRoot { get; set; } = "";
    public int DefenderOffMinutes { get; set; } = 30;
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Luôn true: không cho tắt hộp xác nhận (an toàn cho người nhà).</summary>
    public bool ConfirmBeforeFix { get; set; } = true;

    /// <summary>Đưa giá trị sai/ngoài khoảng về mặc định, để file bị sửa tay cũng không làm hỏng app.</summary>
    public SettingsData Normalized()
    {
        if (DefenderOffMinutes is not (15 or 30 or 60)) DefenderOffMinutes = 30;
        if (LogRetentionDays is < 7 or > 365) LogRetentionDays = 30;
        ConfirmBeforeFix = true;
        BackupRoot ??= "";
        return this;
    }
}

/// <summary>
/// Đọc/ghi settings.json. Ghi nguyên tử, giữ .bak, file hỏng đổi tên .corrupt và dùng mặc định,
/// file do bản app mới hơn tạo thì chỉ đọc.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly Lazy<SettingsService> DefaultInstance =
        new(() => new SettingsService(Path.Combine(AppPaths.DataDir, "settings.json")));

    /// <summary>Bản dùng chung của app.</summary>
    public static SettingsService Default => DefaultInstance.Value;

    private readonly string _path;
    private readonly object _lock = new();
    private SettingsData _data;

    public const string SafeModeMessage =
        "Chế độ an toàn đang bật nên mọi thao tác thay đổi máy bị khóa. Tắt Chế độ an toàn ở góc trên nếu bạn chắc chắn muốn làm.";

    public bool ReadOnly { get; private set; }
    public string? Warning { get; private set; }

    public event Action? Changed;

    public SettingsService(string path)
    {
        _path = path;
        _data = LoadFromDisk();
    }

    public bool SafeMode { get { lock (_lock) return _data.SafeMode; } }
    public bool DryRunDefault { get { lock (_lock) return _data.DryRunDefault; } }
    public int DefenderOffMinutes { get { lock (_lock) return _data.DefenderOffMinutes; } }

    /// <summary>
    /// Tạo tùy chọn chạy cho <see cref="FixExecutor"/> từ cài đặt hiện tại. Mọi màn hình dùng hàm này
    /// nên Chế độ an toàn luôn có hiệu lực, không thể quên ở một nơi nào đó.
    /// </summary>
    public FixOptions BuildOptions(bool dryRun, bool createRestorePoint = false) => new()
    {
        DryRun = dryRun,
        SafeMode = SafeMode,
        CreateRestorePoint = createRestorePoint
    };

    public bool SetSafeMode(bool on) => Update(d => d.SafeMode = on);
    public bool SetDryRunDefault(bool on) => Update(d => d.DryRunDefault = on);

    /// <summary>Trả về false nếu không ghi được (file chỉ đọc hoặc lỗi ổ đĩa). Giá trị trong bộ nhớ vẫn đổi để phiên này an toàn.</summary>
    private bool Update(Action<SettingsData> change)
    {
        bool saved;
        lock (_lock)
        {
            change(_data);
            _data.Normalized();
            saved = !ReadOnly && TrySave();
        }
        Changed?.Invoke();
        return saved;
    }

    private SettingsData LoadFromDisk()
    {
        if (!File.Exists(_path)) return new SettingsData();

        try
        {
            var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_path), Json) ?? new SettingsData();
            if (data.SchemaVersion > SettingsData.CurrentSchema)
            {
                ReadOnly = true;
                Warning = "File cài đặt do bản PCTool mới hơn tạo ra nên chỉ đọc, thay đổi sẽ không được lưu.";
            }
            return data.Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try { File.Move(_path, _path + ".corrupt", true); } catch (IOException) { }
            Warning = "File cài đặt bị hỏng, đã giữ bản hỏng (.corrupt) và dùng cài đặt mặc định.";
            Logger.Warn("settings.json bị hỏng, đã đổi tên thành .corrupt.");
            return new SettingsData();
        }
    }

    private bool TrySave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path))
            {
                File.Delete(_path + ".bak3");
                if (File.Exists(_path + ".bak2")) File.Move(_path + ".bak2", _path + ".bak3");
                if (File.Exists(_path + ".bak1")) File.Move(_path + ".bak1", _path + ".bak2");
                File.Copy(_path, _path + ".bak1", true);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
            File.Move(tmp, _path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Không ghi được settings.json: " + ex.Message);
            return false;
        }
    }
}
