using System.ComponentModel;
using System.Text;
using PCTool.Core.Framework;

namespace PCTool.Core.AppInstaller;

/// <summary>Cài một mục trong danh sách cài nhanh, đi qua khung chung (chạy thử / chế độ an toàn / kiểm tra lại).</summary>
public sealed class InstallAppFix : IFix
{
    // Mã thoát của winget (APPINSTALLER_CLI_ERROR_*), ép sang số có dấu như Process.ExitCode.
    public const int WingetAlreadyInstalled = unchecked((int)0x8A150061);
    public const int WingetNoApplicableUpgrade = unchecked((int)0x8A15002B);

    private const int InstallTimeoutSeconds = 15 * 60;

    private readonly AppEntry _entry;
    private readonly IProcessRunner _runner;
    private readonly string _desktopDir;
    private readonly Action<string> _openUrl;

    public InstallAppFix(AppEntry entry, IProcessRunner runner, string desktopDir, Action<string> openUrl)
    {
        var errors = AppEntryValidator.Validate(entry);   // kiểm tra lần nữa trước khi chạy quyền Admin
        if (errors.Count > 0) throw new ArgumentException("Mục cài đặt không hợp lệ: " + errors[0], nameof(entry));
        _entry = entry.Clone();
        _runner = runner;
        _desktopDir = desktopDir;
        _openUrl = openUrl;
    }

    public AppEntry Entry => _entry;

    public string Title => _entry.Kind switch
    {
        AppKind.Winget => $"Cài {_entry.Name}",
        AppKind.WebShortcut => $"Tạo lối tắt {_entry.Name} ra Desktop",
        _ => $"Mở trang tải {_entry.Name}"
    };

    public IReadOnlyList<string> Actions => _entry.Kind switch
    {
        AppKind.Winget => new[] { $"Cài {_entry.Name} bằng winget (ID {_entry.WingetId}), chạy im lặng. Nếu đã có sẵn thì bỏ qua." },
        AppKind.WebShortcut => new[] { $"Tạo lối tắt \"{ShortcutFileName()}\" trên Desktop, mở {_entry.Url}" },
        _ => new[] { $"Mở trang {_entry.Url} bằng trình duyệt. Bạn tự tải và cài." }
    };

    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct) => _entry.Kind switch
    {
        AppKind.Winget => InstallWithWingetAsync(progress, ct),
        AppKind.WebShortcut => Task.FromResult(CreateShortcut()),
        _ => Task.FromResult(OpenDownloadPage())
    };

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        switch (_entry.Kind)
        {
            case AppKind.Winget:
                var r = await _runner.RunAsync("winget", ListArgs(), 120, ct);
                return r.ExitCode == 0;
            case AppKind.WebShortcut:
                return File.Exists(ShortcutPath());
            default:
                return true;   // chỉ mở trang web, không có gì để kiểm tra lại
        }
    }

    // ------------------------------------------------------------------ winget

    /// <summary>Tham số tách riêng từng cái; ID đã qua kiểm tra định dạng nên không thể thành tham số khác.</summary>
    public IReadOnlyList<string> InstallArgs() => new[]
    {
        "install", "--id", _entry.WingetId!, "--exact", "--silent",
        "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
    };

    public IReadOnlyList<string> ListArgs() => new[]
    {
        "list", "--id", _entry.WingetId!, "--exact", "--accept-source-agreements"
    };

    private async Task<FixResult> InstallWithWingetAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Đang cài {_entry.Name} (có thể mất vài phút)...");
        ProcessResult r;
        try
        {
            r = await _runner.RunAsync("winget", InstallArgs(), InstallTimeoutSeconds, ct);
        }
        catch (Win32Exception)
        {
            return FixResult.Failed("Máy chưa có winget. Hãy cài \"App Installer\" từ Microsoft Store rồi thử lại.");
        }

        if (r.ExitCode == 0) return FixResult.Succeeded($"Đã cài {_entry.Name}.");
        if (r.ExitCode is WingetAlreadyInstalled or WingetNoApplicableUpgrade)
            return FixResult.Succeeded($"{_entry.Name} đã có sẵn trên máy, không cần cài lại.");

        return FixResult.Failed($"winget báo lỗi (mã 0x{unchecked((uint)r.ExitCode):X8}). {Tail(r.Output, r.Error)}");
    }

    private static string Tail(string output, string error)
    {
        var text = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        return text.Length <= 300 ? text : text[^300..];
    }

    // ------------------------------------------------------------------ lối tắt / trang tải

    private string ShortcutFileName()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in _entry.Name.Trim()) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var name = sb.ToString().Trim().TrimEnd('.');
        return (name.Length == 0 ? "Lối tắt" : name) + ".url";
    }

    private string ShortcutPath() => Path.Combine(_desktopDir, ShortcutFileName());

    private FixResult CreateShortcut()
    {
        var path = ShortcutPath();
        try
        {
            if (File.Exists(path)) return FixResult.Succeeded($"Lối tắt {_entry.Name} đã có trên Desktop.");
            Directory.CreateDirectory(_desktopDir);
            File.WriteAllText(path, $"[InternetShortcut]\r\nURL={_entry.Url}\r\n");
            return FixResult.Succeeded($"Đã tạo lối tắt {_entry.Name} trên Desktop.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FixResult.Failed("Không tạo được lối tắt: " + ex.Message);
        }
    }

    private FixResult OpenDownloadPage()
    {
        try
        {
            _openUrl(_entry.Url!);
            return FixResult.Succeeded($"Đã mở trang tải {_entry.Name}. Hãy tải và cài thủ công.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return FixResult.Failed("Không mở được trình duyệt: " + ex.Message);
        }
    }
}

/// <summary>Kiểm tra máy có winget không; không có thì hướng dẫn (app không tự cài winget).</summary>
public sealed class WingetCheck : ICheck
{
    private readonly IProcessRunner _runner;

    public WingetCheck(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    public string Id => "apps.winget";
    public string Name => "Kiểm tra winget (công cụ cài ứng dụng)";

    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken ct)
    {
        try
        {
            var r = await _runner.RunAsync("winget", new[] { "--version" }, 30, ct);
            if (r.ExitCode == 0) return Array.Empty<Finding>();
        }
        catch (Win32Exception) { }

        return new[]
        {
            new Finding
            {
                Id = "apps.winget-missing", Severity = Severity.Warning,
                Title = "Máy chưa có winget",
                Cause = "Không cài nhanh được ứng dụng. Hãy mở Microsoft Store, tìm \"App Installer\" và cập nhật/cài, rồi quét lại."
            }
        };
    }
}
