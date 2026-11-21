using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Printer;

/// <summary>Khởi động lại dịch vụ in (Print Spooler). Các lệnh in đang chạy có thể bị gián đoạn.</summary>
public sealed class RestartSpoolerFix : IFix
{
    public const string Script =
        "$ErrorActionPreference='Stop'; Set-Service -Name Spooler -StartupType Automatic; Restart-Service -Name Spooler -Force";

    public const string StatusScript = "(Get-Service -Name Spooler).Status.ToString()";

    private readonly ScriptRunner _run;

    public RestartSpoolerFix(ScriptRunner? run = null) =>
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct));

    public string Title => "Khởi động lại dịch vụ in (Print Spooler)";

    public IReadOnlyList<string> Actions => new[]
    {
        "Đặt Print Spooler tự khởi động cùng Windows",
        "Khởi động lại Print Spooler (lệnh in đang chạy có thể bị gián đoạn)"
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang khởi động lại Print Spooler...");
        var r = await _run(Script, ct);
        return r.ExitCode == 0
            ? FixResult.Succeeded("Đã khởi động lại Print Spooler.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Windows không cho khởi động lại Spooler." : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        (await _run(StatusScript, ct)).Output.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Xóa toàn bộ lệnh in đang chờ của mọi máy in. Tài liệu đang chờ sẽ mất, phải in lại.</summary>
public sealed class ClearPrintQueueFix : IFix
{
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $n = 0
        try {
            Stop-Service -Name Spooler -Force
            $dir = Join-Path $env:SystemRoot 'System32\spool\PRINTERS'
            $files = @(Get-ChildItem -Path $dir -File -Force)
            $n = $files.Count
            $files | Remove-Item -Force
        }
        finally {
            Start-Service -Name Spooler -ErrorAction SilentlyContinue
        }
        Write-Output $n
        """;

    public const string VerifyScript = """
        $dir = Join-Path $env:SystemRoot 'System32\spool\PRINTERS'
        $left = @(Get-ChildItem -Path $dir -File -Force -ErrorAction SilentlyContinue).Count
        $st = (Get-Service -Name Spooler).Status.ToString()
        Write-Output ($st + ':' + $left)
        """;

    private readonly ScriptRunner _run;

    public ClearPrintQueueFix(ScriptRunner? run = null) =>
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct));

    public string Title => "Xóa hàng đợi in";

    public IReadOnlyList<string> Actions => new[]
    {
        "Tắt Print Spooler",
        "Xóa TOÀN BỘ lệnh in đang chờ của mọi máy in (tài liệu đang chờ sẽ mất, phải in lại)",
        "Bật lại Print Spooler"
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang xóa hàng đợi in...");
        var r = await _run(Script, ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không xóa được hàng đợi in." : r.Error.Trim());

        int.TryParse(r.Output.Trim(), out var count);
        return FixResult.Succeeded($"Đã xóa hàng đợi in ({count} file) và bật lại Print Spooler.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var parts = (await _run(VerifyScript, ct)).Output.Trim().Split(':');
        return parts.Length == 2
            && parts[0].Equals("Running", StringComparison.OrdinalIgnoreCase)
            && parts[1] == "0";
    }
}

/// <summary>Đổi báo cáo chẩn đoán máy in sang vấn đề của khung chung và gắn cách sửa phù hợp.</summary>
public static class PrinterFindingMapper
{
    public static IReadOnlyList<Framework.Finding> Map(PrinterReport report, Func<IFix> restart, Func<IFix> clearQueue)
    {
        var list = new List<Framework.Finding>();
        int n = 0;
        foreach (var f in report.Findings)
        {
            if (f.Level == "Ok") continue;   // "ổn" không phải vấn đề

            var fixes = f.Code switch
            {
                FindingCodes.SpoolerStopped => new[] { restart() },
                FindingCodes.QueueStuck => new[] { clearQueue() },
                _ => Array.Empty<IFix>()
            };

            list.Add(new Framework.Finding
            {
                Id = f.Code.Length > 0 ? f.Code : $"printer.finding-{++n}",
                Title = f.Printer.Length > 0 ? $"{f.Printer}: {f.Problem}" : f.Problem,
                Cause = f.Advice,
                Severity = f.Level switch
                {
                    "Error" => Severity.Critical,
                    "Warn" => Severity.Warning,
                    _ => Severity.Info
                },
                Fixes = fixes
            });
        }
        return list;
    }
}

public sealed class PrinterCheck : ICheck
{
    private readonly Func<CancellationToken, Task<PrinterReport>> _diagnose;

    public PrinterCheck() : this(PrinterDiagnostics.RunAsync) { }

    public PrinterCheck(Func<CancellationToken, Task<PrinterReport>> diagnose) => _diagnose = diagnose;

    public string Id => "printer.diagnose";
    public string Name => "Chẩn đoán máy in";

    public async Task<IReadOnlyList<Framework.Finding>> RunAsync(CancellationToken ct) =>
        PrinterFindingMapper.Map(await _diagnose(ct), () => new RestartSpoolerFix(), () => new ClearPrintQueueFix());
}
