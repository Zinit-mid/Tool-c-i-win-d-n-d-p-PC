using System.Text.Json;
using System.Text.RegularExpressions;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Uninstall;

/// <summary>Một ứng dụng Microsoft Store (gói MSIX/Appx) của tài khoản đang dùng.</summary>
public sealed record StoreApp(string Name, string PackageFullName, string Version, string Publisher,
    bool IsFramework, bool NonRemovable, string SignatureKind, bool IsResource)
{
    private static readonly Regex Cn = new(@"CN=([^,]+)", RegexOptions.Compiled);

    /// <summary>Tên nhà phát hành dễ đọc (lấy phần CN=...).</summary>
    public string PublisherText
    {
        get
        {
            var m = Cn.Match(Publisher);
            return (m.Success ? m.Groups[1].Value : Publisher).Trim('"', ' ');
        }
    }
}

/// <summary>Đọc danh sách ứng dụng Store (CHỈ ĐỌC).</summary>
public static class StoreAppsScanner
{
    public const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$out = @(Get-AppxPackage | ForEach-Object {
  [pscustomobject]@{
    name = [string]$_.Name; full = [string]$_.PackageFullName; version = [string]$_.Version; publisher = [string]$_.Publisher
    framework = [bool]$_.IsFramework; nonremovable = [bool]$_.NonRemovable; sig = [string]$_.SignatureKind; resource = [bool]$_.IsResourcePackage
  }
})
@($out) | ConvertTo-Json -Compress -Depth 3
""";

    public static async Task<IReadOnlyList<StoreApp>> RunAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c);
        var r = await run(Script, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("Không đọc được danh sách ứng dụng Store: " + r.Error.Trim());
        return Parse(r.Output);
    }

    public static IReadOnlyList<StoreApp> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<StoreApp>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidOperationException("Không đọc được danh sách ứng dụng Store (kết quả lạ)."); }
        using (doc)
        {
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
                      : doc.RootElement.ValueKind == JsonValueKind.Object ? new List<JsonElement> { doc.RootElement }
                      : new List<JsonElement>();
            var list = new List<StoreApp>();
            foreach (var e in items)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var name = Str(e, "name").Trim();
                var full = Str(e, "full").Trim();
                if (name.Length == 0 || full.Length == 0) continue;
                list.Add(new StoreApp(name, full, Str(e, "version"), Str(e, "publisher"),
                    Bool(e, "framework"), Bool(e, "nonremovable"), Str(e, "sig"), Bool(e, "resource")));
            }
            return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Quyết định ứng dụng Store nào KHÔNG cho gỡ (lõi Windows, thư viện, gói hệ thống).</summary>
public static class StoreAppPolicy
{
    // Tên gói của thành phần Windows: gỡ có thể làm hỏng Start, tìm kiếm, Store, cài đặt, bảo mật...
    private static readonly Regex Protected = new(
        @"^(Microsoft\.(Windows\.|WindowsStore|StorePurchaseApp|DesktopAppInstaller|VCLibs|NET\.|UI\.Xaml|SecHealthUI|AAD\.|AccountsControl|LockApp|CredDialogHost|ECApp|BioEnrollment|AsyncTextService|XboxIdentityProvider|Services\.Store|WindowsAppRuntime|WebView|MicrosoftEdge|Win32WebViewHost|Edge)|Windows\.|MicrosoftWindows\.|NcsiUwpApp|[0-9a-f]{8}-[0-9a-f]{4}-)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Tên đầy đủ gói: Tên_PhiênBản_KiếnTrúc_MãNgônNgữ_MãNhàPhátHành(13 ký tự). Không có dấu nháy/khoảng trắng nên không thể chèn lệnh.
    private static readonly Regex FullName = new(@"^[A-Za-z0-9.\-]+_[0-9.]+_[A-Za-z0-9]+_[A-Za-z0-9.\-]*_[a-z0-9]{13}$", RegexOptions.Compiled);

    public static bool IsValidFullName(string? s) => s is { Length: > 0 and <= 200 } && FullName.IsMatch(s);

    public static UninstallVerdict Evaluate(StoreApp a)
    {
        if (!IsValidFullName(a.PackageFullName)) return new(false, "Tên gói không hợp lệ.", null);
        if (a.IsFramework) return new(false, "Thư viện dùng chung của Windows/ứng dụng, không cho gỡ.", null);
        if (a.IsResource) return new(false, "Gói tài nguyên (ngôn ngữ...), không cho gỡ riêng.", null);
        if (a.NonRemovable) return new(false, "Windows đánh dấu không thể gỡ.", null);
        if (string.Equals(a.SignatureKind, "System", StringComparison.OrdinalIgnoreCase)) return new(false, "Gói hệ thống của Windows, không cho gỡ.", null);
        if (Protected.IsMatch(a.Name)) return new(false, "Thành phần lõi của Windows (Start, Store, Edge, cài đặt, bảo mật...), gỡ có thể làm hỏng máy.", null);
        return new(true, "", "Gỡ cho tài khoản Windows đang dùng. Có thể cài lại từ Microsoft Store.");
    }
}

/// <summary>Gỡ một ứng dụng Store (Remove-AppxPackage) qua khung chung: chạy thử, điểm khôi phục, kiểm tra lại.</summary>
public sealed class StoreAppFix : IFix
{
    private readonly StoreApp _app;
    private readonly ScriptRunner? _run;

    public StoreAppFix(StoreApp app, ScriptRunner? run = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var v = StoreAppPolicy.Evaluate(app);   // kiểm tra lần nữa trước khi chạy quyền Admin
        if (!v.Allowed) throw new ArgumentException(v.Reason, nameof(app));
        _app = app;
        _run = run;
    }

    public string Script =>
        $"try {{ Remove-AppxPackage -Package '{_app.PackageFullName}' -ErrorAction Stop }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}";

    public string Title => $"Gỡ ứng dụng Store {_app.Name}";

    public IReadOnlyList<string> Actions => new[]
    {
        $"Gỡ ứng dụng Store \"{_app.Name}\" {_app.Version} (gói {_app.PackageFullName}) khỏi tài khoản Windows đang dùng",
        "Dữ liệu của ứng dụng trong tài khoản này sẽ mất",
        "Có thể cài lại từ Microsoft Store",
        "Sau đó đọc lại danh sách để kiểm tra gói đã biến mất chưa"
    };

    public bool RequiresRestorePoint => true;
    public bool IsUndoable => false;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Đang gỡ {_app.Name}...");
        var run = _run ?? ((s, c) => PowerShellRunner.RunAsync(s, c));
        var r = await run(Script, ct);
        if (r.ExitCode != 0)
        {
            var err = r.Error.Trim();
            return FixResult.Failed("Không gỡ được: " + (err.Length <= 300 ? err : err[^300..]));
        }

        return FixResult.Succeeded($"Đã gỡ {_app.Name}.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var now = await StoreAppsScanner.RunAsync(_run, ct);
        return !now.Any(a => string.Equals(a.PackageFullName, _app.PackageFullName, StringComparison.OrdinalIgnoreCase));
    }
}
