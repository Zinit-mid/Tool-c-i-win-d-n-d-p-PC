using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Printer;

public sealed class PrinterRow
{
    public string Name { get; init; } = "";
    public string Driver { get; init; } = "";
    public string Port { get; init; } = "";
    public string Status { get; init; } = "";
    public string Connection { get; set; } = "";
}

/// <summary>Một dòng trong báo cáo chẩn đoán máy in (có cả mức "Ok" và tên máy in, dùng cho bảng và báo cáo chữ). Khác với <see cref="Framework.Finding"/> (vấn đề của khung chung, do PrinterFindingMapper tạo ra).</summary>
public sealed class PrinterFinding
{
    /// <summary>Error / Warn / Info / Ok (dùng để tô màu dòng).</summary>
    public string Level { get; init; } = "Info";

    /// <summary>Mã loại vấn đề (rỗng nếu chưa phân loại). Dùng để gắn cách sửa tương ứng.</summary>
    public string Code { get; init; } = "";
    public string Printer { get; init; } = "";
    public string Problem { get; init; } = "";
    public string Advice { get; init; } = "";

    public string LevelText => Level switch
    {
        "Error" => "✖ Lỗi",
        "Warn" => "⚠ Cảnh báo",
        "Ok" => "✔ Ổn",
        _ => "ℹ Thông tin"
    };
}

public static class FindingCodes
{
    public const string SpoolerStopped = "printer.spooler-stopped";
    public const string QueueStuck = "printer.queue-stuck";
}

public sealed class PrinterReport
{
    public string SpoolerText { get; set; } = "";
    public List<PrinterRow> Printers { get; } = new();
    public List<PrinterFinding> Findings { get; } = new();
    public bool SpoolerRunning { get; set; }
    public bool HasStuckJobs { get; set; }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Báo cáo chẩn đoán máy in - {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Print Spooler: {SpoolerText}");
        sb.AppendLine();
        sb.AppendLine("Máy in:");
        foreach (var p in Printers)
            sb.AppendLine($" - {p.Name} | driver: {p.Driver} | cổng: {p.Port} | {p.Status} | {p.Connection}");
        sb.AppendLine();
        sb.AppendLine("Kết quả:");
        foreach (var f in Findings)
        {
            sb.AppendLine($" [{f.LevelText}] {(f.Printer.Length > 0 ? f.Printer + ": " : "")}{f.Problem}");
            if (f.Advice.Length > 0) sb.AppendLine($"    -> {f.Advice}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Chẩn đoán máy in: thu thập thông tin bằng PowerShell (Get-Printer, Get-PrinterPort, ...)
/// rồi tự kiểm tra kết nối mạng và suy ra nguyên nhân. Chỉ ĐỌC, không sửa gì.
/// </summary>
public static class PrinterDiagnostics
{
    private const string CollectScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $sp = Get-Service -Name Spooler
        $cim = @{}
        Get-CimInstance Win32_Printer | ForEach-Object { $cim[$_.Name] = $_ }
        $printers = @(Get-Printer | ForEach-Object {
            $c = $cim[$_.Name]
            [ordered]@{ name = $_.Name; driver = $_.DriverName; port = $_.PortName; status = [string]$_.PrinterStatus; workOffline = [bool]($c -and $c.WorkOffline); isDefault = [bool]($c -and $c.Default) }
        })
        $ports = @(Get-PrinterPort | ForEach-Object { [ordered]@{ name = $_.Name; host = $_.PrinterHostAddress; portNumber = [int]$_.PortNumber } })
        $drivers = @(Get-PrinterDriver | ForEach-Object { $_.Name })
        $jobs = @()
        foreach ($p in $printers) {
            $jobs += @(Get-PrintJob -PrinterName $p.name | ForEach-Object { [ordered]@{ printer = $p.name; id = $_.Id; doc = $_.DocumentName; status = [string]$_.JobStatus } })
        }
        $pnp = @(Get-PnpDevice -Class Printer, PrintQueue | ForEach-Object { [ordered]@{ name = $_.FriendlyName; status = [string]$_.Status; problem = [string]$_.Problem } })
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-PrintService/Admin'; Level = 2; StartTime = (Get-Date).AddDays(-7) } -MaxEvents 10 | ForEach-Object { [ordered]@{ time = $_.TimeCreated.ToString('dd/MM HH:mm'); id = $_.Id; msg = (($_.Message -split '\r?\n')[0]) } })
        $result = [ordered]@{ spooler = [ordered]@{ status = [string]$sp.Status; startType = [string]$sp.StartType }; printers = $printers; ports = $ports; drivers = $drivers; jobs = $jobs; pnp = $pnp; events = $events }
        ConvertTo-Json -InputObject $result -Depth 5 -Compress
        """;

    // Tên driver/máy in ảo: không có phần cứng để kiểm tra.
    private static readonly string[] VirtualMarkers =
    {
        "Microsoft Print to PDF", "Microsoft XPS", "OneNote", "Fax", "Send To", "PDF", "Snagit", "AnyDesk"
    };

    private static readonly string[] VirtualPorts = { "PORTPROMPT:", "nul:", "FILE:", "SHRFAX:", "XPSPort:", "Microsoft.Office" };

    public static async Task<PrinterReport> RunAsync(CancellationToken ct)
    {
        Logger.Info("Chẩn đoán máy in: đang thu thập thông tin...");
        var ps = await PowerShellRunner.RunAsync(CollectScript, ct);
        if (ps.ExitCode != 0 || string.IsNullOrWhiteSpace(ps.Output))
            throw new InvalidOperationException("PowerShell không trả về dữ liệu. " + ps.Error.Trim());

        using var doc = JsonDocument.Parse(ps.Output.Trim());
        var root = doc.RootElement;
        var report = new PrinterReport();

        // ---- Print Spooler ----
        string spStatus = "", spStart = "";
        if (root.TryGetProperty("spooler", out var sp) && sp.ValueKind == JsonValueKind.Object)
        {
            spStatus = Str(sp, "status");
            spStart = Str(sp, "startType");
        }
        report.SpoolerRunning = spStatus.Equals("Running", StringComparison.OrdinalIgnoreCase);
        report.SpoolerText = string.IsNullOrEmpty(spStatus) ? "không đọc được" : $"{spStatus} (khởi động: {spStart})";

        if (!report.SpoolerRunning)
        {
            report.Findings.Add(new PrinterFinding
            {
                Level = "Error",
                Code = FindingCodes.SpoolerStopped,
                Problem = $"Dịch vụ Print Spooler không chạy (trạng thái: {(string.IsNullOrEmpty(spStatus) ? "không rõ" : spStatus)}).",
                Advice = spStart.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                    ? "Spooler đang bị tắt hẳn (Disabled). Bấm \"Khởi động lại Spooler\" sẽ không đủ, cần đặt lại kiểu khởi động = Automatic trong services.msc."
                    : "Bấm \"Khởi động lại Spooler\" bên dưới."
            });
        }

        // ---- Dữ liệu thô ----
        var ports = new Dictionary<string, (string Host, int Number)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Arr(root, "ports"))
            ports[Str(p, "name")] = (Str(p, "host"), Int(p, "portNumber"));

        var drivers = new HashSet<string>(
            Arr(root, "drivers").Select(d => d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : ""),
            StringComparer.OrdinalIgnoreCase);

        var jobs = Arr(root, "jobs")
            .Select(j => (Printer: Str(j, "printer"), Doc: Str(j, "doc"), Status: Str(j, "status")))
            .ToList();

        // ---- Kiểm tra từng máy in ----
        var checkedHosts = new Dictionary<string, (bool Ping, bool Tcp)>(StringComparer.OrdinalIgnoreCase);
        int realPrinters = 0;

        foreach (var p in Arr(root, "printers"))
        {
            ct.ThrowIfCancellationRequested();

            string name = Str(p, "name"), driver = Str(p, "driver"), port = Str(p, "port");
            string status = Str(p, "status");
            bool offlineFlag = Bool(p, "workOffline");

            var row = new PrinterRow
            {
                Name = name,
                Driver = driver,
                Port = port,
                Status = StatusText(status, offlineFlag)
            };
            report.Printers.Add(row);

            if (IsVirtual(driver, port))
            {
                row.Connection = "Máy in ảo (không cần kiểm tra)";
                continue;
            }
            realPrinters++;

            // 1) Driver
            if (string.IsNullOrWhiteSpace(driver))
            {
                Add(report, "Error", name, "Máy in chưa có driver.",
                    "Cài driver đúng model từ trang hãng, rồi xóa và thêm lại máy in.");
            }
            else if (drivers.Count > 0 && !drivers.Contains(driver))
            {
                Add(report, "Error", name, $"Driver \"{driver}\" không còn trong hệ thống (bị gỡ hoặc hỏng).",
                    "Cài lại driver của hãng cho đúng model, rồi chọn lại driver cho máy in.");
            }
            else if (driver.Contains("Generic", StringComparison.OrdinalIgnoreCase))
            {
                Add(report, "Warn", name, "Đang dùng driver chung (Generic), dễ in sai khổ/mất tính năng.",
                    "Cài driver chính hãng đúng model.");
            }

            // 2) Trạng thái máy in
            if (offlineFlag)
            {
                Add(report, "Warn", name, "Máy in đang bật chế độ \"Use Printer Offline\".",
                    "Mở Cài đặt > Máy in > chọn máy in > Mở hàng đợi in > menu Printer > bỏ tick \"Use Printer Offline\".");
            }
            var (statusLevel, statusAdvice) = StatusInfo(status);
            if (statusLevel != null)
                Add(report, statusLevel, name, $"Trạng thái: {row.Status}.", statusAdvice);

            // 3) Kết nối
            row.Connection = await CheckConnectionAsync(report, row, ports, checkedHosts, ct);

            // 4) Hàng đợi in
            var stuck = jobs.Where(j => j.Printer == name && IsStuckJob(j.Status)).ToList();
            if (stuck.Count > 0)
            {
                report.HasStuckJobs = true;
                Add(report, "Warn", name,
                    $"{stuck.Count} lệnh in đang kẹt (vd: \"{Trunc(stuck[0].Doc)}\" - {stuck[0].Status}).",
                    "Bấm \"Xóa hàng đợi in\" bên dưới để dọn lệnh kẹt.", FindingCodes.QueueStuck);
            }
            else
            {
                int waiting = jobs.Count(j => j.Printer == name);
                if (waiting > 0 && statusLevel != null)
                    Add(report, "Info", name, $"Có {waiting} lệnh in đang chờ.", "Sẽ tự in khi máy in sẵn sàng; nếu không thì xóa hàng đợi.");
            }
        }

        if (realPrinters == 0 && report.SpoolerRunning)
        {
            report.Findings.Add(new PrinterFinding
            {
                Level = "Warn",
                Problem = report.Printers.Count == 0
                    ? "Không có máy in nào được cài trên máy này."
                    : "Chỉ có máy in ảo (PDF/XPS...), chưa có máy in thật.",
                Advice = "Mở \"Cài đặt máy in\" bên dưới > Thêm máy in, hoặc cài driver của hãng."
            });
        }

        // ---- Thiết bị PnP báo lỗi ----
        foreach (var d in Arr(root, "pnp"))
        {
            string st = Str(d, "status"), problem = Str(d, "problem");
            bool bad = st.Equals("Error", StringComparison.OrdinalIgnoreCase)
                       || (!string.IsNullOrEmpty(problem) && !problem.Equals("CM_PROB_NONE", StringComparison.OrdinalIgnoreCase));
            if (!bad) continue;

            Add(report, "Error", Str(d, "name"), $"Windows báo thiết bị lỗi ({(string.IsNullOrEmpty(problem) ? st : problem)}).",
                PnpAdvice(problem));
        }

        // ---- Lỗi PrintService gần đây ----
        var events = Arr(root, "events").ToList();
        if (events.Count > 0)
        {
            var e0 = events[0];
            Add(report, "Warn", "", $"Có {events.Count}+ lỗi in trong nhật ký Windows 7 ngày qua. Gần nhất ({Str(e0, "time")}, ID {Str(e0, "id")}): {Trunc(Str(e0, "msg"), 160)}",
                "Mở Event Viewer > Applications and Services Logs > Microsoft > Windows > PrintService > Admin để xem chi tiết.");
        }

        if (report.Findings.Count == 0)
        {
            report.Findings.Add(new PrinterFinding
            {
                Level = "Ok",
                Problem = "Không phát hiện vấn đề (spooler chạy, driver có, kết nối được, hàng đợi trống).",
                Advice = "Nếu vẫn không in được, thử in trang thử từ Windows hoặc kiểm tra mực/giấy/đèn báo trên máy in."
            });
        }

        // Lỗi nặng lên trước.
        var order = new[] { "Error", "Warn", "Info", "Ok" };
        var sorted = report.Findings.OrderBy(f => Array.IndexOf(order, f.Level)).ToList(); // OrderBy giữ nguyên thứ tự trong cùng mức
        report.Findings.Clear();
        report.Findings.AddRange(sorted);

        Logger.Info($"Chẩn đoán máy in xong: {report.Printers.Count} máy in, " +
                    $"{report.Findings.Count(f => f.Level == "Error")} lỗi, {report.Findings.Count(f => f.Level == "Warn")} cảnh báo.");
        return report;
    }

    // ------------------------------------------------------------------ kết nối

    private static async Task<string> CheckConnectionAsync(
        PrinterReport report, PrinterRow row,
        Dictionary<string, (string Host, int Number)> ports,
        Dictionary<string, (bool Ping, bool Tcp)> cache, CancellationToken ct)
    {
        var port = row.Port;
        string? host = null;
        int tcpPort = 9100;

        if (port.StartsWith(@"\\"))
        {
            // Máy in chia sẻ qua mạng: \\server\ten
            host = port.TrimStart('\\').Split('\\')[0];
            tcpPort = 445;
        }
        else if (ports.TryGetValue(port, out var info) && !string.IsNullOrWhiteSpace(info.Host))
        {
            host = info.Host;
            tcpPort = info.Number > 0 ? info.Number : 9100;
        }

        if (host != null)
        {
            var key = $"{host}:{tcpPort}";
            (bool Ping, bool Tcp) res;
            if (!cache.TryGetValue(key, out res))
            {
                res = await ProbeAsync(host, tcpPort, ct);
                cache[key] = res;
            }

            if (res.Tcp) return $"OK ({host}:{tcpPort})";

            if (res.Ping)
            {
                Add(report, "Error", row.Name,
                    $"Thấy máy in ở {host} nhưng cổng {tcpPort} đóng.",
                    tcpPort == 445
                        ? "Máy chủ chia sẻ có thể chặn chia sẻ file/máy in; kiểm tra tên chia sẻ và quyền truy cập."
                        : "Kiểm tra máy in đã bật cổng RAW/9100 chưa, hoặc đổi sang đúng số cổng trong Port Settings.");
                return $"Ping được, cổng {tcpPort} đóng";
            }

            Add(report, "Error", row.Name,
                $"Không kết nối được tới {host} (không ping được, cổng {tcpPort} không mở).",
                "Kiểm tra máy in đã bật và nối mạng; IP máy in có thể đã đổi (DHCP) - in trang cấu hình từ máy in để xem IP rồi sửa lại cổng; kiểm tra PC cùng dải mạng.");
            return "Không kết nối được";
        }

        if (port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || port.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase))
        {
            if (IsBadStatus(row.Status))
                Add(report, "Warn", row.Name, "Máy in cổng USB đang báo lỗi/ngoại tuyến.",
                    "Kiểm tra cáp USB và nguồn máy in, thử cổng USB khác (cắm trực tiếp, tránh hub), rồi chẩn đoán lại.");
            return "Cổng USB (không kiểm tra từ xa được)";
        }

        if (port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
        {
            if (IsBadStatus(row.Status))
                Add(report, "Warn", row.Name, "Máy in dùng cổng WSD đang báo lỗi/ngoại tuyến.",
                    "Cổng WSD hay mất kết nối khi IP đổi. Nên thêm máy in bằng cổng TCP/IP chuẩn (nhập IP máy in) để ổn định hơn.");
            return "Cổng WSD (không biết IP để ping)";
        }

        return "Không xác định";
    }

    private static async Task<(bool Ping, bool Tcp)> ProbeAsync(string host, int tcpPort, CancellationToken ct)
    {
        bool ping = false, tcp = false;

        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, 2000);
            ping = r.Status == IPStatus.Success;
        }
        catch { /* DNS lỗi, không ping được */ }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            using var client = new TcpClient();
            await client.ConnectAsync(host, tcpPort, cts.Token);
            tcp = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* không mở được cổng */ }

        return (ping, tcp);
    }

    // ------------------------------------------------------------------ trạng thái

    private static string StatusText(string status, bool offline)
    {
        if (offline) return "Ngoại tuyến (Use Offline)";
        return status switch
        {
            "Normal" => "Sẵn sàng",
            "Paused" => "Đang tạm dừng",
            "Error" => "Lỗi",
            "PendingDeletion" => "Đang xóa",
            "PaperJam" => "Kẹt giấy",
            "PaperOut" => "Hết giấy",
            "ManualFeed" => "Chờ nạp giấy tay",
            "PaperProblem" => "Lỗi giấy",
            "Offline" => "Ngoại tuyến",
            "Printing" => "Đang in",
            "Busy" => "Bận",
            "TonerLow" => "Sắp hết mực",
            "NoToner" => "Hết mực",
            "DoorOpen" => "Nắp đang mở",
            "UserIntervention" => "Cần người xử lý",
            "OutOfMemory" => "Hết bộ nhớ máy in",
            "OutputBinFull" => "Khay ra đầy",
            "PowerSave" => "Tiết kiệm điện",
            "" => "Không rõ",
            _ => status
        };
    }

    private static (string? Level, string Advice) StatusInfo(string status) => status switch
    {
        "Offline" => ("Error", "Bật máy in, kiểm tra cáp/mạng, rồi chuột phải máy in > bỏ \"Use Printer Offline\" nếu có."),
        "Error" => ("Error", "Xem đèn báo/màn hình trên máy in (hết giấy, kẹt giấy, nắp mở...) rồi xử lý."),
        "PaperJam" => ("Error", "Gỡ giấy kẹt trong máy in rồi nhấn nút tiếp tục trên máy."),
        "PaperOut" => ("Error", "Nạp thêm giấy vào khay."),
        "PaperProblem" => ("Error", "Kiểm tra giấy trong khay (sai khổ, ẩm, nạp lệch)."),
        "NoToner" => ("Error", "Thay mực/toner."),
        "DoorOpen" => ("Error", "Đóng nắp máy in."),
        "UserIntervention" => ("Error", "Máy in đang chờ thao tác trực tiếp; xem màn hình/đèn báo trên máy."),
        "OutOfMemory" => ("Warn", "Giảm độ phân giải/số trang mỗi lệnh in hoặc khởi động lại máy in."),
        "Paused" => ("Warn", "Mở hàng đợi in, menu Printer > bỏ tick \"Pause Printing\"."),
        "TonerLow" => ("Warn", "Chuẩn bị mực/toner thay thế."),
        "OutputBinFull" => ("Warn", "Lấy giấy ra khỏi khay ra."),
        _ => (null, "")
    };

    private static bool IsBadStatus(string statusText) =>
        statusText is "Ngoại tuyến" or "Ngoại tuyến (Use Offline)" or "Lỗi" or "Kẹt giấy" or "Hết giấy" or "Cần người xử lý";

    private static bool IsStuckJob(string status) =>
        new[] { "Error", "Blocked", "Offline", "Deleting", "UserIntervention", "Paused", "PaperOut", "Retained" }
            .Any(s => status.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static string PnpAdvice(string problem) => problem switch
    {
        "CM_PROB_FAILED_INSTALL" or "CM_PROB_FAILED_ADD" => "Cài driver lỗi: gỡ thiết bị rồi cài lại driver chính hãng.",
        "CM_PROB_DISABLED" => "Thiết bị đang bị tắt: bật lại trong Device Manager.",
        "CM_PROB_NEED_RESTART" => "Khởi động lại máy để hoàn tất cài đặt.",
        "CM_PROB_DRIVER_FAILED_LOAD" or "CM_PROB_DRIVER_SERVICE_KEY_INVALID" => "Driver bị hỏng: gỡ rồi cài lại driver chính hãng.",
        _ => "Mở Device Manager xem thiết bị này, thử cập nhật hoặc cài lại driver."
    };

    private static bool IsVirtual(string driver, string port) =>
        VirtualMarkers.Any(m => driver.Contains(m, StringComparison.OrdinalIgnoreCase))
        || VirtualPorts.Any(p => port.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ tiện ích JSON

    private static void Add(PrinterReport r, string level, string printer, string problem, string advice, string code = "") =>
        r.Findings.Add(new PrinterFinding { Level = level, Printer = printer, Problem = problem, Advice = advice, Code = code });

    private static string Trunc(string s, int max = 50) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>PowerShell thu gọn mảng 1 phần tử thành object tùy phiên bản, nên chấp nhận cả hai dạng.</summary>
    private static List<JsonElement> Arr(JsonElement parent, string prop)
    {
        if (parent.TryGetProperty(prop, out var e))
        {
            if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().ToList();
            if (e.ValueKind is JsonValueKind.Object or JsonValueKind.String) return new List<JsonElement> { e };
        }
        return new List<JsonElement>();
    }

    private static string Str(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
    }

    private static bool Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
