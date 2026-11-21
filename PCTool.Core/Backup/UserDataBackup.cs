using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Reinstall;
using PCTool.Core.Windows;

namespace PCTool.Core.Backup;

public enum UserFolder { Desktop, Documents, Downloads, Pictures, Videos, Music }

public static class UserFolders
{
    public static string Vietnamese(UserFolder f) => f switch
    {
        UserFolder.Desktop => "Desktop (màn hình nền)",
        UserFolder.Documents => "Documents (tài liệu)",
        UserFolder.Downloads => "Downloads (tải về)",
        UserFolder.Pictures => "Pictures (ảnh)",
        UserFolder.Videos => "Videos (video)",
        _ => "Music (nhạc)"
    };

    /// <summary>Tên giá trị trong registry "User Shell Folders" (đúng cả khi thư mục bị chuyển sang ổ khác/OneDrive).</summary>
    public static string RegistryValue(UserFolder f) => f switch
    {
        UserFolder.Desktop => "Desktop",
        UserFolder.Documents => "Personal",
        UserFolder.Downloads => "{374DE290-123F-4565-9164-39C4925E467B}",
        UserFolder.Pictures => "My Pictures",
        UserFolder.Videos => "My Video",
        _ => "My Music"
    };
}

/// <summary>Kết quả quét một thư mục người dùng (chỉ đọc).</summary>
public sealed record UserFolderScan(UserFolder Folder, string Path, bool Exists, long Files, long Bytes, long CloudFiles, long CloudBytes);

public sealed record UserDataScan(string UserProfile, int SystemDisk, int DestDisk, long DestFreeBytes, IReadOnlyList<UserFolderScan> Folders);

/// <summary>Kế hoạch sao lưu sau khi cân nhắc: thư mục nào chép, thư mục nào bỏ qua, có điều gì chặn.</summary>
public sealed record UserDataPlan(
    IReadOnlyList<UserFolderScan> Copy, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings, IReadOnlyList<string> Skipped,
    long TotalFiles, long TotalBytes, long NeedBytes)
{
    public bool CanRun => Blockers.Count == 0 && Copy.Count > 0;
}

public static class UserDataShared
{
    /// <summary>Hàm PowerShell dùng chung: duyệt cây thư mục, bỏ qua junction/symlink và thùng rác, tách riêng file chỉ nằm trên đám mây (OneDrive).</summary>
    public const string Functions = """
function Scan-Tree([string]$root) {
  $files = 0; $bytes = [long]0; $cf = 0; $cb = [long]0
  $stack = New-Object System.Collections.Generic.Stack[string]
  $stack.Push($root)
  while ($stack.Count -gt 0) {
    $d = $stack.Pop()
    foreach ($i in @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)) {
      $a = [int]$i.Attributes
      if ($i.PSIsContainer) {
        if (($a -band 1024) -ne 0) { continue }
        if ($i.Name -eq '$RECYCLE.BIN' -or $i.Name -eq 'System Volume Information') { continue }
        $stack.Push($i.FullName)
      } else {
        if (($a -band 4460544) -ne 0) { $cf++; $cb += $i.Length } else { $files++; $bytes += $i.Length }
      }
    }
  }
  [pscustomobject]@{ Files = [long]$files; Bytes = [long]$bytes; CloudFiles = [long]$cf; CloudBytes = [long]$cb }
}
function Get-UserFolder([string]$valueName) {
  $k = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
  $v = (Get-ItemProperty -Path $k -ErrorAction SilentlyContinue).$valueName
  if (-not $v) { return $null }
  [Environment]::ExpandEnvironmentVariables($v)
}
""";

    public static string RegistryMapScript() =>
        "$map = @{ " + string.Join("; ", Enum.GetValues<UserFolder>().Select(f => $"{f} = '{UserFolders.RegistryValue(f)}'")) + " }";

    public static string NamesArray(IEnumerable<UserFolder> folders) =>
        "@(" + string.Join(", ", folders.Select(f => $"'{f}'")) + ")";

    /// <summary>Chữ cái ổ đĩa của đường dẫn đã kiểm tra bằng <see cref="BackupPath"/>.</summary>
    public static string DriveLetter(string validatedPath) => char.ToUpperInvariant(validatedPath[0]).ToString();
}

/// <summary>Quét kích thước các thư mục người dùng và thông tin ổ đích. CHỈ ĐỌC, không chép, không ghi.</summary>
public sealed class UserDataScanner
{
    private readonly ScriptRunner _run;
    public UserDataScanner() : this((s, ct) => PowerShellRunner.RunAsync(s, ct, 900)) { }
    public UserDataScanner(ScriptRunner run) => _run = run;

    public static string BuildScript(IEnumerable<UserFolder> folders, string destRoot)
    {
        if (!BackupPath.IsValid(destRoot, out var err)) throw new ArgumentException(err, nameof(destRoot));
        return UserDataShared.Functions + $$"""

$ErrorActionPreference = 'Stop'
{{UserDataShared.RegistryMapScript()}}
$sysL = ($env:SystemDrive -replace ':', '')
$dl = '{{UserDataShared.DriveLetter(destRoot)}}'
$sd = -1; $dd = -1; $free = [long]0
try { $sd = [int](Get-Partition -DriveLetter $sysL -ErrorAction Stop).DiskNumber } catch { }
try { $dd = [int](Get-Partition -DriveLetter $dl -ErrorAction Stop).DiskNumber } catch { }
try { $free = [long](Get-Volume -DriveLetter $dl -ErrorAction Stop).SizeRemaining } catch { }
$list = @()
foreach ($name in {{UserDataShared.NamesArray(folders)}}) {
  $p = Get-UserFolder $map[$name]
  if ($p -and (Test-Path -LiteralPath $p)) {
    $s = Scan-Tree $p
    $list += [ordered]@{ name = $name; path = $p; exists = $true; files = $s.Files; bytes = $s.Bytes; cloudFiles = $s.CloudFiles; cloudBytes = $s.CloudBytes }
  } else {
    $list += [ordered]@{ name = $name; path = "$p"; exists = $false; files = 0; bytes = 0; cloudFiles = 0; cloudBytes = 0 }
  }
}
[ordered]@{ userProfile = $env:USERPROFILE; systemDisk = $sd; destDisk = $dd; destFree = $free; folders = $list } | ConvertTo-Json -Depth 4 -Compress
""";
    }

    public async Task<UserDataScan> ScanAsync(IReadOnlyCollection<UserFolder> folders, string destRoot, CancellationToken ct)
    {
        if (folders.Count == 0) throw new ArgumentException("Chưa chọn thư mục nào.", nameof(folders));
        var r = await _run(BuildScript(folders, destRoot), ct);
        if (string.IsNullOrWhiteSpace(r.Output))
            throw new InvalidOperationException("Không quét được thư mục: " + (string.IsNullOrWhiteSpace(r.Error) ? "PowerShell không trả về dữ liệu." : r.Error.Trim()));
        try { return Parse(r.Output); }
        catch (FormatException ex) { throw new InvalidOperationException(ex.Message, ex); }
    }

    public static UserDataScan Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Kết quả quét thư mục không đúng định dạng.", ex); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("Kết quả quét thư mục không đúng định dạng.");
            var list = new List<UserFolderScan>();
            if (r.TryGetProperty("folders", out var arr))
            {
                var items = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList()
                          : arr.ValueKind == JsonValueKind.Object ? new List<JsonElement> { arr } : new List<JsonElement>();
                foreach (var f in items)
                {
                    if (f.ValueKind != JsonValueKind.Object || !Enum.TryParse<UserFolder>(Str(f, "name"), out var uf)) continue;
                    list.Add(new UserFolderScan(uf, Str(f, "path"), Bool(f, "exists"), Num(f, "files"), Num(f, "bytes"), Num(f, "cloudFiles"), Num(f, "cloudBytes")));
                }
            }
            return new UserDataScan(Str(r, "userProfile"), (int)Num(r, "systemDisk", -1), (int)Num(r, "destDisk", -1), Num(r, "destFree", -1), list);
        }
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement e, string n, long d = 0) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : d;
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
}

public static class UserDataPlanner
{
    private const long GiB = 1L << 30;

    public static UserDataPlan Evaluate(UserDataScan scan, string destRoot)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var skipped = new List<string>();
        var copy = new List<UserFolderScan>();

        if (!BackupPath.IsValid(destRoot, out var perr)) blockers.Add(perr);

        // Lớp bảo vệ: nơi lưu phải trên ổ vật lý KHÁC ổ Windows, và phải xác minh được điều đó.
        if (scan.SystemDisk < 0 || scan.DestDisk < 0)
            blockers.Add("Không xác định được ổ vật lý của ổ Windows hoặc nơi lưu (ổ mạng/ổ ảo?), nên không thể bảo đảm bản sao nằm ngoài ổ Windows. Hãy chọn ổ cứng/USB rời khác.");
        else if (scan.SystemDisk == scan.DestDisk)
            blockers.Add("Nơi lưu nằm trên CÙNG ổ vật lý với Windows: cài lại Windows có thể làm mất luôn bản sao lưu. Hãy chọn ổ khác (ổ D vật lý riêng, USB, ổ cứng ngoài).");

        foreach (var f in scan.Folders)
        {
            var name = UserFolders.Vietnamese(f.Folder);
            if (!f.Exists) { skipped.Add($"{name}: không tìm thấy thư mục."); continue; }
            if (f.CloudFiles > 0)
            {
                skipped.Add($"{name}: có {f.CloudFiles:N0} file chỉ nằm trên đám mây (OneDrive, {UsbReport.Size(f.CloudBytes)}), sao lưu sẽ phải tải về nên BỎ QUA thư mục này. Hãy chọn 'Luôn giữ trên thiết bị này' cho thư mục đó trong OneDrive rồi quét lại, hoặc sao lưu tay.");
                continue;
            }
            if (IsInside(destRoot, f.Path))
            {
                blockers.Add($"Nơi lưu nằm bên trong thư mục cần sao lưu ({f.Path}): sẽ chép lặp vô hạn. Chọn nơi khác.");
                continue;
            }
            if (f.Files == 0) { skipped.Add($"{name}: trống, không cần chép."); continue; }
            copy.Add(f);
        }

        var files = copy.Sum(c => c.Files);
        var bytes = copy.Sum(c => c.Bytes);
        var need = (long)(bytes * 1.05) + GiB;

        if (scan.DestFreeBytes < 0) blockers.Add("Không đọc được dung lượng trống của nơi lưu.");
        else if (copy.Count > 0 && scan.DestFreeBytes < need)
            blockers.Add($"Nơi lưu không đủ chỗ: cần khoảng {UsbReport.Size(need)} (dữ liệu {UsbReport.Size(bytes)} + dự phòng), chỉ còn {UsbReport.Size(scan.DestFreeBytes)}.");

        if (copy.Count == 0 && blockers.Count == 0) blockers.Add("Không có thư mục nào để sao lưu.");
        if (copy.Count > 0) warnings.Add("Bản sao chứa dữ liệu riêng tư của bạn (tài liệu, ảnh...): giữ ở nơi an toàn. App không sao lưu mật khẩu trình duyệt hay khóa.");
        warnings.Add($"Chỉ sao lưu tài khoản đang chạy app ({(scan.UserProfile.Length > 0 ? scan.UserProfile : "không rõ")}). Tài khoản Windows khác trên máy chưa được hỗ trợ.");
        return new UserDataPlan(copy, blockers, warnings, skipped, files, bytes, need);
    }

    /// <summary>true nếu <paramref name="child"/> trùng hoặc nằm bên trong <paramref name="parent"/> (so khớp theo ranh giới thư mục).</summary>
    public static bool IsInside(string child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return false;
        var c = child.TrimEnd('\\');
        var p = parent.TrimEnd('\\');
        return c.Equals(p, StringComparison.OrdinalIgnoreCase) || c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string ToText(UserDataScan scan, UserDataPlan plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Tài khoản: {scan.UserProfile}");
        sb.AppendLine();
        foreach (var f in scan.Folders)
            sb.AppendLine($"- {UserFolders.Vietnamese(f.Folder)}: " + (f.Exists
                ? $"{f.Files:N0} file, {UsbReport.Size(f.Bytes)}" + (f.CloudFiles > 0 ? $" (+{f.CloudFiles:N0} file chỉ trên đám mây)" : "")
                : "không tìm thấy"));
        sb.AppendLine();
        sb.AppendLine($"Sẽ chép: {plan.Copy.Count} thư mục, {plan.TotalFiles:N0} file, {UsbReport.Size(plan.TotalBytes)}.");
        foreach (var s in plan.Skipped) sb.AppendLine("Bỏ qua: " + s);
        foreach (var w in plan.Warnings) sb.AppendLine("! " + w);
        foreach (var b in plan.Blockers) sb.AppendLine("X KHÔNG SAO LƯU ĐƯỢC: " + b);
        sb.AppendLine();
        sb.AppendLine(plan.CanRun ? "KẾT LUẬN: đủ điều kiện sao lưu. Mới chỉ quét, chưa chép gì." : "KẾT LUẬN: chưa sao lưu được, xem lý do ở trên. Mới chỉ quét, chưa chép gì.");
        return sb.ToString();
    }
}

public static class UserDataVerifier
{
    /// <summary>Kiểm tra kết quả từ script chép: robocopy không báo lỗi (mã dưới 8) và bản chép không thiếu file/dung lượng.</summary>
    public static IReadOnlyList<string> Problems(string json, IReadOnlyCollection<UserFolder> expected)
    {
        var p = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new[] { "Không đọc được kết quả sao lưu." }; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("folders", out var arr)) return new[] { "Không đọc được kết quả sao lưu." };
            var items = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList()
                      : arr.ValueKind == JsonValueKind.Object ? new List<JsonElement> { arr } : new List<JsonElement>();
            var seen = new HashSet<string>();
            foreach (var f in items)
            {
                var name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                seen.Add(name);
                long L(string k) => f.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : -1;
                var code = L("exit"); var pf = L("preFiles"); var pb = L("preBytes"); var df = L("destFiles"); var db = L("destBytes");
                if (code < 0 || code >= 8) p.Add($"{name}: robocopy báo lỗi (mã {code}); có file không chép được (đang mở?).");
                if (pf < 0 || df < pf) p.Add($"{name}: bản sao thiếu file ({df:N0}/{pf:N0}).");
                if (pb < 0 || db < pb * 0.999) p.Add($"{name}: bản sao thiếu dung lượng ({db:N0}/{pb:N0} byte).");
            }
            foreach (var e in expected)
                if (!seen.Contains(e.ToString())) p.Add($"{e}: không có trong kết quả.");
        }
        return p;
    }
}

/// <summary>
/// GĐ3: sao lưu dữ liệu người dùng (Desktop, Documents, Downloads, Pictures, Videos, Music của tài khoản đang chạy) bằng robocopy.
/// CHỈ SAO CHÉP: không xóa, không di chuyển nguồn. Từ chối nếu nơi lưu cùng ổ vật lý với Windows. Bỏ qua thư mục có file chỉ trên OneDrive.
/// Sau khi chép: so số file/dung lượng, ghi manifest.csv (đường dẫn, kích thước, ngày, SHA256 cho file dưới 1 MB) và userdata.json.
/// </summary>
public sealed class UserDataBackupFix : IFix
{
    private readonly UserDataPlan _plan;
    private readonly string _root;
    private readonly string _dest;
    private readonly ScriptRunner _run;

    public UserDataBackupFix(UserDataPlan plan, string destRoot, DateTime now, ScriptRunner? run = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanRun) throw new ArgumentException("Chưa thể sao lưu: " + string.Join(" ", plan.Blockers), nameof(plan));
        _dest = BackupPath.Combine(destRoot, now, "userdata");
        _root = destRoot;
        _plan = plan;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 7200));
    }

    public string Destination => _dest;
    public string Title => "Sao lưu dữ liệu người dùng";
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true; // chỉ tạo thư mục mới; xóa thư mục đó là hoàn tác

    public IReadOnlyList<string> Actions => new[]
    {
        $"Tạo thư mục mới: {_dest}",
        $"Sao chép (không xóa, không di chuyển nguồn) {_plan.Copy.Count} thư mục: {string.Join(", ", _plan.Copy.Select(c => c.Folder))} — {_plan.TotalFiles:N0} file, {UsbReport.Size(_plan.TotalBytes)}",
        "Không theo junction/liên kết, không chép file chỉ nằm trên đám mây OneDrive",
        "Kiểm tra lại số file và dung lượng, ghi manifest.csv (danh sách file, kích thước, ngày, mã SHA256 file dưới 1 MB)",
        "Có thể mất từ vài phút đến hàng giờ tùy dung lượng; đừng tắt máy hay rút ổ",
        "Bản sao chứa dữ liệu riêng tư của bạn: giữ ở nơi an toàn"
    };

    public string BuildScript()
    {
        var folders = _plan.Copy.Select(c => c.Folder).ToList();
        var letter = UserDataShared.DriveLetter(_root);
        return UserDataShared.Functions + $$"""

$ErrorActionPreference = 'Stop'
{{UserDataShared.RegistryMapScript()}}
$destRoot = '{{_dest}}'
$dl = '{{letter}}'
$sysL = ($env:SystemDrive -replace ':', '')
try { $sd = [int](Get-Partition -DriveLetter $sysL -ErrorAction Stop).DiskNumber; $dd = [int](Get-Partition -DriveLetter $dl -ErrorAction Stop).DiskNumber }
catch { throw 'Không xác định được ổ vật lý của ổ Windows hoặc nơi lưu.' }
if ($sd -eq $dd) { throw 'Nơi lưu nằm cùng ổ vật lý với Windows.' }
$free = [long](Get-Volume -DriveLetter $dl).SizeRemaining
if ($free -lt {{_plan.NeedBytes}}) { throw 'Nơi lưu không đủ chỗ trống.' }
New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
$names = {{UserDataShared.NamesArray(folders)}}
$res = @()
foreach ($name in $names) {
  $src = Get-UserFolder $map[$name]
  if (-not $src -or -not (Test-Path -LiteralPath $src)) { throw "Không thấy thư mục $name." }
  $pre = Scan-Tree $src
  if ($pre.CloudFiles -gt 0) { throw "Thư mục $name có file chỉ nằm trên đám mây; dừng để tránh tải về." }
  $dst = Join-Path $destRoot $name
  & robocopy $src $dst /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NP /NFL /NDL /NJH /NJS /MT:8 /XD '$RECYCLE.BIN' 'System Volume Information' | Out-Null
  $code = [int]$LASTEXITCODE
  $post = Scan-Tree $dst
  $res += [ordered]@{ name = $name; source = $src; preFiles = [long]$pre.Files; preBytes = [long]$pre.Bytes; destFiles = [long]$post.Files; destBytes = [long]$post.Bytes; exit = $code }
}
$mpath = Join-Path $destRoot 'manifest.csv'
$sw = New-Object System.IO.StreamWriter($mpath, $false, (New-Object System.Text.UTF8Encoding($true)))
try {
  $sw.WriteLine('folder,relative_path,size,modified_utc,sha256_if_under_1MB')
  $sha = [System.Security.Cryptography.SHA256]::Create()
  foreach ($name in $names) {
    $base = Join-Path $destRoot $name
    foreach ($f in (Get-ChildItem -LiteralPath $base -Recurse -File -Force -ErrorAction SilentlyContinue)) {
      $rel = $f.FullName.Substring($base.Length).TrimStart('\')
      $h = ''
      if ($f.Length -le 1048576) { try { $h = ([BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($f.FullName)))).Replace('-', '') } catch { $h = '' } }
      $q = '"' + $rel.Replace('"', '""') + '"'
      $sw.WriteLine("$name,$q,$($f.Length),$($f.LastWriteTimeUtc.ToString('o')),$h")
    }
  }
} finally { $sw.Dispose() }
$summary = [ordered]@{ schemaVersion = 1; createdUtc = (Get-Date).ToUniversalTime().ToString('o'); userProfile = $env:USERPROFILE; folders = $res }
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destRoot 'userdata.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 4 -Compress
""";
    }

    public string BuildVerifyScript() => $$"""
$d = '{{_dest}}'
(Test-Path -LiteralPath (Join-Path $d 'manifest.csv')) -and (Test-Path -LiteralPath (Join-Path $d 'userdata.json'))
""";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang sao lưu dữ liệu (có thể lâu, đừng tắt máy)...");
        var r = await _run(BuildScript(), ct);
        if (r.ExitCode != 0)
            return FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không sao lưu được dữ liệu. Kiểm tra nơi lưu còn chỗ và app đang chạy bằng quyền Administrator." : r.Error.Trim());

        var json = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        var problems = UserDataVerifier.Problems(json, _plan.Copy.Select(c => c.Folder).ToList());
        if (problems.Count > 0)
        {
            foreach (var p in problems) Logger.Warn("Sao lưu dữ liệu: " + p);
            return FixResult.Failed("Sao lưu chưa đầy đủ: " + string.Join(" ", problems) + $" Bản sao dở dang nằm ở {_dest} (không bị xóa, bạn có thể xem hoặc xóa tay).");
        }
        return FixResult.Succeeded($"Đã sao lưu {_plan.TotalFiles:N0} file ({UsbReport.Size(_plan.TotalBytes)}) vào {_dest}. Có manifest.csv để đối chiếu. Bản sao chứa dữ liệu riêng tư, hãy giữ kín.");
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var r = await _run(BuildVerifyScript(), ct);
        return r.Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }
}
