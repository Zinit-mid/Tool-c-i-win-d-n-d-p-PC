using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Backup;
using PCTool.Core.Framework;

namespace PCTool.Views;

public partial class BackupView : UserControl
{
    private CancellationTokenSource? _cts;

    public BackupView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PCTool-SaoLuu");
    }

    private void SetBusy(bool busy)
    {
        DriverButton.IsEnabled = !busy;
        WifiButton.IsEnabled = !busy;
        BitLockerButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        DryRunBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        Progress.IsIndeterminate = busy;
        if (!busy) Progress.Value = 0;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục lưu bản sao lưu" };
        if (dlg.ShowDialog() == true) FolderBox.Text = dlg.FolderName;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void DriverButton_Click(object sender, RoutedEventArgs e) => await BackupAsync(root => new DriverBackupFix(BackupPath.Combine(root, DateTime.Now, "Drivers")));

    private async void WifiButton_Click(object sender, RoutedEventArgs e) => await BackupAsync(root => new WifiBackupFix(BackupPath.Combine(root, DateTime.Now, "WiFi")));

    private async Task BackupAsync(Func<string, IFix> make)
    {
        if (SettingsService.Default.SafeMode)
        {
            StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
            return;
        }

        var root = FolderBox.Text.Trim();
        if (!BackupPath.IsValid(root, out var err))
        {
            StatusText.Text = err;
            return;
        }

        var fix = make(root);
        bool dryRun = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang thực hiện...";
        try
        {
            var executor = new FixExecutor(new RestorePointService());
            var progress = new Progress<string>(m => StatusText.Text = m);
            var result = await executor.RunAsync(fix, SettingsService.Default.BuildOptions(dryRun), progress, _cts.Token);

            StatusText.Text = result.Outcome switch
            {
                FixOutcome.Succeeded => result.Message,
                FixOutcome.Simulated => "Chạy thử xong, máy chưa bị thay đổi. Bỏ tick \"Chạy thử\" để sao lưu thật.",
                FixOutcome.Blocked => "Đã khóa: " + result.Message,
                FixOutcome.StillPresent => "Đã chạy nhưng không thấy file trong thư mục: " + result.Message,
                FixOutcome.Unverified => "Đã chạy nhưng chưa kiểm tra lại được: " + result.Message,
                FixOutcome.Cancelled => "Đã dừng.",
                _ => "Không làm được: " + result.Message
            };
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi, xem nhật ký.";
            Logger.Error($"Lỗi sao lưu: {ex}");
        }
        finally { SetBusy(false); }
    }

    private async void BitLockerButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang đọc tình trạng BitLocker (chỉ đọc)...";
        try
        {
            var findings = await new BitLockerCheck().RunAsync(_cts.Token);
            ResultBox.Text = findings.Count == 0
                ? "Không có ổ nào đang bật BitLocker."
                : string.Join("\n\n", findings.Select(f => $"[{SeverityText(f.Severity)}] {f.Title}\n{f.Cause}"));
            StatusText.Text = "Xong.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex)
        {
            StatusText.Text = "Không đọc được BitLocker, xem nhật ký.";
            Logger.Error($"Lỗi đọc BitLocker: {ex}");
        }
        finally { SetBusy(false); }
    }

    private static string SeverityText(Severity s) => s switch
    {
        Severity.Critical => "Nghiêm trọng",
        Severity.Warning => "Cảnh báo",
        _ => "Thông tin"
    };
}
