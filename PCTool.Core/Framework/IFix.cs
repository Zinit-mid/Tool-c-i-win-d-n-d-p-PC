namespace PCTool.Core.Framework;

/// <summary>
/// Một cách sửa lỗi. Chỉ được chạy qua <see cref="FixExecutor"/> (hỏi xác nhận, chạy thử,
/// điểm khôi phục, kiểm tra lại đều do FixExecutor lo).
/// </summary>
public interface IFix
{
    /// <summary>Tên việc sẽ làm, ví dụ "Tắt 5 ứng dụng khởi động cùng Windows".</summary>
    string Title { get; }

    /// <summary>
    /// Danh sách việc CỤ THỂ sẽ làm, hiện trong hộp xác nhận và nhật ký chạy thử.
    /// Phải đầy đủ và đúng, vì người dùng dựa vào đây để đồng ý.
    /// </summary>
    IReadOnlyList<string> Actions { get; }

    /// <summary>Có nên tạo điểm khôi phục Windows trước khi sửa không.</summary>
    bool RequiresRestorePoint { get; }

    /// <summary>Sửa xong có hoàn tác được không (ví dụ xóa file tạm thì không).</summary>
    bool IsUndoable { get; }

    /// <summary>Thực hiện thật. Không bao giờ được gọi khi đang chạy thử hay Chế độ an toàn.</summary>
    Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct);

    /// <summary>Kiểm tra lại sau khi sửa: true nghĩa là vấn đề đã hết.</summary>
    Task<bool> VerifyAsync(CancellationToken ct);
}
