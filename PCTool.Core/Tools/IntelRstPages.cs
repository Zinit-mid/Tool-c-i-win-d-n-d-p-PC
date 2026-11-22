namespace PCTool.Core.Tools;

/// <summary>
/// Trang tải Intel RST (Rapid Storage Technology / VMD) chính thức. PCTool chỉ MỞ trang bằng trình duyệt:
/// Intel bắt bấm đồng ý giấy phép nên không tải tự động được. Chỉ nhận đúng các địa chỉ dưới đây.
/// </summary>
public static class IntelRstPages
{
    public sealed record Page(string Title, string Url);

    public static IReadOnlyList<Page> All { get; } = new[]
    {
        new Page("Intel RST - Core thế hệ 12 đến 15", "https://www.intel.com/content/www/us/en/download/930427"),
        new Page("Intel RST - Core thế hệ 10 và 11", "https://www.intel.com/content/www/us/en/download/19512"),
        new Page("Intel RST - Core Ultra Series 3", "https://www.intel.com/content/www/us/en/download/920456")
    };

    public static bool IsAllowed(string? url) => url is not null && All.Any(p => p.Url == url);

    public static void Open(string url, Action<string> opener)
    {
        if (!IsAllowed(url)) throw new ArgumentException("Chỉ mở trang tải chính thức của Intel đã định sẵn.", nameof(url));
        opener(url);
    }
}
