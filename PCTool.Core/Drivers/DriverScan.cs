using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Drivers;

/// <param name="Code">Mã lỗi của Device Manager (28 = chưa có driver, 22 = đang bị tắt...).</param>
public sealed record DriverProblem(string Name, string DeviceClass, int Code, string DeviceId)
{
    /// <summary>Thiết bị bị tắt (mã 22) là do người dùng/hệ thống chủ động tắt, không phải thiếu driver.</summary>
    public bool NeedsDriver => Code != 22;

    public string Meaning => Code switch
    {
        28 => "Chưa có driver",
        22 => "Đang bị tắt (không phải thiếu driver)",
        1 => "Chưa cấu hình đúng, thường do thiếu driver",
        10 => "Không khởi động được (driver lỗi hoặc không hợp)",
        31 => "Windows không nạp được driver",
        43 => "Windows đã dừng thiết bị vì nó báo lỗi",
        _ => $"Lỗi mã {Code}"
    };
}

public sealed record DriverScanResult(string Maker, string Model, IReadOnlyList<DriverProblem> Problems)
{
    public int MissingCount => Problems.Count(p => p.NeedsDriver);
}

public static class DriverScanner
{
    /// <summary>Chỉ đọc: liệt kê thiết bị Windows đang báo lỗi và hãng/mẫu máy.</summary>
    public const string Script = """
$ErrorActionPreference = 'Stop'
$cs = Get-CimInstance Win32_ComputerSystem
$bad = @(Get-CimInstance Win32_PnPEntity -Filter "ConfigManagerErrorCode <> 0" | ForEach-Object {
  [ordered]@{ name = [string]$_.Name; cls = [string]$_.PNPClass; code = [int]$_.ConfigManagerErrorCode; id = [string]$_.PNPDeviceID }
})
[ordered]@{ maker = [string]$cs.Manufacturer; model = [string]$cs.Model; problems = $bad } | ConvertTo-Json -Compress -Depth 4
""";

    public static DriverScanResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString()) : "";
        var list = new List<DriverProblem>();
        if (root.TryGetProperty("problems", out var arr))
        {
            IEnumerable<JsonElement> items = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : arr.ValueKind == JsonValueKind.Object ? new[] { arr } : Array.Empty<JsonElement>();
            foreach (var p in items)
            {
                int code = p.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
                var name = S(p, "name");
                list.Add(new DriverProblem(name.Length == 0 ? "(thiết bị không tên)" : name, S(p, "cls"), code, S(p, "id")));
            }
        }
        return new DriverScanResult(S(root, "maker"), S(root, "model"), list);
    }

    private static string Clean(string? s) => new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();

    public static async Task<DriverScanResult> ScanAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c, 60);
        var r = await run(Script, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được danh sách thiết bị." : r.Error.Trim());
        return Parse(r.Output);
    }
}

/// <summary>Tìm và cài driver từ Windows Update (driver có chữ ký Microsoft). Bỏ qua driver đòi chấp nhận điều khoản hoặc cần người dùng nhập.</summary>
public sealed class WindowsUpdateDriverFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly ScriptRunner? _scan;
    private int _before;

    public WindowsUpdateDriverFix(ScriptRunner? run = null, ScriptRunner? scanRun = null)
    {
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 3600));
        _scan = scanRun;
    }

    public string Title => "Cài driver từ Windows Update";
    public IReadOnlyList<string> Actions => new[]
    {
        "Tìm các bản cập nhật DRIVER còn thiếu trên Windows Update (cần Internet)",
        "Tải và cài; bỏ qua driver đòi chấp nhận điều khoản hoặc cần bạn nhập thông tin",
        "Có thể mất vài phút đến vài chục phút, một số driver cần khởi động lại máy",
        "Tạo điểm khôi phục trước khi cài"
    };
    public bool RequiresRestorePoint => true;
    public bool IsUndoable => false;

    public const string Script = """
$ErrorActionPreference = 'Stop'
$s = New-Object -ComObject Microsoft.Update.Session
$s.ClientApplicationID = 'PCTool'
$r = $s.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
$found = [int]$r.Updates.Count
$c = New-Object -ComObject Microsoft.Update.UpdateColl
$skipped = 0
foreach ($u in $r.Updates) {
  if ($u.EulaAccepted -and -not $u.InstallationBehavior.CanRequestUserInput) { [void]$c.Add($u) } else { $skipped++ }
}
$installed = 0; $failed = 0; $reboot = $false; $titles = @()
if ($c.Count -gt 0) {
  $d = $s.CreateUpdateDownloader(); $d.Updates = $c; [void]$d.Download()
  $i = $s.CreateUpdateInstaller(); $i.Updates = $c
  $res = $i.Install()
  $reboot = [bool]$res.RebootRequired
  for ($k = 0; $k -lt $c.Count; $k++) {
    if ($res.GetUpdateResult($k).ResultCode -eq 2) { $installed++; $titles += [string]$c.Item($k).Title } else { $failed++ }
  }
}
[ordered]@{ found = $found; skipped = [int]$skipped; installed = [int]$installed; failed = [int]$failed; reboot = $reboot; titles = @($titles) } | ConvertTo-Json -Compress -Depth 4
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang đếm thiết bị thiếu driver...");
        _before = (await DriverScanner.ScanAsync(_scan, ct)).MissingCount;
        progress.Report("Đang tìm và cài driver từ Windows Update (có thể lâu)...");
        var r = await _run(Script, ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Windows Update không trả kết quả (kiểm tra Internet)." : r.Error.Trim());
        try
        {
            using var doc = JsonDocument.Parse(r.Output);
            var e = doc.RootElement;
            int Get(string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out var x) ? x : 0;
            bool reboot = e.TryGetProperty("reboot", out var rb) && rb.ValueKind == JsonValueKind.True;
            int found = Get("found"), ok = Get("installed"), failed = Get("failed"), skipped = Get("skipped");
            if (found == 0) return FixResult.Succeeded("Windows Update không có driver mới nào cho máy này.");
            var msg = $"Tìm thấy {found} driver, cài được {ok}, lỗi {failed}, bỏ qua {skipped}." + (reboot ? " Cần KHỞI ĐỘNG LẠI máy để hoàn tất." : "");
            return failed > 0 && ok == 0 ? FixResult.Failed(msg) : FixResult.Succeeded(msg);
        }
        catch (JsonException) { return FixResult.Failed("Kết quả từ Windows Update không đọc được."); }
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var after = (await DriverScanner.ScanAsync(_scan, ct)).MissingCount;
        return after == 0 || after < _before;
    }
}

/// <summary>Trang hỗ trợ/tải driver chính thức của hãng. PCTool chỉ MỞ trang bằng trình duyệt, bạn tự tải.</summary>
public static class DriverVendorPages
{
    public sealed record Page(string Title, string Url);

    private static readonly (string Key, Page Page)[] Makers =
    {
        ("dell", new("Dell: hỗ trợ và driver", "https://www.dell.com/support/home/")),
        ("hewlett", new("HP: hỗ trợ và driver", "https://support.hp.com/drivers")),
        ("hp", new("HP: hỗ trợ và driver", "https://support.hp.com/drivers")),
        ("lenovo", new("Lenovo: hỗ trợ và driver", "https://support.lenovo.com/")),
        ("asus", new("ASUS: hỗ trợ và driver", "https://www.asus.com/support/")),
        ("acer", new("Acer: hỗ trợ và driver", "https://www.acer.com/support")),
        ("micro-star", new("MSI: hỗ trợ và driver", "https://www.msi.com/support")),
        ("msi", new("MSI: hỗ trợ và driver", "https://www.msi.com/support")),
        ("gigabyte", new("Gigabyte: hỗ trợ và driver", "https://www.gigabyte.com/Support")),
    };

    public static IReadOnlyList<Page> Common { get; } = new Page[]
    {
        new("Intel: tự phát hiện driver (Intel Driver & Support Assistant)", "https://www.intel.com/content/www/us/en/support/detect.html"),
        new("NVIDIA: driver card đồ họa", "https://www.nvidia.com/Download/index.aspx"),
        new("AMD: driver card đồ họa/chipset", "https://www.amd.com/en/support/download/drivers.html"),
    };

    public static IReadOnlyList<Page> ForMaker(string? maker)
    {
        var m = (maker ?? "").ToLowerInvariant();
        var list = new List<Page>();
        foreach (var (key, page) in Makers)
            if (m.Contains(key) && !list.Contains(page)) list.Add(page);
        list.AddRange(Common);
        return list;
    }

    public static bool IsAllowed(string? url) =>
        url is not null && (Common.Any(p => p.Url == url) || Makers.Any(x => x.Page.Url == url));

    public static void Open(string url, Action<string> opener)
    {
        if (!IsAllowed(url)) throw new ArgumentException("Chỉ mở trang hỗ trợ chính thức đã định sẵn.", nameof(url));
        opener(url);
    }
}
