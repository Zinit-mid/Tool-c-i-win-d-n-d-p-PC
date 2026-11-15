using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

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
            {
                var s = d.GetString() ?? "";
                if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = s[7..];
            }

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
        if (!info.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Đường dẫn tải không hợp lệ.");

        var dir = Path.GetDirectoryName(exe)!;
        var tmp = exe + ".new";
        var old = exe + ".old";

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
        try { if (File.Exists(old)) File.Delete(old); } catch { /* .old cũ còn bị khóa: sẽ ghi đè bằng Move bên dưới */ }
        File.Move(exe, old, overwrite: true);
        try
        {
            File.Move(tmp, exe);
        }
        catch
        {
            File.Move(old, exe, overwrite: true);
            throw;
        }

        Logger.Info($"Đã cập nhật lên {info.Version}, đang mở lại app...");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
    }

    /// <summary>Dọn file .old/.new còn sót từ lần cập nhật trước.</summary>
    public static void CleanupLeftovers()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        foreach (var f in new[] { exe + ".old", exe + ".new" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* tiến trình cũ chưa thoát hẳn, lần sau dọn */ }
        }
    }

    private static async Task DownloadAsync(string url, string path, long expectedSize, IProgress<double> progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
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

    private static async Task VerifyAsync(string path, UpdateInfo info, CancellationToken ct)
    {
        var len = new FileInfo(path).Length;
        if (len < 1_000_000) throw new InvalidDataException("File tải về quá nhỏ, có thể bị lỗi.");
        if (info.Size > 0 && len != info.Size) throw new InvalidDataException("File tải về không đủ dung lượng.");

        await using var fs = File.OpenRead(path);
        var head = new byte[2];
        if (await fs.ReadAsync(head, ct) != 2 || head[0] != 'M' || head[1] != 'Z')
            throw new InvalidDataException("File tải về không phải file chạy của Windows.");

        if (!string.IsNullOrEmpty(info.Sha256))
        {
            fs.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mã kiểm tra (SHA256) không khớp, đã hủy cập nhật.");
        }
    }
}
