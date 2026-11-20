namespace PCTool.Core.Framework;

public enum FixOutcome
{
    /// <summary>Đã sửa và kiểm tra lại: vấn đề đã hết.</summary>
    Succeeded,

    /// <summary>Chạy thử: máy không bị thay đổi.</summary>
    Simulated,

    /// <summary>Bị khóa (Chế độ an toàn).</summary>
    Blocked,

    /// <summary>Không sửa được.</summary>
    Failed,

    /// <summary>Người dùng dừng giữa chừng.</summary>
    Cancelled,

    /// <summary>Đã chạy xong nhưng kiểm tra lại thấy vấn đề vẫn còn.</summary>
    StillPresent,

    /// <summary>Đã chạy nhưng chưa kiểm tra lại được, nên chưa dám nói là đã hết lỗi.</summary>
    Unverified
}

public sealed record FixResult(FixOutcome Outcome, string Message)
{
    /// <summary>Có thể coi là "không có sự cố" (đã sửa xong hoặc chỉ chạy thử).</summary>
    public bool IsSuccess => Outcome is FixOutcome.Succeeded or FixOutcome.Simulated;

    public static FixResult Succeeded(string message) => new(FixOutcome.Succeeded, message);
    public static FixResult Simulated(string message) => new(FixOutcome.Simulated, message);
    public static FixResult Blocked(string message) => new(FixOutcome.Blocked, message);
    public static FixResult Failed(string message) => new(FixOutcome.Failed, message);
    public static FixResult Cancelled(string message) => new(FixOutcome.Cancelled, message);
    public static FixResult StillPresent(string message) => new(FixOutcome.StillPresent, message);
    public static FixResult Unverified(string message) => new(FixOutcome.Unverified, message);
}
