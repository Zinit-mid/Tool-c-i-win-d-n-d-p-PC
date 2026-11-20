using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Hardware;

public interface IHardwareReader
{
    Task<HardwareReport> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Đọc thông số bằng WMI/CIM (có sẵn trong Windows) qua PowerShell. Chỉ ĐỌC, không thay đổi gì.
/// Nhiệt độ cảm biến chưa có trong bản này (cần thư viện ngoài, sẽ cân nhắc sau).
/// </summary>
public sealed class WmiHardwareReader : IHardwareReader
{
    public const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$o = [ordered]@{}
$o.cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed)
$o.totalRam = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$o.ram = @(Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity,Speed)
$o.gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,AdapterRAM)
$o.board = @(Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product)
$o.os = @(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,@{n='LastBoot';e={$_.LastBootUpTime.ToString('o')}})
$o.disks = @(Get-PhysicalDisk | Select-Object FriendlyName,@{n='MediaType';e={"$($_.MediaType)"}},Size,@{n='HealthStatus';e={"$($_.HealthStatus)"}},@{n='OperationalStatus';e={"$($_.OperationalStatus)"}})
$o.volumes = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID,VolumeName,Size,FreeSpace)
$o.batteryCharge = @(Get-CimInstance Win32_Battery | Select-Object EstimatedChargeRemaining)
$o.batteryDesign = @(Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData | Select-Object DesignedCapacity)
$o.batteryFull = @(Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity | Select-Object FullChargedCapacity)
$o | ConvertTo-Json -Depth 5 -Compress
""";

    private readonly Func<string, CancellationToken, Task<PowerShellResult>> _run;

    public WmiHardwareReader() : this((s, ct) => PowerShellRunner.RunAsync(s, ct, 60)) { }

    public WmiHardwareReader(Func<string, CancellationToken, Task<PowerShellResult>> run) => _run = run;

    public async Task<HardwareReport> ReadAsync(CancellationToken ct)
    {
        var result = await _run(Script, ct);
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException("Không đọc được thông số máy: " +
                (string.IsNullOrWhiteSpace(result.Error) ? "PowerShell không trả về dữ liệu." : result.Error.Trim()));
        try
        {
            return HardwareParser.Parse(result.Output);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Logger.Warn("Dữ liệu phần cứng không đọc được: " + ex.Message);
            throw new InvalidOperationException("Dữ liệu thông số máy không đúng định dạng.", ex);
        }
    }
}

/// <summary>Cho phép màn hình Tổng quan quét sức khỏe phần cứng cùng các Check khác.</summary>
public sealed class HardwareHealthCheck : ICheck
{
    private readonly IHardwareReader _reader;
    private readonly Func<DateTimeOffset> _now;

    public HardwareHealthCheck(IHardwareReader reader, Func<DateTimeOffset>? now = null)
    {
        _reader = reader;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Id => "hardware.health";
    public string Name => "Kiểm tra sức khỏe phần cứng";

    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken ct) =>
        HardwareAnalyzer.Analyze(await _reader.ReadAsync(ct), _now());
}
