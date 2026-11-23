namespace PCTool.Core.Tools;

/// <summary>
/// Trang web các công cụ cắt/ghép video (dùng FFmpeg). PCTool chỉ MỞ trang bằng trình duyệt, không tải hay chạy file nào:
/// bạn tự xem và tự tải. Chỉ nhận đúng các địa chỉ dưới đây.
/// </summary>
public static class VideoToolPages
{
    public sealed record Page(string Title, string Url);

    public static IReadOnlyList<Page> All { get; } = new[]
    {
        new Page("LosslessCut (trang chính thức)", "https://github.com/mifi/lossless-cut/releases"),
        new Page("Videomass (trang chính thức)", "https://jeanslack.github.io/Videomass/"),
        new Page("FFmpeg GUI Editor (CHƯA kiểm chứng)", "https://github.com/DevedevT/FFmpeg-GUI-Editor-Windows")
    };

    public static bool IsAllowed(string? url) => url is not null && All.Any(p => p.Url == url);

    public static void Open(string url, Action<string> opener)
    {
        if (!IsAllowed(url)) throw new ArgumentException("Chỉ mở các trang đã định sẵn.", nameof(url));
        opener(url);
    }
}
