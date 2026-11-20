using System.Globalization;
using System.Text;

namespace PCTool.Core.Hardware;

public sealed record CpuInfo(string Name, int Cores, int LogicalProcessors, int MaxMhz);
public sealed record GpuInfo(string Name, string? DriverVersion, long AdapterRamBytes);
public sealed record DiskInfo(string Name, string MediaType, long SizeBytes, string HealthStatus, string OperationalStatus);
public sealed record VolumeInfo(string Letter, string Label, long SizeBytes, long FreeBytes)
{
    public double FreePercent => SizeBytes <= 0 ? 100 : FreeBytes * 100.0 / SizeBytes;
}
public sealed record BoardInfo(string Manufacturer, string Product);
public sealed record OsInfo(string Caption, string Version, string Build, string Architecture, DateTimeOffset? LastBoot);

/// <summary>Pin laptop. Null trên máy bàn (không có pin).</summary>
public sealed record BatteryInfo(long DesignCapacity, long FullChargeCapacity, int? ChargePercent)
{
    /// <summary>Phần trăm pin đã chai so với lúc mới (0-100). Null nếu không đủ dữ liệu.</summary>
    public double? WearPercent =>
        DesignCapacity > 0 && FullChargeCapacity > 0
            ? Math.Clamp((1.0 - (double)FullChargeCapacity / DesignCapacity) * 100.0, 0, 100)
            : null;
}

/// <summary>Kết quả đọc thông số máy. Mọi trường có thể rỗng nếu máy không cung cấp.</summary>
public sealed class HardwareReport
{
    public IReadOnlyList<CpuInfo> Cpus { get; init; } = Array.Empty<CpuInfo>();
    public long TotalRamBytes { get; init; }
    public int RamModules { get; init; }
    public int RamSpeedMhz { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public BoardInfo? Board { get; init; }
    public OsInfo? Os { get; init; }
    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = Array.Empty<VolumeInfo>();
    public BatteryInfo? Battery { get; init; }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "không rõ";
        double gb = bytes / 1073741824.0;
        return gb >= 1000
            ? (gb / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " TB"
            : gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>Văn bản tiếng Việt để hiển thị và sao chép gửi người hỗ trợ.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var c in Cpus)
            sb.AppendLine($"CPU: {c.Name} ({c.Cores} nhân, {c.LogicalProcessors} luồng, {c.MaxMhz} MHz)");
        sb.AppendLine($"RAM: {FormatBytes(TotalRamBytes)}" +
            (RamModules > 0 ? $" ({RamModules} thanh" + (RamSpeedMhz > 0 ? $", {RamSpeedMhz} MHz" : "") + ")" : ""));
        foreach (var g in Gpus)
            sb.AppendLine($"Card đồ họa: {g.Name}" + (string.IsNullOrEmpty(g.DriverVersion) ? "" : $" (driver {g.DriverVersion})"));
        if (Board != null) sb.AppendLine($"Mainboard: {Board.Manufacturer} {Board.Product}".TrimEnd());
        if (Os != null)
            sb.AppendLine($"Windows: {Os.Caption} {Os.Architecture}, phiên bản {Os.Version} (build {Os.Build})");
        foreach (var d in Disks)
            sb.AppendLine($"Ổ cứng: {d.Name} - {d.MediaType}, {FormatBytes(d.SizeBytes)}, sức khỏe: {d.HealthStatus}");
        foreach (var v in Volumes)
            sb.AppendLine($"Ổ {v.Letter} {v.Label}".TrimEnd() + $": trống {FormatBytes(v.FreeBytes)} / {FormatBytes(v.SizeBytes)} ({v.FreePercent:0}% trống)");
        if (Battery != null)
        {
            var wear = Battery.WearPercent;
            sb.AppendLine("Pin: " + (wear.HasValue ? $"đã chai khoảng {wear.Value:0}%" : "không đọc được độ chai") +
                (Battery.ChargePercent.HasValue ? $", đang sạc {Battery.ChargePercent}%" : ""));
        }
        return sb.ToString();
    }
}
