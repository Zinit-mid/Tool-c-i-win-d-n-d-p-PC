namespace PCTool.Core.Framework;

/// <summary>
/// Một phép kiểm tra chỉ ĐỌC thông tin (không được thay đổi máy) và trả về các vấn đề tìm thấy.
/// </summary>
public interface ICheck
{
    /// <summary>Mã ổn định, ví dụ "network.dns".</summary>
    string Id { get; }

    /// <summary>Tên hiển thị tiếng Việt, ví dụ "Kiểm tra mạng".</summary>
    string Name { get; }

    Task<IReadOnlyList<Finding>> RunAsync(CancellationToken ct);
}
