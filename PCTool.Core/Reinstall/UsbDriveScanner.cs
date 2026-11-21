using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Reinstall;

/// <summary>
/// GĐ0: liệt kê ổ USB và phân loại. CHỈ ĐỌC (Get-Disk/Get-Partition/Get-Volume), không xóa, không ghi, không đổi gì.
/// Vì chỉ đọc nên dùng được cả khi bật Chế độ an toàn.
/// </summary>
public sealed class UsbDriveScanner
{
    public const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$disks = foreach ($d in (Get-Disk)) {
  $parts = @(Get-Partition -DiskNumber $d.Number | ForEach-Object {
    $p = $_
    $v = $null
    if ($p.DriveLetter) { $v = Get-Volume -DriveLetter $p.DriveLetter }
    [ordered]@{
      PartitionNumber = $p.PartitionNumber
      DriveLetter = $(if ($p.DriveLetter) { "$($p.DriveLetter)" } else { '' })
      Size = $p.Size
      Type = "$($p.Type)"
      Label = $(if ($v) { "$($v.FileSystemLabel)" } else { '' })
      FileSystem = $(if ($v) { "$($v.FileSystem)" } else { '' })
      Free = $(if ($v) { $v.SizeRemaining } else { $null })
    }
  })
  [ordered]@{
    Number = $d.Number
    FriendlyName = "$($d.FriendlyName)"
    SerialNumber = "$($d.SerialNumber)".Trim()
    UniqueId = "$($d.UniqueId)".Trim()
    BusType = "$($d.BusType)"
    Size = $d.Size
    IsSystem = [bool]$d.IsSystem
    IsBoot = [bool]$d.IsBoot
    IsOffline = [bool]$d.IsOffline
    IsReadOnly = [bool]$d.IsReadOnly
    PartitionStyle = "$($d.PartitionStyle)"
    Partitions = $parts
  }
}
[ordered]@{ systemDrive = ($env:SystemDrive -replace ':', ''); disks = @($disks) } | ConvertTo-Json -Depth 6 -Compress
""";

    private readonly ScriptRunner _run;

    public UsbDriveScanner() : this((s, ct) => PowerShellRunner.RunAsync(s, ct, 60)) { }

    public UsbDriveScanner(ScriptRunner run) => _run = run;

    public async Task<UsbScanResult> ScanAsync(UsbScanContext ctx, CancellationToken ct)
    {
        var result = await _run(Script, ct);
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException("Không đọc được danh sách ổ đĩa: " +
                (string.IsNullOrWhiteSpace(result.Error) ? "PowerShell không trả về dữ liệu." : result.Error.Trim()));

        DiskScan scan;
        try { scan = DiskListParser.Parse(result.Output); }
        catch (FormatException ex)
        {
            Logger.Warn("Danh sách ổ đĩa không đọc được: " + ex.Message);
            throw new InvalidOperationException("Dữ liệu danh sách ổ đĩa không đúng định dạng.", ex);
        }

        // Không thấy ổ nào kể cả ổ Windows nghĩa là đọc hỏng: không để app tin rằng "không có ổ nào là ổ hệ thống".
        if (scan.Disks.Count == 0 || scan.SystemDriveLetter.Length == 0 ||
            !scan.Disks.Any(d => d.Partitions.Any(p => p.DriveLetter == scan.SystemDriveLetter)))
            throw new InvalidOperationException("Không xác định được ổ Windows đang chạy nên dừng để an toàn.");

        return UsbClassifier.Classify(scan, ctx);
    }
}
