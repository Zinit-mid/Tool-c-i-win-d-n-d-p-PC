namespace PCTool.Core.Framework;

public sealed record ScanProgress(int Index, int Total, string CheckName);

/// <summary>Một phép kiểm tra bị lỗi khi chạy (không làm hỏng cả đợt quét).</summary>
public sealed record CheckFailure(string CheckId, string Message);

public sealed class ScanReport
{
    /// <summary>Các vấn đề tìm thấy, nặng nhất lên đầu.</summary>
    public required IReadOnlyList<Finding> Findings { get; init; }

    /// <summary>Các phép kiểm tra không chạy được. Giao diện nên báo để người dùng biết kết quả chưa đầy đủ.</summary>
    public required IReadOnlyList<CheckFailure> Failures { get; init; }
}

public static class ScanRunner
{
    public static async Task<ScanReport> RunAsync(
        IReadOnlyList<ICheck> checks, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checks);

        var findings = new List<Finding>();
        var failures = new List<CheckFailure>();

        for (int i = 0; i < checks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var check = checks[i];
            progress?.Report(new ScanProgress(i, checks.Count, check.Name));
            Logger.Info($"Đang kiểm tra: {check.Name}");

            try
            {
                findings.AddRange(await check.RunAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Một phép kiểm tra lỗi không được làm hỏng cả đợt quét.
                Logger.Error($"Kiểm tra '{check.Name}' bị lỗi: {ex}");
                failures.Add(new CheckFailure(check.Id, ex.Message));
            }
        }

        return new ScanReport
        {
            Findings = findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Title, StringComparer.CurrentCulture)
                .ToList(),
            Failures = failures
        };
    }
}
