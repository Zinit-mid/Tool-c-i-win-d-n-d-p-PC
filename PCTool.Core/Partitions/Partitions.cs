using System.Text.Json;
using System.Text.RegularExpressions;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Partitions;

public sealed record VolumeInfo(string Letter, string Label, string FileSystem, long Size, long Free, int DiskNumber, string BusType, bool IsSystem, bool DiskDynamicOrOffline)
{
    public string Describe() => $"{Letter}:  {(Label.Length == 0 ? "(không tên)" : Label)}  {FileSystem}  tổng {Gb(Size)}  trống {Gb(Free)}  (ổ cứng số {DiskNumber}, {BusType}){(IsSystem ? "  <- Ổ WINDOWS" : "")}";
    private static string Gb(long b) => (b / 1073741824.0).ToString("0.0") + " GB";
}

/// <summary>Chỉ đọc: liệt kê các ổ có ký tự (Get-Volume + Get-Partition + Get-Disk).</summary>
public static class VolumeScanner
{
    public const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$sys = $env:SystemDrive.TrimEnd(':')
$r = @()
foreach ($v in Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' }) {
  $p = Get-Partition -DriveLetter $v.DriveLetter
  $d = if ($p) { Get-Disk -Number $p.DiskNumber } else { $null }
  $r += [ordered]@{ letter = "$($v.DriveLetter)"; label = "$($v.FileSystemLabel)"; fs = "$($v.FileSystem)"; size = [long]$v.Size; free = [long]$v.SizeRemaining
    disk = if ($p) { [int]$p.DiskNumber } else { -1 }; bus = if ($d) { "$($d.BusType)" } else { '' }
    isSystem = ($v.DriveLetter -eq $sys); bad = [bool]($d -and ($d.IsOffline -or $d.IsReadOnly -or $d.PartitionStyle -eq 'RAW')) }
}
@($r) | ConvertTo-Json -Compress -Depth 3
""";

    public static IReadOnlyList<VolumeInfo> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
                : doc.RootElement.ValueKind == JsonValueKind.Object ? new List<JsonElement> { doc.RootElement } : throw new FormatException();
            var res = new List<VolumeInfo>();
            foreach (var e in items)
            {
                string S(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                long N(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;
                bool B(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
                var letter = S("letter").ToUpperInvariant();
                if (!Regex.IsMatch(letter, "^[A-Z]$")) continue;
                res.Add(new VolumeInfo(letter, S("label"), S("fs"), N("size"), N("free"), (int)N("disk"), S("bus"), B("isSystem"), B("bad")));
            }
            return res;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new FormatException("Không đọc được danh sách ổ đĩa."); }
    }

    public static async Task<IReadOnlyList<VolumeInfo>> ReadAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c, 60);
        var r = await run(Script, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được ổ đĩa." : r.Error.Trim());
        return Parse(r.Output);
    }
}

public static class PartitionRules
{
    public const long MinShrinkMb = 1024;
    public const long KeepFreeBytes = 5L * 1073741824;
    private static readonly Regex LabelRx = new(@"^[\p{L}\p{N} _-]{1,32}$", RegexOptions.Compiled);

    public static bool IsLetter(string? s) => s is { Length: 1 } && s[0] is >= 'A' and <= 'Z';
    public static bool ValidLabel(string? s) => s != null && LabelRx.IsMatch(s) && s.Trim() == s;

    /// <summary>Mã người dùng phải gõ để xác nhận, vd "CHIA C", "MO RONG D", "DOI E".</summary>
    public static string Code(string op, string letter) => $"{op} {letter}";

    /// <summary>Ký tự ổ không được dùng làm ký tự MỚI: A, B (ổ mềm) và C.</summary>
    public static bool LetterReserved(string l) => l is "A" or "B" or "C";
}

public enum PartitionOp { Shrink, Extend, ChangeLetter }

/// <summary>Tách (thu nhỏ rồi tạo phân vùng mới NTFS). KHÔNG xóa phân vùng nào, KHÔNG format ổ có sẵn: chỉ format đúng phân vùng mới vừa tạo.</summary>
public sealed class ShrinkAndCreateFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly VolumeInfo _src;
    private readonly long _mb;
    private readonly string _newLetter, _label;
    private long _srcOldSize;

    public ShrinkAndCreateFix(VolumeInfo source, long amountMb, string newLetter, string label, string confirm, ScriptRunner? run = null)
    {
        _src = source;
        if (!PartitionRules.IsLetter(source.Letter)) throw new ArgumentException("Ký tự ổ nguồn không hợp lệ.", nameof(source));
        if (!string.Equals(source.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Chỉ hỗ trợ ổ NTFS.", nameof(source));
        if (source.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) || source.DiskDynamicOrOffline) throw new ArgumentException("Ổ này là USB, đang offline hoặc chỉ đọc nên không chỉnh được.", nameof(source));
        if (amountMb < PartitionRules.MinShrinkMb) throw new ArgumentException($"Tối thiểu {PartitionRules.MinShrinkMb} MB.", nameof(amountMb));
        if (source.Free < 0 || amountMb * 1048576 > source.Free - PartitionRules.KeepFreeBytes)
            throw new ArgumentException("Ổ nguồn phải còn trống ít nhất 5 GB sau khi tách.", nameof(amountMb));
        if (!PartitionRules.IsLetter(newLetter) || PartitionRules.LetterReserved(newLetter)) throw new ArgumentException("Ký tự ổ mới không hợp lệ (không dùng A, B, C).", nameof(newLetter));
        if (newLetter == source.Letter) throw new ArgumentException("Ký tự ổ mới phải khác ổ nguồn.", nameof(newLetter));
        if (!PartitionRules.ValidLabel(label)) throw new ArgumentException("Tên ổ chỉ gồm chữ, số, khoảng trắng, _ và -, tối đa 32 ký tự.", nameof(label));
        if (confirm != PartitionRules.Code("CHIA", source.Letter)) throw new ArgumentException($"Chưa gõ đúng mã xác nhận ({PartitionRules.Code("CHIA", source.Letter)}).", nameof(confirm));
        _mb = amountMb; _newLetter = newLetter; _label = label;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 1800));
    }

    public string Title => $"Tách {_mb} MB từ ổ {_src.Letter}: thành ổ mới {_newLetter}:";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Thu nhỏ phân vùng {_src.Letter}: đi {_mb} MB (Windows tự di chuyển file nếu được; dữ liệu không bị xóa)",
        $"Tạo phân vùng mới NTFS tên \"{_label}\", ký tự {_newLetter}: ngay sau phân vùng vừa thu nhỏ, và format ĐÚNG phân vùng mới đó",
        "KHÔNG xóa, KHÔNG format phân vùng có sẵn nào. Không hoàn tác tự động được (chỉ gộp lại bằng Disk Management)",
        "Hãy sao lưu dữ liệu quan trọng trước. Mất điện giữa chừng có thể làm hỏng bảng phân vùng"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$l = '{{_src.Letter}}'; $nl = '{{_newLetter}}'; $mb = {{_mb}}; $label = '{{_label}}'
$p = Get-Partition -DriveLetter $l
if (-not $p) { throw 'Không thấy ổ nguồn.' }
$d = Get-Disk -Number $p.DiskNumber
if ($d.BusType -eq 'USB' -or $d.IsOffline -or $d.IsReadOnly) { throw 'Ổ cứng không phù hợp (USB, offline hoặc chỉ đọc).' }
if (Get-Volume -DriveLetter $nl -ErrorAction SilentlyContinue) { throw 'Ký tự ổ mới đã được dùng.' }
if ((Get-Volume -DriveLetter $l).FileSystem -ne 'NTFS') { throw 'Chỉ hỗ trợ NTFS.' }
$sup = Get-PartitionSupportedSize -DriveLetter $l
$old = [long]$p.Size
$target = $old - ($mb * 1MB)
if ($target -lt $sup.SizeMin) { throw 'Windows không thu nhỏ được nhiều như vậy (có file không di chuyển được). Hãy thử số nhỏ hơn.' }
Resize-Partition -DriveLetter $l -Size $target
$p2 = Get-Partition -DriveLetter $l
$freed = $old - [long]$p2.Size
try {
  $np = New-Partition -DiskNumber $p2.DiskNumber -Offset ($p2.Offset + $p2.Size) -Size $freed -DriveLetter $nl
  Format-Volume -DriveLetter $nl -FileSystem NTFS -NewFileSystemLabel $label -Confirm:$false | Out-Null
} catch {
  [Console]::Error.WriteLine('ĐÃ THU NHỎ ổ ' + $l + ': nhưng CHƯA tạo được phân vùng mới (' + $_.Exception.Message + '). Phần trống vẫn còn, hãy tạo ổ mới trong Disk Management.')
  exit 1
}
$v = Get-Volume -DriveLetter $nl
$s2 = Get-Partition -DriveLetter $l
[ordered]@{ src = $l; srcOld = $old; srcNew = [long]$s2.Size; newLetter = $nl; fs = "$($v.FileSystem)"; label = "$($v.FileSystemLabel)"; newSize = [long]$v.Size } | ConvertTo-Json -Compress
""";

    public static IReadOnlyList<string> Problems(string json, string src, string newLetter, long amountMb)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim().Split('\n').Last().Trim());
            var r = doc.RootElement;
            long N(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;
            string S(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var list = new List<string>();
            if (S("src") != src || S("newLetter") != newLetter) list.Add("Kết quả không khớp ổ đã chọn.");
            if (N("srcNew") < 0 || N("srcOld") < 0 || N("srcNew") >= N("srcOld")) list.Add("Ổ nguồn không thấy nhỏ đi.");
            if (!S("fs").Equals("NTFS", StringComparison.OrdinalIgnoreCase)) list.Add("Ổ mới chưa là NTFS.");
            if (N("newSize") < amountMb * 1048576 * 9 / 10) list.Add("Ổ mới nhỏ hơn dự kiến.");
            return list;
        }
        catch (JsonException) { return new[] { "Không đọc được kết quả tách ổ." }; }
    }

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang thu nhỏ ổ và tạo ổ mới (có thể mất vài phút, đừng tắt máy)...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không tách được ổ. Chưa có gì bị xóa." : r.Error.Trim());
        var problems = Problems(r.Output, _src.Letter, _newLetter, _mb);
        if (problems.Count > 0) return FixResult.Failed("Kết quả chưa đạt: " + string.Join(" ", problems) + " Hãy kiểm tra Disk Management.");
        _ok = true;
        return FixResult.Succeeded($"Đã tạo ổ {_newLetter}: ({_label}) từ {_src.Letter}:. Dữ liệu cũ vẫn còn nguyên.");
    }

    private bool _ok;
    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_ok);
}

/// <summary>Mở rộng một phân vùng vào vùng trống NẰM SÁT SAU nó (không xóa gì). Dùng sau khi bạn tự xóa/giải phóng phân vùng bên cạnh bằng Disk Management.</summary>
public sealed class ExtendIntoFreeSpaceFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly string _letter;
    private long _before;
    private bool _ok;

    public ExtendIntoFreeSpaceFix(VolumeInfo volume, string confirm, ScriptRunner? run = null)
    {
        if (!PartitionRules.IsLetter(volume.Letter)) throw new ArgumentException("Ký tự ổ không hợp lệ.", nameof(volume));
        if (volume.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) || volume.DiskDynamicOrOffline) throw new ArgumentException("Ổ này không chỉnh được.", nameof(volume));
        if (confirm != PartitionRules.Code("MO RONG", volume.Letter)) throw new ArgumentException($"Chưa gõ đúng mã xác nhận ({PartitionRules.Code("MO RONG", volume.Letter)}).", nameof(confirm));
        _letter = volume.Letter;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 1800));
    }

    public string Title => $"Mở rộng ổ {_letter}: vào vùng trống sát sau";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Mở rộng phân vùng {_letter}: ra hết vùng trống liền kề phía sau (nếu có)",
        "KHÔNG xóa, KHÔNG format gì. Nếu không có vùng trống liền kề thì báo và không làm gì",
        "Không thu nhỏ lại tự động được"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$l = '{{_letter}}'
$p = Get-Partition -DriveLetter $l
if (-not $p) { throw 'Không thấy ổ.' }
$sup = Get-PartitionSupportedSize -DriveLetter $l
$before = [long]$p.Size
if ($sup.SizeMax - $before -lt 1GB) { Write-Output 'NOSPACE'; exit 0 }
Resize-Partition -DriveLetter $l -Size $sup.SizeMax
$after = [long](Get-Partition -DriveLetter $l).Size
[ordered]@{ letter = $l; before = $before; after = $after } | ConvertTo-Json -Compress
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0) return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không mở rộng được ổ." : r.Error.Trim());
        var last = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (last == "NOSPACE") return FixResult.Failed("Không có vùng trống liền kề phía sau ổ này (từ 1 GB trở lên). Chưa làm gì.");
        try
        {
            using var doc = JsonDocument.Parse(last);
            var b = doc.RootElement.GetProperty("before").GetInt64();
            var a = doc.RootElement.GetProperty("after").GetInt64();
            if (doc.RootElement.GetProperty("letter").GetString() != _letter || a <= b) return FixResult.Failed("Ổ không thấy lớn lên. Hãy kiểm tra Disk Management.");
            _before = b; _ok = true;
            return FixResult.Succeeded($"Ổ {_letter}: tăng thêm {(a - b) / 1048576} MB.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { return FixResult.Failed("Không đọc được kết quả mở rộng."); }
    }

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(_ok && _before > 0);
}

/// <summary>Đổi ký tự một ổ DỮ LIỆU. Không cho đổi ổ Windows, ổ có Windows/khởi động, A, B, C. Cảnh báo: phần mềm/lối tắt trỏ ký tự cũ sẽ hỏng.</summary>
public sealed class ChangeLetterFix : IFix
{
    private readonly ScriptRunner _run;
    private readonly string _old, _new;

    public ChangeLetterFix(VolumeInfo volume, string newLetter, string confirm, ScriptRunner? run = null)
    {
        if (volume.IsSystem || volume.Letter == "C") throw new ArgumentException("Không đổi ký tự ổ Windows (C:).", nameof(volume));
        if (!PartitionRules.IsLetter(newLetter) || PartitionRules.LetterReserved(newLetter)) throw new ArgumentException("Ký tự mới không hợp lệ (không dùng A, B, C).", nameof(newLetter));
        if (newLetter == volume.Letter) throw new ArgumentException("Ký tự mới trùng ký tự cũ.", nameof(newLetter));
        if (confirm != PartitionRules.Code("DOI", volume.Letter)) throw new ArgumentException($"Chưa gõ đúng mã xác nhận ({PartitionRules.Code("DOI", volume.Letter)}).", nameof(confirm));
        _old = volume.Letter; _new = newLetter;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 120));
    }

    public string Title => $"Đổi ký tự ổ {_old}: thành {_new}:";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Đổi ký tự ổ {_old}: thành {_new}: (không đụng dữ liệu)",
        "Phần mềm, game, lối tắt đang trỏ tới ký tự cũ sẽ không tìm thấy file; hãy đóng chúng trước",
        "Hoàn tác bằng cách đổi ngược lại"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public string BuildScript() => $$"""
$ErrorActionPreference = 'Stop'
$o = '{{_old}}'; $n = '{{_new}}'
if (Get-Volume -DriveLetter $n -ErrorAction SilentlyContinue) { throw 'Ký tự mới đã được dùng.' }
if ($o -eq $env:SystemDrive.TrimEnd(':')) { throw 'Không đổi ký tự ổ Windows.' }
$p = Get-Partition -DriveLetter $o
if (-not $p) { throw 'Không thấy ổ.' }
Set-Partition -DriveLetter $o -NewDriveLetter $n
""";

    public const string CheckScript = "Get-Volume | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter }";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var r = await _run(BuildScript(), ct);
        return r.ExitCode == 0 ? FixResult.Succeeded($"Đã đổi {_old}: thành {_new}:.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không đổi được ký tự ổ." : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var r = await _run(CheckScript, ct);
        if (r.ExitCode != 0) return false;
        var letters = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return letters.Contains(_new) && !letters.Contains(_old);
    }
}
