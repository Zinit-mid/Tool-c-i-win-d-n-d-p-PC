using System.Text.Json;

namespace PCTool.Core.Defender;

/// <summary>Trạng thái Windows Defender. Mỗi trường null nghĩa là không đọc được.</summary>
public sealed record DefenderStatus(bool? RealTimeEnabled, bool? TamperProtected, bool? ServiceEnabled)
{
    public static DefenderStatus Unknown { get; } = new(null, null, null);

    public static DefenderStatus Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Unknown;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unknown;
            return new DefenderStatus(
                Bool(root, "RealTimeProtectionEnabled"), Bool(root, "IsTamperProtected"), Bool(root, "AMServiceEnabled"));
        }
        catch (JsonException)
        {
            return Unknown;
        }
    }

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;
}

/// <summary>Ghi lại lần tắt tạm để lần sau mở app còn biết và nhắc/bật lại. Lưu ở defender-timer.json.</summary>
public sealed class DefenderTimerState
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset OffAt { get; set; }
    public DateTimeOffset ReEnableAt { get; set; }
    public string TaskName { get; set; } = DefenderService.TaskName;
}
