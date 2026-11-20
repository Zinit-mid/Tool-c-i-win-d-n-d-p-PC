using System.IO;

namespace PCTool.Core.Framework;

/// <summary>Nhật ký thao tác: hiển thị trong app và ghi ra file để xem lại.</summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static string LogDir => AppPaths.LogDir;

    public static string LogFile => Path.Combine(LogDir, $"pctool-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>UI đăng ký để nhận dòng log mới.</summary>
    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Không để lỗi ghi log làm hỏng app.
        }
        LineWritten?.Invoke(line);
    }
}
