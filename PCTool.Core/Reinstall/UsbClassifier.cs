using System.Text.RegularExpressions;

namespace PCTool.Core.Reinstall;

/// <summary>Những thứ cần tránh ghi đè: file ISO đang dùng và thư mục sao lưu đã chọn (chỉ lấy ký tự ổ).</summary>
public sealed record UsbScanContext(string? IsoPath = null, string? BackupPath = null);

/// <summary>Một ổ USB đã được phân loại. <see cref="Blockers"/> rỗng nghĩa là đủ điều kiện để chọn (GĐ2 sẽ dùng).</summary>
public sealed class UsbCandidate
{
    public required DiskInfo Disk { get; init; }
    public required IReadOnlyList<string> Blockers { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public long? UsedBytes { get; init; }
    public bool UsedBytesUnknown { get; init; }
    public bool Eligible => Blockers.Count == 0;

    /// <summary>Chuỗi người dùng phải gõ lại để xác nhận: "số ổ-4 ký tự cuối serial". Null nếu không đủ điều kiện.</summary>
    public string? ConfirmationToken => Eligible ? UsbClassifier.ConfirmationFor(Disk) : null;
}

public sealed record UsbScanResult(IReadOnlyList<UsbCandidate> Candidates, int HiddenNonUsbCount, string SystemDriveLetter);

/// <summary>
/// Phân loại ổ đĩa theo các lớp chặn trong tài liệu thiết kế (mục 4). Hàm thuần, không đọc/ghi hệ thống, test được bằng dữ liệu giả.
/// Chỉ ổ gắn qua USB mới được liệt kê; ổ khác chỉ được đếm, không hiện, không bao giờ chọn được.
/// </summary>
public static class UsbClassifier
{
    /// <summary>Ngưỡng sơ bộ: USB "32 GB" thực tế khoảng 29,8-31,9 tỷ byte. Nhỏ hơn mức này không đủ chứa bộ cài + dữ liệu.</summary>
    public const long MinBytes = 28_000_000_000L;

    /// <summary>Lớn hơn mức này thì cảnh báo "có thể là ổ cứng ngoài chứa dữ liệu".</summary>
    public const long BigDriveWarnBytes = 128L * 1000 * 1000 * 1000;

    private static readonly Regex DriveRoot = new(@"^\s*([A-Za-z]):", RegexOptions.Compiled);

    public static string? DriveLetterOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var m = DriveRoot.Match(path);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static string LastFour(string serial)
    {
        var s = serial.Trim();
        return s.Length <= 4 ? s : s[^4..];
    }

    public static string ConfirmationFor(DiskInfo d) => $"{d.Number}-{LastFour(d.Serial)}".ToUpperInvariant();

    /// <summary>So khớp chuỗi người dùng gõ (không phân biệt hoa thường, bỏ khoảng trắng hai đầu).</summary>
    public static bool ConfirmationMatches(DiskInfo d, string? typed) =>
        !string.IsNullOrWhiteSpace(typed) && d.Serial.Trim().Length > 0 &&
        string.Equals(typed.Trim(), ConfirmationFor(d), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kiểm tra lại NGAY TRƯỚC KHI xóa: ổ phải còn đúng là ổ đã chọn. Số thứ tự, mã duy nhất, serial và dung lượng
    /// đều phải trùng; khác một thứ là hủy. (Chưa dùng ở GĐ0; GĐ2 bắt buộc gọi.)
    /// </summary>
    public static bool IsSameDisk(DiskInfo chosen, DiskInfo? now) =>
        now is not null && chosen.UniqueId.Length > 0 && chosen.Serial.Length > 0 &&
        now.Number == chosen.Number && now.UniqueId == chosen.UniqueId && now.Serial == chosen.Serial &&
        now.Size == chosen.Size && IsUsb(now);

    public static bool IsUsb(DiskInfo d) => d.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase);

    public static UsbScanResult Classify(DiskScan scan, UsbScanContext ctx)
    {
        var avoid = new Dictionary<string, string>();
        if (scan.SystemDriveLetter.Length > 0) avoid[scan.SystemDriveLetter] = "ổ Windows đang chạy";
        if (DriveLetterOf(ctx.IsoPath) is { } iso) avoid.TryAdd(iso, "ổ chứa file ISO đang dùng");
        if (DriveLetterOf(ctx.BackupPath) is { } bk) avoid.TryAdd(bk, "ổ chứa thư mục sao lưu đã chọn");

        var list = new List<UsbCandidate>();
        var hidden = 0;
        foreach (var d in scan.Disks.OrderBy(x => x.Number))
        {
            if (!IsUsb(d)) { hidden++; continue; }

            var blockers = new List<string>();
            var warnings = new List<string>();

            if (d.IsSystem || d.IsBoot) blockers.Add("Là ổ hệ thống/khởi động của máy này.");
            foreach (var p in d.Partitions)
                if (p.DriveLetter.Length > 0 && avoid.TryGetValue(p.DriveLetter, out var why))
                    blockers.Add($"Chứa {why} ({p.DriveLetter}:).");
            if (d.IsOffline) blockers.Add("Ổ đang ở trạng thái Offline.");
            if (d.IsReadOnly) blockers.Add("Ổ đang bị khóa chỉ đọc.");
            if (d.Size <= 0) blockers.Add("Không đọc được dung lượng (đầu đọc thẻ trống?).");
            else if (d.Size < MinBytes) blockers.Add("Dung lượng quá nhỏ: cần USB từ 32 GB.");
            if (d.UniqueId.Length == 0) blockers.Add("Không đọc được mã duy nhất của ổ nên không thể kiểm tra lại trước khi xóa.");
            if (d.Serial.Length == 0) blockers.Add("Không đọc được số serial nên không thể xác nhận an toàn.");

            if (d.Size > BigDriveWarnBytes)
                warnings.Add("Ổ lớn hơn 128 GB: có thể là ổ cứng ngoài chứa dữ liệu, hãy kiểm tra kỹ.");

            var known = d.Partitions.Where(p => p.UsedBytes is not null).Sum(p => p.UsedBytes!.Value);
            var unknown = d.Partitions.Any(p => p.UsedBytes is null);
            if (d.Partitions.Count > 0)
                warnings.Add(unknown
                    ? "Ổ đang có phân vùng; một số phân vùng không đọc được dung lượng đã dùng, coi như đang có dữ liệu."
                    : known > 0 ? "Ổ đang có dữ liệu: mọi thứ trên ổ sẽ bị xóa nếu ghi USB." : "Ổ đang có phân vùng nhưng chưa có dữ liệu.");

            list.Add(new UsbCandidate
            {
                Disk = d,
                Blockers = blockers,
                Warnings = warnings,
                UsedBytes = d.Partitions.Count == 0 ? 0 : known,
                UsedBytesUnknown = unknown
            });
        }
        return new UsbScanResult(list, hidden, scan.SystemDriveLetter);
    }
}
