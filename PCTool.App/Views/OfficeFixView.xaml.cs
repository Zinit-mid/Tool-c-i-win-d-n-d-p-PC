using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Office;

namespace PCTool.Views;

public partial class OfficeFixView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());

    public OfficeFixView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
    }

    private void WordSafeButton_Click(object sender, RoutedEventArgs e) => LaunchSafe(OfficeApp.Word, "Word");

    private void ExcelSafeButton_Click(object sender, RoutedEventArgs e) => LaunchSafe(OfficeApp.Excel, "Excel");

    private void LaunchSafe(OfficeApp app, string name)
    {
        var (file, args) = OfficeSafeMode.Command(app);
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            ResultText.Text = $"Đã mở {name} ở chế độ an toàn. Máy không bị thay đổi gì.";
        }
        catch (Win32Exception)
        {
            ResultText.Text = $"Không tìm thấy {name} trên máy này.";
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Không mở được {name}, xem nhật ký.";
            Logger.Error($"Mở {name} an toàn lỗi: {ex.Message}");
        }
    }

    private void RepairPageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
            ResultText.Text = "Đã mở trang Ứng dụng. Tìm Microsoft 365/Office → Sửa đổi hoặc Tùy chọn nâng cao → Sửa chữa.";
        }
        catch (Exception ex)
        {
            ResultText.Text = "Không mở được trang Ứng dụng, xem nhật ký.";
            Logger.Error($"Mở trang sửa Office lỗi: {ex.Message}");
        }
    }

    private async void TemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Default.SafeMode)
        {
            ResultText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
            return;
        }

        var fix = new ResetWordTemplateFix();
        bool dryRun = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        TemplateButton.IsEnabled = false;
        ResultText.Text = "Đang thực hiện...";
        try
        {
            var progress = new Progress<string>(m => ResultText.Text = m);
            var r = await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(dryRun), progress, CancellationToken.None);
            ResultText.Text = r.Outcome switch
            {
                FixOutcome.Succeeded => "Xong: " + r.Message,
                FixOutcome.Simulated => "Chạy thử xong, máy chưa bị thay đổi. Bỏ tick \"Chạy thử\" để làm thật.",
                FixOutcome.Blocked => "Đã khóa: " + r.Message,
                FixOutcome.StillPresent => "Chưa có hiệu lực: " + r.Message,
                FixOutcome.Unverified => "Đã thực hiện nhưng chưa kiểm tra lại được: " + r.Message,
                FixOutcome.Cancelled => "Đã dừng.",
                _ => "Không làm được: " + r.Message
            };
        }
        catch (Exception ex)
        {
            ResultText.Text = "Có lỗi, xem nhật ký.";
            Logger.Error($"Đặt lại Normal.dotm lỗi: {ex}");
        }
        finally
        {
            TemplateButton.IsEnabled = true;
        }
    }
}
