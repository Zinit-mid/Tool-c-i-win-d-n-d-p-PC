using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Update;

namespace PCTool.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Tự cập nhật từ GitHub Releases của repo (repo công khai, không cần đăng nhập).
/// Mỗi lần push lên main, GitHub Actions build và tạo 1 Release có file PCTool.exe.
/// App so phiên bản, tải bản mới, đổi tên file đang chạy thành .old (Windows cho phép đổi tên
/// file exe đang chạy), đặt bản mới vào chỗ cũ rồi mở lại.
/// </summary>
public static class UpdateService
{
    private const string Owner = "Zinit-mid";
    private const string Repo = "Tool-c-i-win-d-n-d-p-PC";
    private const string AssetName = "PCTool.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PCTool-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion => Normalize(Assembly.GetExecutingAssembly().GetName().Version);

    private static Version Normalize(Version? v) =>
        v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Trả về thông tin bản mới nếu có, null nếu đang dùng bản mới nhất (hoặc chưa có Release nào).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote)) return null;
        remote = Normalize(remote);
        if (remote <= CurrentVersion) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;

            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                sha = UpdateValidator.ParseSha256(d.GetString());

            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            return new UpdateInfo(remote, tag, notes.Trim(),
                a.GetProperty("browser_download_url").GetString() ?? "",
                a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0, sha);
        }
        return null;
    }

    /// <summary>Tải bản mới, kiểm tra, thay file và mở lại app. Gọi xong thì UI phải tắt app hiện tại.</summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được đường dẫn app.");
        if (!string.Equals(Path.GetFileName(exe), AssetName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Chỉ tự cập nhật được khi chạy file {AssetName} (đang chạy: {Path.GetFileName(exe)}).");
        if (!UpdateValidator.IsAllowedInitialUrl(info.DownloadUrl, Owner, Repo))
            throw new InvalidOperationException("Đường dẫn tải không hợp lệ (không phải từ GitHub của PCTool).");
        if (string.IsNullOrEmpty(info.Sha256))
            throw new InvalidOperationException("GitHub không cung cấp mã SHA256 cho bản này nên app từ chối tự cập nhật. Hãy tải bản mới thủ công.");

        var dir = Path.GetDirectoryName(exe)!;
        var tmp = exe + ".new";
        // Tên .old riêng mỗi lần: nếu bản cũ từ lần trước còn bị khóa thì không đụng nhau ("Access denied").
        var old = exe + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");

        Logger.Info($"Đang tải bản {info.Version}...");
        try
        {
            await DownloadAsync(info.DownloadUrl, tmp, info.Size, progress, ct);
            await VerifyAsync(tmp, info, ct);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* bỏ qua */ }
            throw;
        }

        // Đổi tên file đang chạy -> .old, đặt bản mới vào chỗ cũ. Lỗi thì trả lại như cũ.
        try { File.Move(exe, old); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"Không đổi tên được file đang chạy ({ex.Message}). Hãy đóng hẳn mọi cửa sổ PCTool (kiểm tra Task Manager), mở lại rồi cập nhật lần nữa. Thư mục: {dir}", ex);
        }
        try
        {
            File.Move(tmp, exe);
        }
        catch (Exception ex)
        {
            try { File.Move(old, exe, overwrite: true); } catch { /* giữ nguyên lỗi gốc */ }
            throw new InvalidOperationException($"Không đặt được bản mới vào {exe} ({ex.Message}). Đã trả lại bản cũ.", ex);
        }

        Logger.Info($"Đã cập nhật lên {info.Version}, đang mở lại app...");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
    }

    /// <summary>Dọn file .old/.new còn sót từ các lần cập nhật trước (file nào còn bị khóa thì để lần sau).</summary>
    public static void CleanupLeftovers()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var dir = Path.GetDirectoryName(exe);
        if (dir == null) return;
        var name = Path.GetFileName(exe);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, name + ".*"))
            {
                var ext = Path.GetExtension(f);
                if (ext.Equals(".new", StringComparison.OrdinalIgnoreCase) || ext.StartsWith(".old", StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { /* tiến trình cũ chưa thoát hẳn, lần sau dọn */ }
            }
        }
        catch { /* không liệt kê được thư mục: bỏ qua */ }
    }

    private static async Task DownloadAsync(string url, string path, long expectedSize, IProgress<double> progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (!UpdateValidator.IsAllowedFinalUri(resp.RequestMessage?.RequestUri))
            throw new InvalidOperationException("Máy chủ tải về không nằm trong danh sách cho phép, đã hủy cập nhật.");
        long total = resp.Content.Headers.ContentLength ?? expectedSize;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress.Report(done * 100.0 / total);
        }
    }

    private static Task VerifyAsync(string path, UpdateInfo info, CancellationToken ct) =>
        UpdateValidator.VerifyFileAsync(path, info.Size, info.Sha256, ct);
}
