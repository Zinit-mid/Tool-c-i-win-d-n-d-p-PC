using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Tools;

namespace PCTool.Views;

public partial class ToolsView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());

    public ToolsView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
    }

    private bool Dry => DryRunBox.IsChecked == true;

    private void SetBusy(bool busy)
    {
        foreach (var b in new[] { DeviceButton, ShowHiddenButton, HideHiddenButton, CopyButton }) b.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private bool Locked()
    {
        if (!SettingsService.Default.SafeMode) return false;
        StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
        return true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục" };
        if (d.ShowDialog() != true) return;
        ((string)((Button)sender).Tag == "Src" ? SrcBox : DstBox).Text = d.FolderName;
    }

    private static string Describe(FixResult res) => res.Outcome switch
    {
        FixOutcome.Succeeded => "Xong: " + res.Message,
        FixOutcome.Simulated => "Chạy thử xong, chưa thay đổi gì. Bỏ tick \"Chạy thử\" để làm thật.",
        FixOutcome.Blocked => "Đã khóa: " + res.Message,
        FixOutcome.StillPresent => "Chưa đạt khi kiểm tra lại: " + res.Message,
        FixOutcome.Unverified => "Đã làm nhưng chưa kiểm tra lại được: " + res.Message,
        FixOutcome.Cancelled => "Đã dừng.",
        _ => "Không làm được: " + res.Message
    };

    private async Task RunFix(IFix fix)
    {
        if (Locked()) return;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (Dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng.)" : "") + "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { StatusText.Text = "Đã hủy."; return; }
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            var res = await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(Dry), progress, CancellationToken.None);
            StatusText.Text = Describe(res);
            ResultBox.Text = res.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi: " + ex.Message;
            Logger.Error($"{fix.Title} lỗi: {ex}");
        }
        finally { SetBusy(false); }
    }

    private async void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "Đang đọc thiết bị...";
        try
        {
            ResultBox.Text = (await DeviceProbe.ReadAsync(null, CancellationToken.None)).ToText();
            StatusText.Text = "Xong (chỉ đọc, chưa đổi gì).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            StatusText.Text = "Chưa đọc được: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async void ShowHiddenButton_Click(object sender, RoutedEventArgs e) => await RunFix(new ShowHiddenFilesFix(true));
    private async void HideHiddenButton_Click(object sender, RoutedEventArgs e) => await RunFix(new ShowHiddenFilesFix(false));

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        BulkCopyFix fix;
        try { fix = new BulkCopyFix(SrcBox.Text.Trim(), DstBox.Text.Trim()); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        await RunFix(fix);
    }
}
