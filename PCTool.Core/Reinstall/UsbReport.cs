using System.Globalization;
using System.Text;

namespace PCTool.Core.Reinstall;

/// <summary>Biến kết quả quét thành văn bản tiếng Việt để hiện và sao chép. Không đọc/ghi hệ thống.</summary>
public static class UsbReport
{
    public static string Size(long bytes)
    {
        var gb = bytes / 1_000_000_000d;
        if (gb >= 1) return gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        return (bytes / 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    public static string ToText(UsbScanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ổ Windows đang chạy: {r.SystemDriveLetter}: (luôn được bảo vệ, không bao giờ nằm trong danh sách chọn).");
        sb.AppendLine($"Ổ không gắn qua USB (bị ẩn, không thể chọn): {r.HiddenNonUsbCount}.");
        sb.AppendLine();

        if (r.Candidates.Count == 0)
        {
            sb.AppendLine("Không thấy ổ USB nào. Hãy cắm USB (từ 32 GB) rồi bấm Quét lại.");
            return sb.ToString();
        }

        foreach (var c in r.Candidates)
        {
            var d = c.Disk;
            sb.AppendLine($"Ổ số {d.Number}: {(d.Name.Length > 0 ? d.Name : "(không rõ tên)")}  -  {Size(d.Size)}");
            sb.AppendLine($"  Serial: {(d.Serial.Length > 0 ? d.Serial : "(không đọc được)")}   Kiểu bảng phân vùng: {d.PartitionStyle}");
            sb.AppendLine(c.UsedBytesUnknown
                ? "  Đã dùng: không đọc đủ (coi như đang có dữ liệu)"
                : $"  Đã dùng: {Size(c.UsedBytes ?? 0)}");
            if (d.Partitions.Count == 0) sb.AppendLine("  Phân vùng: chưa có.");
            foreach (var p in d.Partitions)
            {
                var letter = p.DriveLetter.Length > 0 ? p.DriveLetter + ":" : "(không có ký tự ổ)";
                var label = p.Label.Length > 0 ? $" \"{p.Label}\"" : "";
                var fs = p.FileSystem.Length > 0 ? " " + p.FileSystem : "";
                sb.AppendLine($"  - Phân vùng {p.Number}: {letter}{label}{fs}, {Size(p.Size)}");
            }
            foreach (var w in c.Warnings) sb.AppendLine("  ! " + w);
            if (c.Eligible)
                sb.AppendLine($"  => ĐỦ ĐIỀU KIỆN để làm USB cài. (Sau này, để ghi thật bạn phải gõ lại: {c.ConfirmationToken})");
            else
                foreach (var b in c.Blockers) sb.AppendLine("  X KHÔNG CHỌN ĐƯỢC: " + b);
            sb.AppendLine();
        }
        sb.AppendLine("Mới chỉ liệt kê. Chưa có gì bị ghi hay xóa trên bất kỳ ổ nào.");
        return sb.ToString();
    }
}
