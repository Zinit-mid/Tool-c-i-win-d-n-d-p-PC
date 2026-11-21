using System.Text.Json;

namespace PCTool.Core.Reinstall;

/// <summary>Một phân vùng của ổ đĩa (dữ liệu đọc được, chưa phán đoán gì).</summary>
public sealed record PartitionInfo(
    int Number, string DriveLetter, long Size, string Type, string Label, string FileSystem, long? FreeBytes)
{
    /// <summary>Dung lượng đã dùng; null nếu không đọc được (phân vùng không có ký tự ổ).</summary>
    public long? UsedBytes => FreeBytes is long f && Size >= f ? Size - f : null;
}

/// <summary>Một ổ đĩa vật lý theo Windows (Get-Disk).</summary>
public sealed record DiskInfo(
    int Number, string Name, string Serial, string UniqueId, string BusType, long Size,
    bool IsSystem, bool IsBoot, bool IsOffline, bool IsReadOnly, string PartitionStyle,
    IReadOnlyList<PartitionInfo> Partitions);

public sealed record DiskScan(string SystemDriveLetter, IReadOnlyList<DiskInfo> Disks);

/// <summary>
/// Đọc JSON do script liệt kê ổ đĩa trả về. Chấp nhận cả trường hợp PowerShell 5.1 gộp mảng 1 phần tử
/// thành đối tượng đơn. Thiếu/sai dữ liệu thì ném <see cref="FormatException"/> (không đoán).
/// </summary>
public static class DiskListParser
{
    public static DiskScan Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Dữ liệu ổ đĩa không đúng định dạng.", ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Dữ liệu ổ đĩa không đúng định dạng.");

            var sys = Str(root, "systemDrive").Trim().TrimEnd(':').ToUpperInvariant();
            var disks = new List<DiskInfo>();
            foreach (var d in Items(root, "disks"))
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                if (!d.TryGetProperty("Number", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var num))
                    throw new FormatException("Có ổ đĩa không có số thứ tự.");

                var parts = new List<PartitionInfo>();
                foreach (var p in Items(d, "Partitions"))
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    parts.Add(new PartitionInfo(
                        (int)Num(p, "PartitionNumber"), Str(p, "DriveLetter").Trim().ToUpperInvariant(), Num(p, "Size"),
                        Str(p, "Type"), Str(p, "Label"), Str(p, "FileSystem"), NullableNum(p, "Free")));
                }

                disks.Add(new DiskInfo(
                    num, Str(d, "FriendlyName").Trim(), Str(d, "SerialNumber").Trim(), Str(d, "UniqueId").Trim(),
                    Str(d, "BusType").Trim(), Num(d, "Size"),
                    Bool(d, "IsSystem"), Bool(d, "IsBoot"), Bool(d, "IsOffline"), Bool(d, "IsReadOnly"),
                    Str(d, "PartitionStyle").Trim(), parts));
            }
            return new DiskScan(sys, disks);
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array) { foreach (var e in v.EnumerateArray()) yield return e; }
        else if (v.ValueKind == JsonValueKind.Object) yield return v;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name) => NullableNum(e, name) ?? 0;

    private static long? NullableNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
