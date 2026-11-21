using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Backup;
using PCTool.Core.Framework;

namespace PCTool.Views;

public partial class BackupRestoreView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());

    public BackupRestoreView()
    {
        InitializeComponent();
    }

    private void SetBusy(bool busy)
    {
        BrowseButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        DataButton.IsEnabled = !busy;
        AppsButton.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private List<UserFolder> Selected()
    {
        var list = new List<UserFolder>();
        if (DesktopBox.IsChecked == true) list.Add(UserFolder.Desktop);
        if (DocumentsBox.IsChecked == true) list.Add(UserFolder.Documents);
        if (DownloadsBox.IsChecked == true) list.Add(UserFolder.Downloads);
        if (PicturesBox.IsChecked == true) list.Add(UserFolder.Pictures);
        if (VideosBox.IsChecked == true) list.Add(UserFolder.Videos);
        if (MusicBox.IsChecked == true) list.Add(UserFolder.Music);
        return list;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn nơi lưu bản sao lưu (nên là ổ khác ổ Windows)" };
        if (dlg.ShowDialog() == true) DestBox.Text = dlg.FolderName;
    }

    private bool CheckDest(out string dest)
    {
        dest = DestBox.Text.Trim();
        if (BackupPath.IsValid(dest, out var err)) return true;
        StatusText.Text = err + " (Chưa làm gì cả.)";
        return false;
    }

    private async Task<(UserDataScan scan, UserDataPlan plan)?> ScanAsync(string dest)
    {
        var folders = Selected();
        if (folders.Count == 0)
        {
            StatusText.Text = "Hãy tick ít nhất một thư mục.";
            return null;
        }
        StatusText.Text = "Đang quét dung lượng (chỉ đọc, có thể mất vài phút nếu nhiều file)...";
        var scan = await new UserDataScanner().ScanAsync(folders, dest, CancellationToken.None);
        var plan = UserDataPlanner.Evaluate(scan, dest);
        ResultBox.Text = UserDataPlanner.ToText(scan, plan);
        return (scan, plan);
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckDest(out var dest)) return;
        SetBusy(true);
        try
        {
            var r = await ScanAsync(dest);
            if (r is { } v)
            {
                StatusText.Text = v.plan.CanRun ? "Xong: đủ điều kiện sao lưu. Chưa chép gì." : "Xong: chưa sao lưu được, xem lý do ở phần Kết quả.";
                Logger.Info($"Quét dữ liệu để sao lưu (chỉ đọc): {v.plan.Copy.Count} thư mục, {v.plan.TotalFiles} file, {v.plan.TotalBytes} byte, chạy được: {v.plan.CanRun}.");
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Chưa quét được: " + ex.Message + " (Máy không bị thay đổi gì.)";
            Logger.Error("Quét dữ liệu sao lưu lỗi: " + ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async void DataButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }
        if (!CheckDest(out var dest)) return;
        SetBusy(true);
        try
        {
            var r = await ScanAsync(dest);
            if (r is not { } v) return;
            if (!v.plan.CanRun)
            {
                StatusText.Text = "Chưa sao lưu được, xem lý do ở phần Kết quả. (Chưa chép gì.)";
                return;
            }

            var fix = new UserDataBackupFix(v.plan, dest, DateTime.Now);
            if (!Confirm(fix)) { StatusText.Text = "Đã hủy."; return; }
            await RunFix(fix, "Sao lưu dữ liệu");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi, xem nhật ký. (" + ex.Message + ")";
            Logger.Error($"Sao lưu dữ liệu lỗi: {ex}");
        }
        finally { SetBusy(false); }
    }

    private async void AppsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }
        if (!CheckDest(out var dest)) return;
        SetBusy(true);
        try
        {
            var fix = new AppListBackupFix(dest, DateTime.Now);
            if (!Confirm(fix)) { StatusText.Text = "Đã hủy."; return; }
            await RunFix(fix, "Lưu danh sách app");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi, xem nhật ký. (" + ex.Message + ")";
            Logger.Error($"Lưu danh sách app lỗi: {ex}");
        }
        finally { SetBusy(false); }
    }

    private bool Confirm(IFix fix)
    {
        bool dryRun = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        return MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async Task RunFix(IFix fix, string label)
    {
        bool dryRun = DryRunBox.IsChecked == true;
        StatusText.Text = "Đang thực hiện...";
        var progress = new Progress<string>(m => StatusText.Text = m);
        var res = await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(dryRun), progress, CancellationToken.None);
        StatusText.Text = res.Outcome switch
        {
            FixOutcome.Succeeded => "Xong: " + res.Message,
            FixOutcome.Simulated => "Chạy thử xong, chưa chép/ghi gì. Bỏ tick \"Chạy thử\" để làm thật.",
            FixOutcome.Blocked => "Đã khóa: " + res.Message,
            FixOutcome.StillPresent => label + ": chưa đạt khi kiểm tra lại: " + res.Message,
            FixOutcome.Unverified => label + ": đã làm nhưng chưa kiểm tra lại được: " + res.Message,
            FixOutcome.Cancelled => "Đã dừng.",
            _ => "Không làm được: " + res.Message
        };
    }
}
