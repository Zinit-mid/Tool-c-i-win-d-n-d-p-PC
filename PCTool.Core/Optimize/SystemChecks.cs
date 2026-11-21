using System.Globalization;
using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Optimize;

/// <summary>Đọc JSON do PowerShell trả về, chịu được thiếu trường và mảng 1 phần tử bị biến thành đối tượng.</summary>
internal static class Js
{
    public static JsonElement Root(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("Không đọc được dữ liệu từ máy (PowerShell không trả về gì).");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Dữ liệu từ máy không đúng định dạng.");
            return root;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Dữ liệu từ máy không đúng định dạng.", ex);
        }
    }

    public static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in v.EnumerateArray()) if (i.ValueKind == JsonValueKind.Object) yield return i;
        }
        else if (v.ValueKind == JsonValueKind.Object) yield return v;
    }

    public static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim() : "";

    public static double Num(JsonElement e, string name, double fallback = 0)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String &&
               double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    public static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}

/// <summary>Chạy một script chỉ-đọc và đưa kết quả cho bộ phân tích. Dùng chung cho các Check bên dưới.</summary>
public abstract class ScriptCheck : ICheck
{
    private readonly ScriptRunner _run;

    protected ScriptCheck(ScriptRunner? run) =>
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 60));

    public abstract string Id { get; }
    public abstract string Name { get; }
    protected abstract string Script { get; }
    protected abstract IReadOnlyList<Finding> Analyze(JsonElement data);

    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken ct)
    {
        var r = await _run(Script, ct);
        return Analyze(Js.Root(r.Output));
    }
}

// ====================================================================== Máy chậm: RAM / CPU

public sealed class MemoryCheck : ScriptCheck
{
    public const string ScriptText = """
$ErrorActionPreference = 'SilentlyContinue'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
$top = @(Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 5 @{n='Name';e={$_.ProcessName}},@{n='MemMB';e={[int]($_.WorkingSet64/1MB)}})
[ordered]@{ totalKb = $os.TotalVisibleMemorySize; freeKb = $os.FreePhysicalMemory; cpu = $cpu; top = $top } | ConvertTo-Json -Depth 4 -Compress
""";

    public MemoryCheck(ScriptRunner? run = null) : base(run) { }

    public override string Id => "slow.memory";
    public override string Name => "Kiểm tra RAM và CPU";
    protected override string Script => ScriptText;
    protected override IReadOnlyList<Finding> Analyze(JsonElement data) => Evaluate(data);

    public static IReadOnlyList<Finding> Evaluate(JsonElement data)
    {
        var list = new List<Finding>();
        double total = Js.Num(data, "totalKb"), free = Js.Num(data, "freeKb");
        var top = string.Join(", ", Js.Items(data, "top").Select(t => $"{Js.Str(t, "Name")} ({Js.Num(t, "MemMB"):0} MB)"));

        if (total > 0)
        {
            double used = (total - free) * 100.0 / total;
            if (used >= 80)
                list.Add(new Finding
                {
                    Id = used >= 90 ? "memory.critical" : "memory.high",
                    Severity = used >= 90 ? Severity.Warning : Severity.Info,
                    Title = $"RAM đang dùng {used:0}%",
                    Cause = "RAM gần đầy làm máy chậm và giật." +
                            (top.Length > 0 ? $" Đang dùng nhiều nhất: {top}." : "") +
                            " Hãy đóng bớt chương trình/tab không cần, hoặc khởi động lại máy.",
                    TechnicalDetail = $"Used={used:0.#}%"
                });
        }

        double cpu = Js.Num(data, "cpu", -1);
        if (cpu >= 90)
            list.Add(new Finding
            {
                Id = "cpu.busy", Severity = Severity.Info,
                Title = $"CPU đang bận {cpu:0}%",
                Cause = "Đây chỉ là số đo tại một thời điểm. Nếu máy luôn bận như vậy, hãy xem Task Manager (Ctrl+Shift+Esc) để biết chương trình nào chiếm CPU."
            });

        return list;
    }
}

// ====================================================================== Máy chậm: chương trình khởi động

public sealed class StartupCheck : ScriptCheck
{
    public const string ScriptText = """
$ErrorActionPreference = 'SilentlyContinue'
[ordered]@{ items = @(Get-CimInstance Win32_StartupCommand | Select-Object Name,Location) } | ConvertTo-Json -Depth 3 -Compress
""";

    public const int InfoAt = 10;
    public const int WarnAt = 15;

    public StartupCheck(ScriptRunner? run = null) : base(run) { }

    public override string Id => "slow.startup";
    public override string Name => "Kiểm tra chương trình khởi động cùng Windows";
    protected override string Script => ScriptText;
    protected override IReadOnlyList<Finding> Analyze(JsonElement data) => Evaluate(data);

    public static IReadOnlyList<Finding> Evaluate(JsonElement data)
    {
        var names = Js.Items(data, "items").Select(i => Js.Str(i, "Name")).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count < InfoAt) return Array.Empty<Finding>();

        // Chỉ báo, không tự tắt: tắt nhầm có thể làm mất tính năng (diệt virus, cloud...). Người dùng tự chọn trong Task Manager.
        return new[]
        {
            new Finding
            {
                Id = "startup.many",
                Severity = names.Count >= WarnAt ? Severity.Warning : Severity.Info,
                Title = $"{names.Count} chương trình khởi động cùng Windows",
                Cause = "Nhiều chương trình tự chạy khi bật máy làm máy khởi động chậm. Mở Task Manager (Ctrl+Shift+Esc) > Startup apps, " +
                        "chuột phải chương trình không cần rồi chọn Disable. Gồm: " + string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", ..." : "."),
                TechnicalDetail = string.Join("; ", names)
            }
        };
    }
}

// ====================================================================== Mạng / DNS

public sealed class FlushDnsFix : IFix
{
    public const string Script = "$ErrorActionPreference='Stop'; Clear-DnsClientCache";
    public const string VerifyScript = "if (Resolve-DnsName -Name www.microsoft.com -Type A -DnsOnly -QuickTimeout -ErrorAction SilentlyContinue) { 'OK' } else { 'FAIL' }";

    private readonly ScriptRunner _run;

    public FlushDnsFix(ScriptRunner? run = null) => _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 60));

    public string Title => "Xóa bộ nhớ đệm DNS";
    public IReadOnlyList<string> Actions => new[] { "Xóa bộ nhớ đệm DNS của Windows (an toàn, Windows tự tạo lại khi cần)" };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang xóa bộ nhớ đệm DNS...");
        var r = await _run(Script, ct);
        return r.ExitCode == 0
            ? FixResult.Succeeded("Đã xóa bộ nhớ đệm DNS.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không xóa được bộ nhớ đệm DNS." : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        (await _run(VerifyScript, ct)).Output.Trim() == "OK";
}

public sealed class NetworkCheck : ScriptCheck
{
    public const string ScriptText = """
$ErrorActionPreference = 'SilentlyContinue'
$cfg = @(Get-NetIPConfiguration | Where-Object { $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway } |
    Select-Object InterfaceAlias,@{n='Gateway';e={"$($_.IPv4DefaultGateway.NextHop)"}})
$gw = $false
if ($cfg.Count -gt 0) { $gw = [bool](Test-Connection -ComputerName $cfg[0].Gateway -Count 1 -Quiet) }
$ip = [bool](Test-Connection -ComputerName 1.1.1.1 -Count 1 -Quiet)
$dns = [bool](Resolve-DnsName -Name www.microsoft.com -Type A -DnsOnly -QuickTimeout)
[ordered]@{ adapters = $cfg; gatewayOk = $gw; internetIpOk = $ip; dnsOk = $dns } | ConvertTo-Json -Depth 3 -Compress
""";

    private readonly Func<IFix> _flushDns;

    public NetworkCheck(ScriptRunner? run = null, Func<IFix>? flushDns = null) : base(run) =>
        _flushDns = flushDns ?? (() => new FlushDnsFix());

    public override string Id => "network.basic";
    public override string Name => "Kiểm tra mạng và DNS";
    protected override string Script => ScriptText;
    protected override IReadOnlyList<Finding> Analyze(JsonElement data) => Evaluate(data, _flushDns);

    public static IReadOnlyList<Finding> Evaluate(JsonElement data, Func<IFix> flushDns)
    {
        // Dịch được tên miền nghĩa là mạng đang dùng được (kể cả khi mạng công ty chặn ping).
        if (Js.Bool(data, "dnsOk") == true) return Array.Empty<Finding>();

        bool hasAdapter = Js.Items(data, "adapters").Any();
        bool gwOk = Js.Bool(data, "gatewayOk") == true;
        bool ipOk = Js.Bool(data, "internetIpOk") == true;

        if (!hasAdapter)
            return One("network.none", Severity.Critical, "Không có kết nối mạng",
                "Không thấy card mạng nào đang bật và có đường ra router. Kiểm tra dây mạng, bật Wi-Fi, hoặc bật lại Wi-Fi/Ethernet trong Settings > Network.");

        if (!gwOk)
            return One("network.gateway", Severity.Critical, "Không liên lạc được với router",
                "Máy có kết nối nhưng router không trả lời. Thử tắt/bật Wi-Fi, rút cắm dây mạng, hoặc khởi động lại router.");

        if (!ipOk)
            return One("network.internet", Severity.Critical, "Có mạng nội bộ nhưng không ra được Internet",
                "Router trả lời nhưng không đi ra Internet được. Thường do modem/nhà mạng. Thử khởi động lại modem hoặc báo nhà mạng.");

        return new[]
        {
            new Finding
            {
                Id = "network.dns", Severity = Severity.Warning,
                Title = "Lỗi DNS: ra được Internet nhưng không vào được trang web",
                Cause = "Máy đi ra Internet được nhưng không dịch được tên miền (vd. www.microsoft.com). Xóa bộ nhớ đệm DNS thường khắc phục. " +
                        "Nếu vẫn lỗi, thử đổi máy chủ DNS hoặc khởi động lại router.",
                Fixes = new[] { flushDns() }
            }
        };
    }

    private static IReadOnlyList<Finding> One(string id, Severity s, string title, string cause) =>
        new[] { new Finding { Id = id, Severity = s, Title = title, Cause = cause } };
}

// ====================================================================== Windows Update / hệ thống

public sealed class StartUpdateServiceFix : IFix
{
    public const string Script = "$ErrorActionPreference='Stop'; Set-Service -Name wuauserv -StartupType Manual; Start-Service -Name wuauserv";
    public const string VerifyScript = "(Get-CimInstance Win32_Service -Filter \"Name='wuauserv'\").StartMode";

    private readonly ScriptRunner _run;

    public StartUpdateServiceFix(ScriptRunner? run = null) => _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 60));

    public string Title => "Bật lại dịch vụ Windows Update";
    public IReadOnlyList<string> Actions => new[]
    {
        "Đặt dịch vụ Windows Update (wuauserv) về chế độ Manual (mặc định của Windows)",
        "Khởi động dịch vụ Windows Update"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang bật dịch vụ Windows Update...");
        var r = await _run(Script, ct);
        return r.ExitCode == 0
            ? FixResult.Succeeded("Đã bật dịch vụ Windows Update.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error)
                ? "Không bật được. Có thể máy công ty đang chặn bằng chính sách."
                : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        !(await _run(VerifyScript, ct)).Output.Trim().Equals("Disabled", StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowsUpdateCheck : ScriptCheck
{
    public const long LowDiskBytes = 10L * 1024 * 1024 * 1024;

    public const string ScriptText = """
$ErrorActionPreference = 'SilentlyContinue'
$svc = Get-CimInstance Win32_Service -Filter "Name='wuauserv'"
$reboot = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or
          (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')
$sysDrive = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='" + $env:SystemDrive + "'")
[ordered]@{ updateStartMode = "$($svc.StartMode)"; pendingReboot = [bool]$reboot; systemFreeBytes = $sysDrive.FreeSpace } | ConvertTo-Json -Compress
""";

    private readonly Func<IFix> _startService;

    public WindowsUpdateCheck(ScriptRunner? run = null, Func<IFix>? startService = null) : base(run) =>
        _startService = startService ?? (() => new StartUpdateServiceFix());

    public override string Id => "system.update";
    public override string Name => "Kiểm tra Windows Update và hệ thống";
    protected override string Script => ScriptText;
    protected override IReadOnlyList<Finding> Analyze(JsonElement data) => Evaluate(data, _startService);

    public static IReadOnlyList<Finding> Evaluate(JsonElement data, Func<IFix> startService)
    {
        var list = new List<Finding>();

        if (Js.Str(data, "updateStartMode").Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            list.Add(new Finding
            {
                Id = "update.disabled", Severity = Severity.Warning,
                Title = "Dịch vụ Windows Update đang bị tắt",
                Cause = "Windows không thể tìm hoặc cài bản cập nhật. Có thể do phần mềm tối ưu đã tắt nó. " +
                        "Trên máy công ty, đây có thể là chính sách của công ty, hãy hỏi bộ phận IT trước khi bật.",
                Fixes = new[] { startService() }
            });

        if (Js.Bool(data, "pendingReboot") == true)
            list.Add(new Finding
            {
                Id = "update.reboot-pending", Severity = Severity.Info,
                Title = "Máy đang chờ khởi động lại để hoàn tất cập nhật",
                Cause = "Có bản cập nhật hoặc thay đổi hệ thống chưa hoàn tất. Hãy lưu việc đang làm rồi khởi động lại máy."
            });

        double free = Js.Num(data, "systemFreeBytes", -1);
        if (free >= 0 && free < LowDiskBytes)
            list.Add(new Finding
            {
                Id = "update.low-disk", Severity = Severity.Warning,
                Title = "Ổ cài Windows còn trống ít",
                Cause = $"Chỉ còn trống khoảng {free / 1073741824.0:0.#} GB. Windows Update thường cần ít nhất 10 GB. Hãy dùng tab Dọn rác để giải phóng chỗ."
            });

        return list;
    }
}
