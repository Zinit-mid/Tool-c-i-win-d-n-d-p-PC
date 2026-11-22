using System.Windows;
using PCTool.Core.Framework;

namespace PCTool.Views;

/// <summary>Cách chạy một cách sửa qua khung chung (hỏi xác nhận, Chế độ an toàn, chạy thử) dùng cho các màn hình tiện ích mới.</summary>
internal static class FixUi
{
    public static string Describe(FixResult res) => res.Outcome switch
    {
        FixOutcome.Succeeded => "Xong: " + res.Message,
        FixOutcome.Simulated => "Chạy thử xong, chưa thay đổi gì. Bỏ tick \"Chạy thử\" để làm thật.",
        FixOutcome.Blocked => "Đã khóa: " + res.Message,
        FixOutcome.StillPresent => "Chưa đạt khi kiểm tra lại: " + res.Message,
        FixOutcome.Unverified => "Đã làm nhưng chưa kiểm tra lại được: " + res.Message,
        FixOutcome.Cancelled => "Đã dừng.",
        _ => "Không làm được: " + res.Message
    };

    public static async Task<string> RunAsync(FixExecutor executor, IFix fix, bool dry, Action<string> status)
    {
        if (SettingsService.Default.SafeMode) return "Đã khóa: " + SettingsService.SafeModeMessage;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng.)" : "") + "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return "Đã hủy.";
        try
        {
            var res = await executor.RunAsync(fix, SettingsService.Default.BuildOptions(dry), new Progress<string>(status), CancellationToken.None);
            return Describe(res);
        }
        catch (Exception ex)
        {
            Logger.Error($"{fix.Title} lỗi: {ex}");
            return "Có lỗi: " + ex.Message;
        }
    }
}
