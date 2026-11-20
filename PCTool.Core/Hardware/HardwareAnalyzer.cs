using PCTool.Core.Framework;

namespace PCTool.Core.Hardware;

/// <summary>
/// Đọc <see cref="HardwareReport"/> và đưa ra các vấn đề (chỉ báo, không có cách sửa tự động:
/// phần cứng hỏng hoặc pin chai thì app không sửa được).
/// </summary>
public static class HardwareAnalyzer
{
    public static IReadOnlyList<Finding> Analyze(HardwareReport report, DateTimeOffset now)
    {
        var list = new List<Finding>();

        foreach (var d in report.Disks)
        {
            var health = d.HealthStatus.ToLowerInvariant();
            if (health == "unhealthy")
                list.Add(new Finding
                {
                    Id = "disk.unhealthy", Severity = Severity.Critical,
                    Title = $"Ổ cứng {d.Name} báo hỏng",
                    Cause = "Windows báo ổ cứng này không còn khỏe. Hãy sao lưu dữ liệu quan trọng ngay và nên thay ổ mới.",
                    TechnicalDetail = $"HealthStatus={d.HealthStatus}; OperationalStatus={d.OperationalStatus}"
                });
            else if (health == "warning")
                list.Add(new Finding
                {
                    Id = "disk.warning", Severity = Severity.Warning,
                    Title = $"Ổ cứng {d.Name} có dấu hiệu yếu",
                    Cause = "Windows báo ổ cứng này có cảnh báo sức khỏe. Nên sao lưu dữ liệu quan trọng sớm.",
                    TechnicalDetail = $"HealthStatus={d.HealthStatus}; OperationalStatus={d.OperationalStatus}"
                });
        }

        foreach (var v in report.Volumes.Where(v => v.SizeBytes > 0))
        {
            if (v.FreePercent < 5)
                list.Add(new Finding
                {
                    Id = "volume.full", Severity = Severity.Critical,
                    Title = $"Ổ {v.Letter} gần đầy",
                    Cause = $"Chỉ còn trống {HardwareReport.FormatBytes(v.FreeBytes)} ({v.FreePercent:0}%). Ổ quá đầy làm máy chậm và Windows Update dễ lỗi."
                });
            else if (v.FreePercent < 10)
                list.Add(new Finding
                {
                    Id = "volume.low", Severity = Severity.Warning,
                    Title = $"Ổ {v.Letter} sắp đầy",
                    Cause = $"Còn trống {HardwareReport.FormatBytes(v.FreeBytes)} ({v.FreePercent:0}%). Nên dọn bớt file."
                });
        }

        if (report.TotalRamBytes > 0)
        {
            double gb = report.TotalRamBytes / 1073741824.0;
            if (gb < 4)
                list.Add(new Finding
                {
                    Id = "ram.low", Severity = Severity.Warning,
                    Title = "RAM ít",
                    Cause = $"Máy chỉ có {HardwareReport.FormatBytes(report.TotalRamBytes)} RAM, dễ bị chậm khi mở nhiều chương trình. Nên nâng lên ít nhất 8 GB."
                });
            else if (gb < 8)
                list.Add(new Finding
                {
                    Id = "ram.modest", Severity = Severity.Info,
                    Title = "RAM ở mức vừa phải",
                    Cause = $"Máy có {HardwareReport.FormatBytes(report.TotalRamBytes)} RAM. Dùng văn phòng ổn, nhưng nâng lên 8 GB sẽ mượt hơn."
                });
        }

        var wear = report.Battery?.WearPercent;
        if (wear >= 60)
            list.Add(new Finding
            {
                Id = "battery.worn", Severity = Severity.Critical,
                Title = "Pin đã chai nhiều",
                Cause = $"Pin chỉ còn khoảng {100 - wear.Value:0}% dung lượng so với lúc mới. Nên thay pin."
            });
        else if (wear >= 40)
            list.Add(new Finding
            {
                Id = "battery.aging", Severity = Severity.Warning,
                Title = "Pin bắt đầu chai",
                Cause = $"Pin còn khoảng {100 - wear.Value:0}% dung lượng so với lúc mới."
            });

        if (report.Os?.LastBoot is { } boot && (now - boot).TotalDays >= 14)
            list.Add(new Finding
            {
                Id = "os.long-uptime", Severity = Severity.Info,
                Title = "Máy chưa khởi động lại hơn 14 ngày",
                Cause = "Khởi động lại giúp giải phóng RAM và hoàn tất các bản cập nhật Windows đang chờ."
            });

        return list.OrderByDescending(f => f.Severity).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
    }
}
