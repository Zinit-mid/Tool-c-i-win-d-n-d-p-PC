using System.IO;

namespace PCTool.Core.Framework;

/// <summary>
/// Các đường dẫn dữ liệu của app. Dùng %ProgramData%\PCTool (xem docs: Thiết kế dữ liệu)
/// để dữ liệu nằm một chỗ cho mọi tài khoản Windows trên máy.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCTool");

    public static string LogDir => Path.Combine(DataDir, "logs");

    public static string ReportDir => Path.Combine(DataDir, "reports");
}
