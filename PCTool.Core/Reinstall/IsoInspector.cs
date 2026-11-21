using System.Globalization;
using System.Text;
using System.Text.Json;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Reinstall;

public sealed record IsoImage(int Index, string Name, string EditionId, string Version, string Architecture, IReadOnlyList<string> Languages, long SizeBytes)
{
    /// <summary>Số build lấy từ phiên bản dạng "10.0.22631.xxxx"; 0 nếu không đọc được.</summary>
    public int Build => Version.Split('.') is { Length: >= 3 } p && int.TryParse(p[2], out var b) ? b : 0;

    public string Family => Build >= 22000 ? "Windows 11" : Build >= 10240 ? "Windows 10" : Build > 0 ? $"Windows (build {Build})" : "Windows (không rõ phiên bản)";

    /// <summary>"x64", "x86", "arm64" hoặc "" nếu không rõ. Chấp nhận cả số mã của DISM (9, 0, 12).</summary>
    public string Arch => Architecture.Trim().ToLowerInvariant() switch
    {
        "9" or "x64" or "amd64" => "x64",
        "0" or "x86" => "x86",
        "12" or "arm64" => "arm64",
        _ => ""
    };
}

public sealed record IsoInfo(
    string Path, long IsoSizeBytes, bool WasAlreadyMounted, bool HasInstallWim, long InstallWimBytes,
    bool HasInstallEsd, long InstallEsdBytes, bool HasEfiBoot, IReadOnlyList<IsoImage> Images);

public sealed record IsoVerdict(IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes)
{
    public bool UsableForUsb => Blockers.Count == 0;
}

/// <summary>
/// GĐ1: kiểm tra file ISO Windows (chỉ đọc nội dung). Có gắn ISO tạm thời ở chế độ chỉ đọc rồi gỡ ra
/// (không gỡ nếu bạn đã gắn sẵn từ trước). Không ghi gì vào ISO hay vào ổ nào.
/// </summary>
public sealed class IsoInspector
{
    public const long Fat32MaxFile = 4_294_967_295L;
    private const string PathToken = "@@ISO_PATH@@";

    public const string ScriptTemplate = """
$ErrorActionPreference = 'Stop'
$path = '@@ISO_PATH@@'
$o = [ordered]@{ ok = $false; error = ''; wasMounted = $false; isoSize = 0; hasWim = $false; wimSize = 0; hasEsd = $false; esdSize = 0; hasEfi = $false; images = @() }
try {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Không tìm thấy file ISO.' }
  $o.isoSize = (Get-Item -LiteralPath $path).Length
  $o.wasMounted = [bool](Get-DiskImage -ImagePath $path).Attached
  if (-not $o.wasMounted) { Mount-DiskImage -ImagePath $path -StorageType ISO -Access ReadOnly | Out-Null }
  try {
    $letter = "$((Get-DiskImage -ImagePath $path | Get-Volume).DriveLetter)"
    if (-not $letter) { throw 'Không đọc được ổ ảo của file ISO.' }
    $root = $letter + ':\'
    $wim = $root + 'sources\install.wim'
    $esd = $root + 'sources\install.esd'
    $o.hasEfi = Test-Path -LiteralPath ($root + 'efi\boot\bootx64.efi')
    $src = $null
    if (Test-Path -LiteralPath $wim) { $o.hasWim = $true; $o.wimSize = (Get-Item -LiteralPath $wim).Length; $src = $wim }
    if (Test-Path -LiteralPath $esd) { $o.hasEsd = $true; $o.esdSize = (Get-Item -LiteralPath $esd).Length; if (-not $src) { $src = $esd } }
    if ($src) {
      $list = @()
      foreach ($i in @(Get-WindowsImage -ImagePath $src)) {
        $d = Get-WindowsImage -ImagePath $src -Index $i.ImageIndex -ErrorAction SilentlyContinue
        $list += [ordered]@{
          Index = [int]$i.ImageIndex
          Name = "$($i.ImageName)"
          EditionId = "$($d.EditionId)"
          Version = "$($d.Version)"
          Architecture = "$($d.Architecture)"
          Languages = @($d.Languages | ForEach-Object { "$_" })
          Size = [long]$i.ImageSize
        }
      }
      $o.images = $list
    }
  } finally {
    if (-not $o.wasMounted) { Dismount-DiskImage -ImagePath $path | Out-Null }
  }
  $o.ok = $true
} catch { $o.error = "$($_.Exception.Message)" }
$o | ConvertTo-Json -Depth 5 -Compress
""";

    private readonly ScriptRunner _run;

    public IsoInspector() : this((s, ct) => PowerShellRunner.RunAsync(s, ct, 180)) { }

    public IsoInspector(ScriptRunner run) => _run = run;

    /// <summary>Chỉ nhận đường dẫn kiểu "D:\Thư mục\file.iso" với ký tự an toàn, để đặt trong dấu nháy đơn của PowerShell.</summary>
    public static bool IsValidIsoPath(string? path, out string error)
    {
        if (!BackupPath.IsValid(path, out error)) return false;
        if (!path!.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || path.EndsWith('\\') || path.Length <= 7)
        {
            error = "Phải là một file có đuôi .iso, ví dụ E:\\Win11.iso.";
            return false;
        }
        error = "";
        return true;
    }

    public async Task<IsoInfo> InspectAsync(string path, CancellationToken ct)
    {
        if (!IsValidIsoPath(path, out var err)) throw new ArgumentException(err, nameof(path));
        var script = ScriptTemplate.Replace(PathToken, path);
        var result = await _run(script, ct);
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException("Không kiểm tra được file ISO: " +
                (string.IsNullOrWhiteSpace(result.Error) ? "PowerShell không trả về dữ liệu." : result.Error.Trim()));
        try { return Parse(path, result.Output); }
        catch (FormatException ex)
        {
            Logger.Warn("Kết quả kiểm tra ISO không đọc được: " + ex.Message);
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public static IsoInfo Parse(string path, string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Kết quả kiểm tra ISO không đúng định dạng.", ex); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("Kết quả kiểm tra ISO không đúng định dạng.");
            if (!Bool(r, "ok"))
            {
                var e = Str(r, "error");
                throw new FormatException("Không kiểm tra được file ISO: " + (e.Length > 0 ? e : "lỗi không rõ."));
            }

            var imgs = new List<IsoImage>();
            if (r.TryGetProperty("images", out var arr))
            {
                var items = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList()
                          : arr.ValueKind == JsonValueKind.Object ? new List<JsonElement> { arr } : new List<JsonElement>();
                foreach (var i in items)
                {
                    if (i.ValueKind != JsonValueKind.Object) continue;
                    var langs = new List<string>();
                    if (i.TryGetProperty("Languages", out var l))
                    {
                        if (l.ValueKind == JsonValueKind.Array) langs.AddRange(l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? ""));
                        else if (l.ValueKind == JsonValueKind.String) langs.Add(l.GetString() ?? "");
                    }
                    imgs.Add(new IsoImage((int)Num(i, "Index"), Str(i, "Name"), Str(i, "EditionId"), Str(i, "Version"),
                        Str(i, "Architecture"), langs.Where(x => x.Length > 0).ToList(), Num(i, "Size")));
                }
            }
            return new IsoInfo(path, Num(r, "isoSize"), Bool(r, "wasMounted"), Bool(r, "hasWim"), Num(r, "wimSize"),
                Bool(r, "hasEsd"), Num(r, "esdSize"), Bool(r, "hasEfi"), imgs);
        }
    }

    public static IsoVerdict Evaluate(IsoInfo i)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var notes = new List<string>();

        if (!i.HasInstallWim && !i.HasInstallEsd)
            blockers.Add("Không thấy sources\\install.wim hay install.esd: đây không phải ISO cài Windows.");
        if (!i.HasEfiBoot)
            blockers.Add("Không thấy efi\\boot\\bootx64.efi: ISO này không khởi động được bằng UEFI (bản này chỉ hỗ trợ UEFI x64).");

        if (i.HasInstallWim)
        {
            if (i.InstallWimBytes > Fat32MaxFile)
                notes.Add($"install.wim lớn hơn 4 GB ({UsbReport.Size(i.InstallWimBytes)}): khi ghi USB sẽ được tách bằng DISM thành các file .swm.");
        }
        else if (i.HasInstallEsd)
        {
            if (i.InstallEsdBytes > Fat32MaxFile)
                blockers.Add("ISO dùng install.esd lớn hơn 4 GB: chưa hỗ trợ ở bản này (không chép được vào USB FAT32). Hãy tải ISO khác từ Microsoft.");
            else
                warnings.Add("ISO dùng install.esd (không phải install.wim): chưa được thử, hãy ưu tiên ISO chuẩn của Microsoft.");
        }

        if (i.Images.Count == 0 && (i.HasInstallWim || i.HasInstallEsd))
            blockers.Add("Đọc được file cài nhưng không lấy được danh sách edition (file có thể bị hỏng): tải lại ISO.");
        foreach (var img in i.Images)
            if (img.Arch.Length > 0 && img.Arch != "x64")
            {
                blockers.Add($"Có bản {img.Arch} ({img.Name}): bản này chỉ hỗ trợ ISO x64.");
                break;
            }
        if (i.Images.Any(x => x.Arch.Length == 0))
            warnings.Add("Không đọc được kiến trúc (x64/x86) của một số edition.");

        var fams = i.Images.Select(x => x.Family).Distinct().ToList();
        if (fams.Count > 1) warnings.Add("ISO có nhiều đời Windows lẫn lộn: " + string.Join(", ", fams) + ". ISO chính thức thường chỉ có một.");
        if (i.WasAlreadyMounted) notes.Add("ISO này bạn đã gắn sẵn từ trước, app để nguyên không gỡ.");
        notes.Add("Ghi chú: ISO phải tải từ trang chính thức của Microsoft; app không kiểm tra được ISO có bị sửa đổi hay không.");
        return new IsoVerdict(blockers, warnings, notes);
    }

    public static string ToText(IsoInfo i, IsoVerdict v)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File: {i.Path}  ({UsbReport.Size(i.IsoSizeBytes)})");
        sb.AppendLine($"Khởi động UEFI (efi\\boot\\bootx64.efi): {(i.HasEfiBoot ? "có" : "KHÔNG")}");
        if (i.HasInstallWim) sb.AppendLine($"install.wim: có, {UsbReport.Size(i.InstallWimBytes)}");
        if (i.HasInstallEsd) sb.AppendLine($"install.esd: có, {UsbReport.Size(i.InstallEsdBytes)}");
        if (!i.HasInstallWim && !i.HasInstallEsd) sb.AppendLine("File cài (install.wim/esd): KHÔNG thấy");
        sb.AppendLine();
        if (i.Images.Count > 0)
        {
            sb.AppendLine("Các edition trong ISO:");
            foreach (var m in i.Images)
            {
                var lang = m.Languages.Count > 0 ? string.Join(", ", m.Languages) : "không rõ ngôn ngữ";
                sb.AppendLine($"  {m.Index}. {m.Name}  [{m.Family}, {(m.Arch.Length > 0 ? m.Arch : "kiến trúc?")}, {lang}, build {(m.Build > 0 ? m.Build.ToString(CultureInfo.InvariantCulture) : "?")}]");
            }
            sb.AppendLine();
        }
        foreach (var b in v.Blockers) sb.AppendLine("X KHÔNG DÙNG ĐƯỢC: " + b);
        foreach (var w in v.Warnings) sb.AppendLine("! " + w);
        foreach (var n in v.Notes) sb.AppendLine("- " + n);
        sb.AppendLine();
        sb.AppendLine(v.UsableForUsb ? "KẾT LUẬN: ISO này dùng được để làm USB cài (UEFI x64)." : "KẾT LUẬN: ISO này CHƯA dùng được để làm USB cài.");
        sb.AppendLine("Mới chỉ kiểm tra. Không có gì bị ghi hay xóa.");
        return sb.ToString();
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}
