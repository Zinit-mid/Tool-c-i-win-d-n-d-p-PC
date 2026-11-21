using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Backup;

/// <summary>Cơ sở chung: chỉ ghi vào thư mục đích đã kiểm tra, đếm file để biết có kết quả thật.</summary>
public abstract class BackupFix : IFix
{
    private readonly ScriptRunner _run;

    protected BackupFix(string folder, ScriptRunner? run)
    {
        if (!BackupPath.IsValid(folder, out var err)) throw new ArgumentException(err, nameof(folder));
        Folder = folder;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 600));
    }

    public string Folder { get; }
    public abstract string Title { get; }
    public abstract IReadOnlyList<string> Actions { get; }
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true; // chỉ tạo thư mục mới; xóa thư mục đó là hoàn tác
    protected abstract string Pattern { get; }
    protected abstract string Noun { get; }
    protected abstract string NothingMessage { get; }
    protected abstract string FailMessage { get; }
    public abstract string BuildScript();
    protected virtual string DoneNote => "";

    public string BuildVerifyScript() =>
        $"(Get-ChildItem -LiteralPath '{Folder}' -Filter '{Pattern}' -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count";

    protected static int LastInt(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return int.TryParse(line, out var n) ? n : -1;
    }

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Đang sao lưu {Noun}...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? FailMessage : r.Error.Trim());

        int n = LastInt(r.Output);
        if (n < 0) return FixResult.Failed("Không đọc được kết quả sao lưu, hãy kiểm tra thư mục: " + Folder);
        if (n == 0) return FixResult.Failed(NothingMessage);
        return FixResult.Succeeded($"Đã sao lưu {n} {Noun} vào {Folder}." + DoneNote);
    }

    public async Task<bool> VerifyAsync(CancellationToken ct) =>
        LastInt((await _run(BuildVerifyScript(), ct)).Output) > 0;
}

/// <summary>Xuất các driver bên thứ ba (card mạng, card đồ họa, chipset...) ra thư mục để cài lại sau khi cài Windows.</summary>
public sealed class DriverBackupFix : BackupFix
{
    public DriverBackupFix(string folder, ScriptRunner? run = null) : base(folder, run) { }

    public override string Title => "Sao lưu driver";
    public override IReadOnlyList<string> Actions => new[]
    {
        $"Tạo thư mục mới: {Folder}",
        "Xuất các driver bên thứ ba đang cài trên máy vào thư mục đó (không gỡ, không đổi driver nào)",
        "Có thể mất vài phút và tốn vài trăm MB đến vài GB trên ổ đích"
    };
    protected override string Pattern => "*.inf";
    protected override string Noun => "driver";
    protected override string NothingMessage => "Không có driver bên thứ ba nào để sao lưu (Windows tự có sẵn driver cơ bản).";
    protected override string FailMessage => "Không sao lưu được driver. Kiểm tra ổ đích còn trống và đang chạy bằng quyền Administrator.";

    public override string BuildScript() => $"""
$ErrorActionPreference = 'Stop'
$d = '{Folder}'
New-Item -ItemType Directory -Force -Path $d | Out-Null
Export-WindowsDriver -Online -Destination $d | Out-Null
(Get-ChildItem -LiteralPath $d -Filter '*.inf' -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
""";
}

/// <summary>Xuất các mạng Wi-Fi đã lưu. LƯU Ý: file xuất chứa mật khẩu Wi-Fi dạng chữ thường.</summary>
public sealed class WifiBackupFix : BackupFix
{
    public WifiBackupFix(string folder, ScriptRunner? run = null) : base(folder, run) { }

    public override string Title => "Sao lưu Wi-Fi đã lưu";
    public override IReadOnlyList<string> Actions => new[]
    {
        $"Tạo thư mục mới: {Folder}",
        "Xuất mỗi mạng Wi-Fi đã lưu thành một file .xml vào thư mục đó",
        "CẢNH BÁO: các file này chứa MẬT KHẨU Wi-Fi dạng chữ thường. Chỉ lưu ở nơi an toàn, đừng gửi cho người khác"
    };
    protected override string Pattern => "*.xml";
    protected override string Noun => "mạng Wi-Fi";
    protected override string NothingMessage => "Máy không có mạng Wi-Fi nào đã lưu.";
    protected override string FailMessage => "Không đọc được danh sách Wi-Fi (máy có thể không có card Wi-Fi hoặc dịch vụ Wi-Fi đang tắt).";
    protected override string DoneNote => " Các file chứa mật khẩu Wi-Fi dạng chữ thường, hãy giữ kín.";

    public override string BuildScript() => $"""
$ErrorActionPreference = 'Stop'
$d = '{Folder}'
New-Item -ItemType Directory -Force -Path $d | Out-Null
netsh wlan export profile key=clear folder="$d" | Out-Null
if ($LASTEXITCODE -ne 0) {{ [Console]::Error.WriteLine('netsh wlan thất bại'); exit 1 }}
(Get-ChildItem -LiteralPath $d -Filter '*.xml' | Measure-Object).Count
""";
}
