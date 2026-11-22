using System.Text;
using PCTool.Core.Drivers;
using PCTool.Core.License;
using PCTool.Core.Startup;

namespace PCTool.Core.Report;

/// <summary>Ghép báo cáo máy dạng chữ để gửi cho người hỗ trợ. Không có khóa sản phẩm, mật khẩu hay đường dẫn cá nhân.</summary>
public static class MachineReport
{
    public static string Build(DateTimeOffset now, string hardwareText, DriverScanResult? drivers,
        IReadOnlyList<LicenseEntry>? licenses, IReadOnlyList<StartupItem>? startup, IReadOnlyList<string>? logTail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BÁO CÁO MÁY - PCTool");
        sb.AppendLine("Ngày giờ: " + now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine();
        sb.AppendLine("== Thông số máy ==");
        sb.AppendLine(string.IsNullOrWhiteSpace(hardwareText) ? "(không đọc được)" : hardwareText.TrimEnd());
        sb.AppendLine();

        sb.AppendLine("== Driver ==");
        if (drivers == null) sb.AppendLine("(chưa quét)");
        else
        {
            sb.AppendLine($"Hãng/mẫu máy: {drivers.Maker} {drivers.Model}".TrimEnd());
            if (drivers.Problems.Count == 0) sb.AppendLine("Không có thiết bị nào báo lỗi.");
            foreach (var p in drivers.Problems) sb.AppendLine($"- {p.Name} ({p.DeviceClass}): {p.Meaning}");
        }
        sb.AppendLine();

        sb.AppendLine("== Bản quyền ==");
        if (licenses == null) sb.AppendLine("(chưa đọc)");
        else if (licenses.Count == 0) sb.AppendLine("Không đọc được sản phẩm nào có khóa.");
        else foreach (var l in licenses) sb.AppendLine($"- {l.Name}: {l.StatusText}");
        sb.AppendLine();

        sb.AppendLine("== Chương trình khởi động cùng Windows ==");
        if (startup == null) sb.AppendLine("(chưa đọc)");
        else
        {
            sb.AppendLine($"Tổng {startup.Count} mục, đang bật {startup.Count(s => s.Enabled)}.");
            foreach (var s in startup.Where(s => s.Enabled)) sb.AppendLine("- " + s.Name);
        }

        if (logTail is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("== Nhật ký gần nhất ==");
            foreach (var line in logTail) sb.AppendLine(line);
        }
        return sb.ToString();
    }
}
