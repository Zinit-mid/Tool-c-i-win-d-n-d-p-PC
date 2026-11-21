using System.Security.Cryptography;

namespace PCTool.Core.Update;

/// <summary>
/// Kiểm tra an toàn cho tự cập nhật (thuần logic, có test): chỉ nhận link tải từ GitHub của repo này
/// và bắt buộc có mã SHA256 khớp. Thiếu mã hoặc sai mã thì từ chối.
/// </summary>
public static class UpdateValidator
{
    /// <summary>Link ban đầu (browser_download_url): https, github.com, đúng repo, đúng đường dẫn release.</summary>
    public static bool IsAllowedInitialUrl(string? url, string owner, string repo)
    {
        if (!TryHttps(url, out var uri)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = $"/{owner}/{repo}/releases/download/";
        return uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Địa chỉ sau khi GitHub chuyển hướng: chỉ github.com hoặc *.githubusercontent.com qua https.</summary>
    public static bool IsAllowedFinalUri(Uri? uri)
    {
        if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var h = uri.Host.ToLowerInvariant();
        return h == "github.com" || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    /// <summary>Đọc "sha256:&lt;64 hex&gt;" của GitHub. Trả null nếu thiếu hoặc sai định dạng.</summary>
    public static string? ParseSha256(string? digest)
    {
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var h = digest[7..];
        if (h.Length != 64) return null;
        foreach (var c in h) if (!Uri.IsHexDigit(c)) return null;
        return h.ToLowerInvariant();
    }

    /// <summary>Kiểm tra file đã tải. sha256 là bắt buộc.</summary>
    public static async Task VerifyFileAsync(string path, long expectedSize, string? sha256, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sha256))
            throw new InvalidDataException("GitHub không cung cấp mã SHA256 cho bản này nên app từ chối tự cập nhật. Hãy tải bản mới thủ công.");

        var len = new FileInfo(path).Length;
        if (len < 1_000_000) throw new InvalidDataException("File tải về quá nhỏ, có thể bị lỗi.");
        if (expectedSize > 0 && len != expectedSize) throw new InvalidDataException("File tải về không đủ dung lượng.");

        await using var fs = File.OpenRead(path);
        var head = new byte[2];
        if (await fs.ReadAsync(head, ct) != 2 || head[0] != 'M' || head[1] != 'Z')
            throw new InvalidDataException("File tải về không phải file chạy của Windows.");

        fs.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mã kiểm tra (SHA256) không khớp, đã hủy cập nhật.");
    }

    private static bool TryHttps(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
        uri = u;
        return true;
    }
}
