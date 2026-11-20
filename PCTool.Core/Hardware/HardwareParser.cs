using System.Globalization;
using System.Text.Json;

namespace PCTool.Core.Hardware;

/// <summary>
/// Đổi JSON do PowerShell trả về thành <see cref="HardwareReport"/>.
/// Chịu được dữ liệu thiếu: PowerShell 5.1 hay biến mảng 1 phần tử thành đối tượng, và máy có thể thiếu cảm biến.
/// </summary>
public static class HardwareParser
{
    public static HardwareReport Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Dữ liệu phần cứng không đúng định dạng.");

        var rams = Items(root, "ram").ToList();

        var batteryFull = Items(root, "batteryFull").Select(e => Long(e, "FullChargedCapacity")).FirstOrDefault();
        var batteryDesign = Items(root, "batteryDesign").Select(e => Long(e, "DesignedCapacity")).FirstOrDefault();
        var batteryCharge = Items(root, "batteryCharge").Select(e => (int?)Int(e, "EstimatedChargeRemaining")).FirstOrDefault();
        var hasBattery = Items(root, "batteryCharge").Any() || batteryDesign > 0;

        return new HardwareReport
        {
            Cpus = Items(root, "cpu").Select(e => new CpuInfo(
                Str(e, "Name"), Int(e, "NumberOfCores"), Int(e, "NumberOfLogicalProcessors"), Int(e, "MaxClockSpeed"))).ToList(),
            TotalRamBytes = Long(root, "totalRam"),
            RamModules = rams.Count,
            RamSpeedMhz = rams.Select(e => Int(e, "Speed")).DefaultIfEmpty(0).Max(),
            Gpus = Items(root, "gpu").Select(e => new GpuInfo(
                Str(e, "Name"), NullIfEmpty(Str(e, "DriverVersion")), Long(e, "AdapterRAM"))).ToList(),
            Board = Items(root, "board").Select(e => new BoardInfo(Str(e, "Manufacturer"), Str(e, "Product"))).FirstOrDefault(),
            Os = Items(root, "os").Select(e => new OsInfo(
                Str(e, "Caption"), Str(e, "Version"), Str(e, "BuildNumber"), Str(e, "OSArchitecture"), Date(e, "LastBoot"))).FirstOrDefault(),
            Disks = Items(root, "disks").Select(e => new DiskInfo(
                Str(e, "FriendlyName"), Str(e, "MediaType"), Long(e, "Size"), Str(e, "HealthStatus"), Str(e, "OperationalStatus"))).ToList(),
            Volumes = Items(root, "volumes").Select(e => new VolumeInfo(
                Str(e, "DeviceID"), Str(e, "VolumeName"), Long(e, "Size"), Long(e, "FreeSpace"))).ToList(),
            Battery = hasBattery ? new BatteryInfo(batteryDesign, batteryFull, batteryCharge) : null
        };
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
        else if (v.ValueKind == JsonValueKind.Object)
        {
            yield return v;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim()
            : "";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.Number) return (long)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static int Int(JsonElement e, string name)
    {
        var n = Long(e, name);
        return n is < int.MinValue or > int.MaxValue ? 0 : (int)n;
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
