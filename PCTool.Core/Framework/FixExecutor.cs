namespace PCTool.Core.Framework;

public sealed class FixOptions
{
    /// <summary>Chạy thử: chỉ ghi nhật ký, KHÔNG thay đổi gì. Mặc định bật cho an toàn.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Chế độ an toàn (máy công ty): khóa mọi thao tác sửa.</summary>
    public bool SafeMode { get; init; }

    /// <summary>Có tạo điểm khôi phục trước khi sửa (nếu cách sửa yêu cầu) không.</summary>
    public bool CreateRestorePoint { get; init; } = true;
}

/// <summary>
/// Cổng duy nhất để chạy một <see cref="IFix"/>. Luồng: Chế độ an toàn → chạy thử →
/// điểm khôi phục → sửa → kiểm tra lại → ghi nhật ký. Mọi quy tắc an toàn nằm ở đây
/// nên từng cách sửa không thể "quên" tuân thủ.
/// </summary>
public sealed class FixExecutor
{
    private readonly IRestorePointService _restorePoints;

    public FixExecutor(IRestorePointService restorePoints)
    {
        _restorePoints = restorePoints ?? throw new ArgumentNullException(nameof(restorePoints));
    }

    public async Task<FixResult> RunAsync(
        IFix fix, FixOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(options);

        void Say(string text)
        {
            Logger.Info(text);
            progress?.Report(text);
        }

        // 1. Chế độ an toàn khóa tất cả, kể cả chạy thử.
        if (options.SafeMode)
        {
            Logger.Warn($"Chế độ an toàn đang bật, không chạy: {fix.Title}");
            return FixResult.Blocked("Đã khóa vì Chế độ an toàn đang bật.");
        }

        // 2. Chạy thử: KHÔNG gọi ExecuteAsync, chỉ liệt kê việc sẽ làm. Nhờ vậy chạy thử luôn an toàn
        //    dù từng cách sửa được viết thế nào.
        if (options.DryRun)
        {
            Say($"[CHẠY THỬ] {fix.Title}");
            foreach (var action in fix.Actions)
                Say($"[CHẠY THỬ] Sẽ làm: {action}");
            return FixResult.Simulated("Chạy thử xong, máy chưa bị thay đổi gì.");
        }

        FixResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            Say($"Bắt đầu: {fix.Title}");

            // 3. Điểm khôi phục.
            if (fix.RequiresRestorePoint && options.CreateRestorePoint)
            {
                var rp = await _restorePoints.CreateAsync($"PCTool: {fix.Title}", ct);
                switch (rp.Status)
                {
                    case RestorePointStatus.Created:
                        Say("Đã tạo điểm khôi phục Windows.");
                        break;
                    case RestorePointStatus.Skipped:
                        Logger.Warn($"Bỏ qua điểm khôi phục: {rp.Message}");
                        progress?.Report($"Chưa tạo điểm khôi phục mới: {rp.Message}");
                        break;
                    default:
                        if (!fix.IsUndoable)
                        {
                            Logger.Error($"Không tạo được điểm khôi phục, dừng: {rp.Message}");
                            return FixResult.Failed(
                                "Chưa sửa gì cả vì không tạo được điểm khôi phục, mà cách sửa này không hoàn tác được. " +
                                $"Chi tiết: {rp.Message}");
                        }
                        Logger.Warn($"Không tạo được điểm khôi phục, vẫn tiếp tục vì cách sửa này hoàn tác được: {rp.Message}");
                        progress?.Report("Không tạo được điểm khôi phục, nhưng cách sửa này hoàn tác được nên vẫn tiếp tục.");
                        break;
                }
            }

            // 4. Sửa thật.
            result = await fix.ExecuteAsync(progress ?? NullProgress.Instance, ct);
        }
        catch (OperationCanceledException)
        {
            Logger.Warn($"Đã dừng: {fix.Title}");
            return FixResult.Cancelled("Đã dừng theo yêu cầu. Việc đang làm dở có thể chưa xong, hãy quét lại để kiểm tra.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Lỗi khi sửa '{fix.Title}': {ex}");
            return FixResult.Failed($"Không sửa được: {ex.Message}");
        }

        if (result.Outcome != FixOutcome.Succeeded)
        {
            Logger.Warn($"Kết quả '{fix.Title}': {result.Outcome} - {result.Message}");
            return result;
        }

        // 5. Kiểm tra lại: chỉ khi chắc chắn mới báo "đã hết lỗi".
        try
        {
            if (await fix.VerifyAsync(ct))
            {
                Say($"Đã sửa và kiểm tra lại: {fix.Title}");
                return result;
            }

            Logger.Warn($"Sửa xong nhưng kiểm tra lại vẫn còn lỗi: {fix.Title}");
            return FixResult.StillPresent("Đã chạy xong nhưng kiểm tra lại thấy vấn đề vẫn còn. Bạn thử khởi động lại máy rồi quét lại.");
        }
        catch (OperationCanceledException)
        {
            return FixResult.Unverified("Đã sửa nhưng bị dừng trước khi kiểm tra lại, hãy quét lại để chắc chắn.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Không kiểm tra lại được '{fix.Title}': {ex}");
            return FixResult.Unverified($"Đã sửa nhưng chưa kiểm tra lại được ({ex.Message}). Hãy quét lại để chắc chắn.");
        }
    }

    private sealed class NullProgress : IProgress<string>
    {
        public static readonly NullProgress Instance = new();

        public void Report(string value)
        {
        }
    }
}
