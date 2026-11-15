using System.Diagnostics;
using System.Text;

namespace PCTool.Services;

public sealed record PowerShellResult(int ExitCode, string Output, string Error);

/// <summary>
/// Chạy một đoạn PowerShell (Windows PowerShell 5.1 có sẵn trên mọi máy Win 10/11), ẩn cửa sổ.
/// Dùng -EncodedCommand để không phải lo escape dấu nháy/tiếng Việt.
/// </summary>
public static class PowerShellRunner
{
    public static async Task<PowerShellResult> RunAsync(string script, CancellationToken ct, int timeoutSeconds = 90)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Không khởi động được PowerShell.");

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* đã thoát */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"PowerShell chạy quá {timeoutSeconds} giây nên đã bị dừng.");
        }

        return new PowerShellResult(process.ExitCode, await outTask, await errTask);
    }
}
