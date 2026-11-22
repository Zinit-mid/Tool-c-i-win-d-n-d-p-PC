namespace PCTool.Core.AppInstaller;

public sealed record AppProfile(string Name, IReadOnlyList<string> Ids);

/// <summary>Bộ app chọn sẵn. Chỉ là danh sách mã mục trong danh sách cài nhanh; cài đặt vẫn do bạn bấm.</summary>
public static class AppProfiles
{
    public static IReadOnlyList<AppProfile> All { get; } = new AppProfile[]
    {
        new("Văn phòng", new[] { "chrome", "zalo", "unikey", "7zip", "foxit", "google-drive" }),
        new("Gia đình", new[] { "chrome", "zalo", "unikey", "vlc", "7zip", "sumatrapdf" }),
        new("Chơi game / giải trí", new[] { "chrome", "discord", "telegram", "vlc", "7zip" }),
        new("Hỗ trợ từ xa", new[] { "chrome", "ultraviewer", "teamviewer", "anydesk", "7zip" }),
    };
}
