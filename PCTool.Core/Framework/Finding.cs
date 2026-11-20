namespace PCTool.Core.Framework;

/// <summary>
/// Một vấn đề do một <see cref="ICheck"/> phát hiện. Giao diện chỉ cần vẽ các trường này
/// và hỏi xác nhận, không cần biết lỗi cụ thể là gì.
/// </summary>
public sealed class Finding
{
    /// <summary>Mã ổn định để ghi nhật ký/báo cáo, ví dụ "startup.too-many".</summary>
    public required string Id { get; init; }

    /// <summary>Tên lỗi bằng tiếng Việt dễ hiểu.</summary>
    public required string Title { get; init; }

    /// <summary>Nguyên nhân bằng tiếng Việt dễ hiểu.</summary>
    public required string Cause { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>Các cách sửa người dùng có thể chọn. Rỗng nghĩa là app chỉ báo, không sửa được.</summary>
    public IReadOnlyList<IFix> Fixes { get; init; } = Array.Empty<IFix>();

    /// <summary>Chi tiết kỹ thuật (hiện trong phần thu gọn, để gửi cho người hỗ trợ).</summary>
    public string? TechnicalDetail { get; init; }
}
