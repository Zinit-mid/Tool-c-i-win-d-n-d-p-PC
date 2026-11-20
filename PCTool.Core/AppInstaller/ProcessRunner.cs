using System.Diagnostics;
using System.Text;

namespace PCTool.Core.AppInstaller;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Chạy một chương trình với danh sách tham số RIÊNG TỪNG CÁI (không ghép chuỗi lệnh, không qua shell).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Không khởi động được {fileName}.");

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
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, "", $"Quá thời gian chờ ({timeoutSeconds} giây).");
        }

        return new ProcessResult(process.ExitCode, await outTask, await errTask);
    }
}
