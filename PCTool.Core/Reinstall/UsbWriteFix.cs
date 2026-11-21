using System.Text.Json;
using System.Text.RegularExpressions;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Reinstall;

/// <summary>Cách chia USB: phân vùng 1 FAT32 (khởi động + bộ cài), phân vùng 2 NTFS (dữ liệu/sao lưu).</summary>
public sealed record UsbLayout(long BootBytes, long DataBytes);

public static class UsbLayoutPlanner
{
    private const long GiB = 1L << 30;
    public const string BootLabel = "PCTOOL-BOOT";
    public const string DataLabel = "PCTOOL-DATA";

    /// <summary>
    /// Phân vùng FAT32 = dung lượng ISO + 1,5 GiB dự phòng, làm tròn lên GiB, tối thiểu 8, tối đa 30 GiB
    /// (Windows không format FAT32 lớn hơn 32 GB). Phần còn lại (trừ 1 GiB dự phòng) là phân vùng NTFS, phải còn ít nhất 2 GiB.
    /// </summary>
    public static UsbLayout? Plan(long diskSize, long isoSize, out string error)
    {
        error = "";
        if (diskSize <= 0 || isoSize <= 0) { error = "Không có dung lượng ổ hoặc ISO để tính cách chia."; return null; }
        var bootGiB = (long)Math.Ceiling((isoSize + 1.5 * GiB) / GiB);
        bootGiB = Math.Clamp(bootGiB, 8, 30);
        var boot = bootGiB * GiB;
        var data = diskSize - boot - GiB;
        if (data < 2 * GiB)
        {
            error = $"USB quá nhỏ để chứa bộ cài ({bootGiB} GiB) và phân vùng dữ liệu (tối thiểu 2 GiB). Hãy dùng USB lớn hơn.";
            return null;
        }
        return new UsbLayout(boot, data);
    }
}

/// <summary>Kiểm tra lại USB sau khi ghi (đọc kết quả từ script chỉ đọc). Hàm thuần để test được.</summary>
public static class UsbWriteVerifier
{
    public static IReadOnlyList<string> Problems(string json, int diskNumber, IsoInfo iso, bool split, bool expectAnswer = false)
    {
        var p = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new[] { "Không đọc được kết quả kiểm tra lại USB." }; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new[] { "Không đọc được kết quả kiểm tra lại USB." };

            if (Str(r, "style") != "GPT") p.Add("Bảng phân vùng không phải GPT.");
            if (Num(r, "bootDisk") != diskNumber || Num(r, "dataDisk") != diskNumber) p.Add("Phân vùng không nằm trên ổ USB đã chọn.");
            if (Str(r, "bootLabel") != UsbLayoutPlanner.BootLabel || Str(r, "bootFs") != "FAT32") p.Add("Phân vùng khởi động không đúng (cần FAT32, nhãn PCTOOL-BOOT).");
            if (Str(r, "dataLabel") != UsbLayoutPlanner.DataLabel || Str(r, "dataFs") != "NTFS") p.Add("Phân vùng dữ liệu không đúng (cần NTFS, nhãn PCTOOL-DATA).");
            if (!Bool(r, "efi")) p.Add("Thiếu efi\\boot\\bootx64.efi trên USB.");
            if (!Bool(r, "bootWim")) p.Add("Thiếu sources\\boot.wim trên USB.");
            if (expectAnswer && !Bool(r, "answer")) p.Add("Thiếu file autounattend.xml trên USB.");

            long expected = split ? iso.InstallWimBytes : iso.HasInstallWim ? iso.InstallWimBytes : iso.InstallEsdBytes;
            long actual = Num(r, "installBytes");
            if (split)
            {
                if (!Bool(r, "hasSwm")) p.Add("Chưa có các file install*.swm (tách install.wim).");
                if (Bool(r, "hasWim")) p.Add("Vẫn còn install.wim lớn trên USB FAT32 (không hợp lệ).");
                if (expected <= 0 || actual < expected * 0.95 || actual > expected * 1.05)
                    p.Add($"Tổng dung lượng file cài ({actual:N0} byte) lệch nhiều so với bản gốc ({expected:N0} byte).");
            }
            else
            {
                if (!Bool(r, "hasWim") && !Bool(r, "hasEsd")) p.Add("Thiếu file cài (install.wim/esd) trên USB.");
                if (expected <= 0 || actual != expected)
                    p.Add($"Dung lượng file cài ({actual:N0} byte) khác bản gốc ({expected:N0} byte).");
            }
        }
        return p;
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : -1;
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// GĐ2: ghi USB cài Windows (UEFI/GPT: FAT32 khởi động + NTFS dữ liệu). ĐÂY LÀ THAO TÁC XÓA DỮ LIỆU duy nhất của PCTool:
/// xóa toàn bộ ổ USB đã chọn. Bảo vệ nhiều lớp: chỉ nhận ổ USB đủ điều kiện, phải gõ đúng mã xác nhận (kiểm tra ngay khi tạo),
/// quét lại ngay trước khi xóa và so khớp số ổ/mã duy nhất/serial/dung lượng, script kiểm tra lại lần nữa ngay trước lệnh xóa.
/// Chỉ chạy qua FixExecutor (Chế độ an toàn và chạy thử được FixExecutor lo).
/// </summary>
public sealed class UsbWriteFix : IFix
{
    private static readonly Regex Letter = new("^[A-Z]$", RegexOptions.Compiled);
    private static readonly Regex OkLine = new(@"^OK\|([A-Z])\|([A-Z])$", RegexOptions.Compiled);

    private readonly UsbCandidate _candidate;
    private readonly string _typed;
    private readonly IsoInfo _iso;
    private readonly UsbScanContext _ctx;
    private readonly UsbLayout _layout;
    private readonly bool _split;
    private readonly UsbWriteOptions _options;
    private readonly string? _answerXml;
    private readonly ScriptRunner _run;
    private string? _boot, _data;

    public UsbWriteFix(UsbCandidate candidate, string typedConfirmation, IsoInfo iso, string? backupFolder = null, ScriptRunner? run = null, UsbWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(iso);
        var d = candidate.Disk;
        if (!UsbClassifier.IsUsb(d) || !candidate.Eligible)
            throw new ArgumentException("Ổ này không đủ điều kiện để ghi USB: " + string.Join(" ", candidate.Blockers), nameof(candidate));
        if (!UsbClassifier.ConfirmationMatches(d, typedConfirmation))
            throw new ArgumentException("Mã xác nhận chưa đúng.", nameof(typedConfirmation));
        if (!IsoInspector.IsValidIsoPath(iso.Path, out var perr)) throw new ArgumentException(perr, nameof(iso));
        var verdict = IsoInspector.Evaluate(iso);
        if (!verdict.UsableForUsb)
            throw new ArgumentException("ISO chưa dùng được: " + string.Join(" ", verdict.Blockers), nameof(iso));
        if (!EmbedSafe(d.UniqueId) || !EmbedSafe(d.Serial))
            throw new ArgumentException("Mã/serial của ổ có ký tự lạ, không dùng để kiểm tra lại an toàn được.", nameof(candidate));

        _options = options ?? UsbWriteOptions.Default;
        if (_options.Answer is { } ans)
        {
            var errs = AnswerFileBuilder.Validate(ans, iso);
            if (errs.Count > 0) throw new ArgumentException("File autounattend không hợp lệ: " + string.Join(" ", errs), nameof(options));
            _answerXml = AnswerFileBuilder.Build(ans);
        }

        _layout = UsbLayoutPlanner.Plan(d.Size, iso.IsoSizeBytes, out var lerr)
            ?? throw new ArgumentException(lerr, nameof(candidate));

        _candidate = candidate;
        _typed = typedConfirmation;
        _iso = iso;
        _split = iso.HasInstallWim && iso.InstallWimBytes > IsoInspector.Fat32MaxFile;
        _ctx = new UsbScanContext(iso.Path, backupFolder);
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 3600));
    }

    public UsbLayout Layout => _layout;
    public bool WillSplitWim => _split;

    public string Title => $"Ghi USB cài Windows lên ổ số {_candidate.Disk.Number}";

    public IReadOnlyList<string> Actions
    {
        get
        {
            var d = _candidate.Disk;
            var used = _candidate.UsedBytesUnknown ? "không rõ" : UsbReport.Size(_candidate.UsedBytes ?? 0);
            var list = new List<string>
            {
                $"XÓA TOÀN BỘ dữ liệu trên ổ USB số {d.Number}: {d.Name}, {UsbReport.Size(d.Size)}, serial {d.Serial} (đang dùng {used}). KHÔNG KHÔI PHỤC ĐƯỢC.",
                "Kiểm tra lại ổ ngay trước khi xóa; nếu ổ đã đổi (số ổ, mã, serial, dung lượng) thì HỦY, chưa xóa gì",
                $"Tạo bảng phân vùng GPT; phân vùng 1 FAT32 \"{UsbLayoutPlanner.BootLabel}\" {UsbReport.Size(_layout.BootBytes)} (khởi động + bộ cài); phân vùng 2 NTFS \"{UsbLayoutPlanner.DataLabel}\" khoảng {UsbReport.Size(_layout.DataBytes)}",
                $"Gắn tạm file ISO {_iso.Path} (chỉ đọc) và chép nội dung vào phân vùng 1" +
                    (_split ? "; install.wim lớn hơn 4 GB nên được tách thành các file .swm bằng DISM" : ""),
                "Kiểm tra lại USB: phân vùng, nhãn, bootx64.efi, boot.wim, dung lượng file cài",
                "Không đụng tới ổ nào khác, không đụng tới ổ Windows. Quá trình có thể mất 10-30 phút, đừng rút USB"
            };
            if (_options.Answer is { } a)
                list.Insert(4, $"Ghi file autounattend.xml tối thiểu vào USB (ngôn ngữ {a.Language}, chấp nhận điều khoản, bỏ qua màn nhập key" +
                    (a.EditionIndex is { } ei ? $", chọn sẵn phiên bản số {ei}" : "") +
                    (a.BypassWin11Checks ? "; BỎ QUA kiểm tra phần cứng Windows 11 (không chính thức, Microsoft có thể không hỗ trợ/cập nhật máy này)" : "") +
                    "). File này KHÔNG chọn và KHÔNG xóa ổ cài: bạn vẫn tự chọn ổ ở màn hình của Microsoft");
            return list;
        }
    }

    public bool RequiresRestorePoint => false;   // điểm khôi phục Windows không liên quan tới USB
    public bool IsUndoable => false;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var d = _candidate.Disk;

        // Lớp bảo vệ cuối (1): quét lại ngay bây giờ, ổ phải còn y nguyên và vẫn đủ điều kiện.
        progress.Report("Kiểm tra lại ổ USB trước khi xóa...");
        UsbScanResult now;
        try { now = await new UsbDriveScanner(_run).ScanAsync(_ctx, ct); }
        catch (InvalidOperationException ex)
        {
            return FixResult.Failed("Đã hủy, CHƯA xóa gì: không kiểm tra lại được ổ đĩa (" + ex.Message + ")");
        }
        var cur = now.Candidates.FirstOrDefault(c => c.Disk.Number == d.Number);
        if (cur is null || !cur.Eligible || !UsbClassifier.IsSameDisk(d, cur.Disk) || !UsbClassifier.ConfirmationMatches(cur.Disk, _typed))
        {
            Logger.Error($"Hủy ghi USB: ổ số {d.Number} đã thay đổi hoặc không còn đủ điều kiện (serial {d.Serial}).");
            return FixResult.Failed("Đã hủy, CHƯA xóa gì: ổ USB đã thay đổi (rút/cắm lại, đổi số ổ) hoặc không còn đủ điều kiện. Quét lại ổ USB rồi làm lại.");
        }

        Logger.Warn($"BẮT ĐẦU XÓA ổ USB số {d.Number} ({d.Name}, serial {d.Serial}, UniqueId {d.UniqueId}) để ghi bộ cài Windows.");

        // Bước 1: xóa + chia phân vùng + format. Script tự kiểm tra lại ổ một lần nữa ngay trước lệnh xóa.
        progress.Report("Đang xóa và chia lại phân vùng USB...");
        var r1 = await _run(BuildPartitionScript(), ct);
        var ok = LastLine(r1.Output) is { } l1 ? OkLine.Match(l1) : Match.Empty;
        if (r1.ExitCode != 0 || !ok.Success)
        {
            Logger.Error("Chia phân vùng USB lỗi: " + r1.Error + " | " + r1.Output);
            return FixResult.Failed("Không chia được phân vùng USB: " + Short(r1.Error, r1.Output) +
                " (USB có thể đã bị xóa một phần; hãy thử ghi lại hoặc dùng USB khác.)");
        }
        _boot = ok.Groups[1].Value;
        _data = ok.Groups[2].Value;
        if (!Letter.IsMatch(_boot) || !Letter.IsMatch(_data) || _boot == _data)
            return FixResult.Failed("Không đọc được ký tự ổ của USB sau khi chia phân vùng.");
        Logger.Info($"Đã chia USB: {_boot}: (khởi động), {_data}: (dữ liệu).");

        // Bước 2: chép bộ cài.
        progress.Report($"Đang chép bộ cài Windows vào {_boot}: (10-30 phút, đừng rút USB)...");
        var r2 = await _run(BuildCopyScript(_boot), ct);
        if (r2.ExitCode != 0 || LastLine(r2.Output) != "OK")
        {
            Logger.Error("Chép bộ cài lỗi: " + r2.Error + " | " + r2.Output);
            return FixResult.Failed("Chép bộ cài vào USB không thành công: " + Short(r2.Error, r2.Output) +
                " USB hiện chưa dùng được, hãy ghi lại.");
        }

        if (_answerXml is not null)
        {
            progress.Report("Đang ghi file autounattend.xml...");
            var r3 = await _run(BuildAnswerScript(_boot), ct);
            if (r3.ExitCode != 0 || LastLine(r3.Output) != "OK")
            {
                Logger.Error("Ghi autounattend lỗi: " + r3.Error + " | " + r3.Output);
                return FixResult.Failed("Ghi file autounattend.xml không thành công: " + Short(r3.Error, r3.Output) + " USB chưa dùng được, hãy ghi lại.");
            }
        }

        Logger.Info("Đã chép bộ cài vào USB, chờ kiểm tra lại.");
        return FixResult.Succeeded($"Đã ghi USB cài Windows (khởi động {_boot}:, dữ liệu {_data}:).");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (_boot is null || _data is null) return false;
        var r = await _run(BuildVerifyScript(_boot, _data), ct);
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Output)) return false;
        var problems = UsbWriteVerifier.Problems(LastLine(r.Output) ?? "", _candidate.Disk.Number, _iso, _split, _answerXml is not null);
        foreach (var p in problems) Logger.Warn("Kiểm tra lại USB: " + p);
        return problems.Count == 0;
    }

    // ------------------------------------------------------------------ Script

    public string BuildPartitionScript()
    {
        var d = _candidate.Disk;
        return $$"""
$ErrorActionPreference = 'Stop'
$n = {{d.Number}}
$disk = Get-Disk -Number $n
if ($null -eq $disk) { throw 'Không thấy ổ đã chọn.' }
if ("$($disk.BusType)" -ne 'USB') { throw 'Ổ đã chọn không còn là ổ USB.' }
if ($disk.IsSystem -or $disk.IsBoot) { throw 'Ổ đã chọn là ổ hệ thống/khởi động.' }
if ($disk.IsOffline -or $disk.IsReadOnly) { throw 'Ổ đang Offline hoặc chỉ đọc.' }
if ([long]$disk.Size -ne {{d.Size}}) { throw 'Dung lượng ổ đã thay đổi.' }
if ("$($disk.UniqueId)".Trim() -ne '{{d.UniqueId}}') { throw 'Mã duy nhất của ổ đã thay đổi.' }
if ("$($disk.SerialNumber)".Trim() -ne '{{d.Serial}}') { throw 'Serial của ổ đã thay đổi.' }
$sys = ($env:SystemDrive -replace ':', '')
$hit = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter -and "$($_.DriveLetter)" -eq $sys })
if ($hit.Count -gt 0) { throw 'Ổ này chứa ổ Windows đang chạy.' }
if ("$($disk.PartitionStyle)" -ne 'RAW') { Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false }
Initialize-Disk -Number $n -PartitionStyle GPT
$p1 = New-Partition -DiskNumber $n -Size {{_layout.BootBytes}} -AssignDriveLetter
$v1 = $p1 | Format-Volume -FileSystem FAT32 -NewFileSystemLabel '{{UsbLayoutPlanner.BootLabel}}' -Confirm:$false
$p2 = New-Partition -DiskNumber $n -UseMaximumSize -AssignDriveLetter
$v2 = $p2 | Format-Volume -FileSystem NTFS -NewFileSystemLabel '{{UsbLayoutPlanner.DataLabel}}' -Confirm:$false
"OK|$($v1.DriveLetter)|$($v2.DriveLetter)"
""";
    }

    public string BuildCopyScript(string bootLetter)
    {
        if (!Letter.IsMatch(bootLetter)) throw new ArgumentException("Ký tự ổ không hợp lệ.", nameof(bootLetter));
        var splitPs = _split ? "$true" : "$false";
        return $$"""
$ErrorActionPreference = 'Stop'
$n = {{_candidate.Disk.Number}}
$boot = '{{bootLetter}}'
$iso = '{{_iso.Path}}'
if ((Get-Partition -DriveLetter $boot).DiskNumber -ne $n) { throw 'Phân vùng đích không nằm trên ổ USB đã chọn.' }
$bv = Get-Volume -DriveLetter $boot
if ($bv.FileSystemLabel -ne '{{UsbLayoutPlanner.BootLabel}}' -or "$($bv.FileSystem)" -ne 'FAT32') { throw 'Phân vùng đích không đúng.' }
$wasMounted = [bool](Get-DiskImage -ImagePath $iso).Attached
if (-not $wasMounted) { Mount-DiskImage -ImagePath $iso -StorageType ISO -Access ReadOnly | Out-Null }
try {
  $letter = "$((Get-DiskImage -ImagePath $iso | Get-Volume).DriveLetter)"
  if (-not $letter) { throw 'Không đọc được ổ ảo của file ISO.' }
  $src = $letter + ':\'
  $dst = $boot + ':\'
  $split = {{splitPs}}
  $rc = @($src, $dst, '/E', '/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
  if ($split) { $rc += @('/XF', 'install.wim') }
  & robocopy @rc | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy báo lỗi (mã $LASTEXITCODE)" }
  if ($split) {
    Split-WindowsImage -ImagePath ($src + 'sources\install.wim') -SplitImagePath ($dst + 'sources\install.swm') -FileSize 3800 | Out-Null
  }
} finally {
  if (-not $wasMounted) { Dismount-DiskImage -ImagePath $iso | Out-Null }
}
'OK'
""";
    }

    public string BuildAnswerScript(string bootLetter)
    {
        if (!Letter.IsMatch(bootLetter)) throw new ArgumentException("Ký tự ổ không hợp lệ.", nameof(bootLetter));
        if (_answerXml is null) throw new InvalidOperationException("Không có file autounattend để ghi.");
        var b64 = Convert.ToBase64String(new System.Text.UTF8Encoding(false).GetBytes(_answerXml));
        return $$"""
$ErrorActionPreference = 'Stop'
$boot = '{{bootLetter}}'
if ((Get-Partition -DriveLetter $boot).DiskNumber -ne {{_candidate.Disk.Number}}) { throw 'Phân vùng đích không nằm trên ổ USB đã chọn.' }
$dst = $boot + ':\autounattend.xml'
[System.IO.File]::WriteAllBytes($dst, [System.Convert]::FromBase64String('{{b64}}'))
if (-not (Test-Path -LiteralPath $dst)) { throw 'Không ghi được autounattend.xml.' }
'OK'
""";
    }

    public string BuildVerifyScript(string bootLetter, string dataLetter)
    {
        if (!Letter.IsMatch(bootLetter) || !Letter.IsMatch(dataLetter)) throw new ArgumentException("Ký tự ổ không hợp lệ.");
        return $$"""
$ErrorActionPreference = 'Stop'
$b = '{{bootLetter}}'
$dl = '{{dataLetter}}'
$bp = Get-Partition -DriveLetter $b
$dp = Get-Partition -DriveLetter $dl
$bv = Get-Volume -DriveLetter $b
$dv = Get-Volume -DriveLetter $dl
$root = $b + ':\'
$inst = @(Get-ChildItem -LiteralPath ($root + 'sources') -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'install*.swm' -or $_.Name -eq 'install.wim' -or $_.Name -eq 'install.esd' })
$sum = 0
foreach ($f in $inst) { $sum += $f.Length }
[ordered]@{
  style = "$((Get-Disk -Number $bp.DiskNumber).PartitionStyle)"
  bootDisk = [int]$bp.DiskNumber
  dataDisk = [int]$dp.DiskNumber
  bootLabel = "$($bv.FileSystemLabel)"
  bootFs = "$($bv.FileSystem)"
  dataLabel = "$($dv.FileSystemLabel)"
  dataFs = "$($dv.FileSystem)"
  efi = [bool](Test-Path -LiteralPath ($root + 'efi\boot\bootx64.efi'))
  answer = [bool](Test-Path -LiteralPath ($root + 'autounattend.xml'))
  bootWim = [bool](Test-Path -LiteralPath ($root + 'sources\boot.wim'))
  hasSwm = [bool](@($inst | Where-Object { $_.Name -like '*.swm' }).Count -gt 0)
  hasWim = [bool](@($inst | Where-Object { $_.Name -eq 'install.wim' }).Count -gt 0)
  hasEsd = [bool](@($inst | Where-Object { $_.Name -eq 'install.esd' }).Count -gt 0)
  installBytes = [long]$sum
} | ConvertTo-Json -Compress
""";
    }

    // ------------------------------------------------------------------ Hỗ trợ

    /// <summary>Chuỗi đặt trong dấu nháy đơn của PowerShell: chỉ ký tự ASCII in được, không có dấu nháy đơn.</summary>
    public static bool EmbedSafe(string s) =>
        s.Length is > 0 and <= 200 && s.All(c => c >= 0x20 && c <= 0x7E && c != '\'');

    private static string? LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static string Short(string err, string output)
    {
        var t = string.IsNullOrWhiteSpace(err) ? output : err;
        t = t.Trim();
        return t.Length > 300 ? t[..300] + "..." : (t.Length == 0 ? "không rõ lý do" : t);
    }
}
