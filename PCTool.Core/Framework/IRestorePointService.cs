namespace PCTool.Core.Framework;

public enum RestorePointStatus
{
    /// <summary>Đã tạo điểm khôi phục mới.</summary>
    Created,

    /// <summary>Không tạo mới nhưng không phải lỗi (ví dụ Windows đã có điểm khôi phục trong 24 giờ qua).</summary>
    Skipped,

    /// <summary>Không tạo được (ví dụ Bảo vệ hệ thống đang tắt).</summary>
    Failed
}

public sealed record RestorePointResult(RestorePointStatus Status, string Message);

public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken ct);
}
