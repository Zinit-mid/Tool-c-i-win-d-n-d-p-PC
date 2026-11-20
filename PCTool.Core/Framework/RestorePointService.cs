using System.Text;
using PCTool.Core.Windows;

namespace PCTool.Core.Framework;

/// <summary>Tạo điểm khôi phục Windows bằng Checkpoint-Computer (cần quyền Administrator).</summary>
public sealed class RestorePointService : IRestorePointService
{
    private readonly Func<string, CancellationToken, Task<PowerShellResult>> _run;

    public RestorePointService()
        : this((script, ct) => PowerShellRunner.RunAsync(script, ct, timeoutSeconds: 180))
    {
    }

    /// <summary>Dùng cho kiểm thử: truyền hàm chạy PowerShell giả.</summary>
    public RestorePointService(Func<string, CancellationToken, Task<PowerShellResult>> run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
    {
        try
        {
            var result = await _run(BuildScript(description), ct);
            return ParseOutput(result.Output, result.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Lỗi khi tạo điểm khôi phục: {ex}");
            return new RestorePointResult(RestorePointStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Chỉ giữ chữ, số, khoảng trắng và vài dấu an toàn. Mô tả được đặt trong dấu nháy đơn của PowerShell,
    /// nên loại bỏ dấu nháy, dấu chấm phẩy, ký tự $ và ký tự điều khiển để không thể chèn lệnh.
    /// </summary>
    public static string Sanitize(string? description)
    {
        var sb = new StringBuilder();
        foreach (var c in description ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or ':' or '(' or ')')
                sb.Append(c);
        }

        var text = sb.ToString().Trim();
        if (text.Length > 100) text = text[..100].TrimEnd();
        return text.Length == 0 ? "PCTool" : text;
    }

    public static string BuildScript(string description)
    {
        var safe = Sanitize(description);
        return
            "$ErrorActionPreference = 'Stop'\n" +
            "try {\n" +
            "  $before = (Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Measure-Object -Property SequenceNumber -Maximum).Maximum\n" +
            $"  Checkpoint-Computer -Description '{safe}' -RestorePointType 'MODIFY_SETTINGS' -WarningAction SilentlyContinue\n" +
            "  $after = (Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Measure-Object -Property SequenceNumber -Maximum).Maximum\n" +
            "  if ($after -ne $before) { 'CREATED' } else { 'SKIPPED' }\n" +
            "} catch { 'FAILED:' + $_.Exception.Message }\n";
    }

    public static RestorePointResult ParseOutput(string? output, string? error = null)
    {
        var lastLine = (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;

        if (lastLine.Equals("CREATED", StringComparison.Ordinal))
            return new RestorePointResult(RestorePointStatus.Created, "Đã tạo điểm khôi phục.");

        if (lastLine.Equals("SKIPPED", StringComparison.Ordinal))
            return new RestorePointResult(
                RestorePointStatus.Skipped,
                "Windows không tạo thêm điểm khôi phục mới (mặc định chỉ cho 1 điểm mỗi 24 giờ), nên dùng điểm gần nhất.");

        if (lastLine.StartsWith("FAILED:", StringComparison.Ordinal))
            return new RestorePointResult(RestorePointStatus.Failed, lastLine["FAILED:".Length..].Trim());

        var detail = string.IsNullOrWhiteSpace(error) ? "không rõ nguyên nhân" : error.Trim();
        return new RestorePointResult(RestorePointStatus.Failed, $"Không đọc được kết quả tạo điểm khôi phục ({detail}).");
    }
}
