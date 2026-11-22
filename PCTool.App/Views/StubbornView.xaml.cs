using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Stubborn;

namespace PCTool.Views;

public partial class StubbornView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());

    public StubbornView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        PathBox.TextChanged += (_, _) =>
            MoveHint.Text = StubbornGuard.Check(PathBox.Text.Trim(), out _)
                ? "Mã xác nhận: " + StubbornGuard.MoveCode(PathBox.Text.Trim())
                : "Mã xác nhận: CHUYEN + khoảng trắng + tên file/thư mục (sẽ hiện khi đường dẫn hợp lệ).";
    }

    private bool Dry => DryRunBox.IsChecked == true;

    private void SetBusy(bool busy)
    {
        foreach (var b in new[] { GrantButton, MoveButton, TrashButton }) b.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục" };
        if (d.ShowDialog() == true) PathBox.Text = d.FolderName;
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "Chọn file" };
        if (d.ShowDialog() == true) PathBox.Text = d.FileName;
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

    private async Task RunFix(Func<IFix> make)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }
        IFix fix;
        try { fix = make(); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) + (Dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng.)" : "") + "\n\nĐồng ý?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { StatusText.Text = "Đã hủy."; return; }
        SetBusy(true);
        try
        {
            var res = await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(Dry), new Progress<string>(m => StatusText.Text = m), CancellationToken.None);
            StatusText.Text = Describe(res);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi: " + ex.Message;
            Logger.Error($"{fix.Title} lỗi: {ex}");
        }
        finally { SetBusy(false); }
    }

    private async void GrantButton_Click(object sender, RoutedEventArgs e) =>
        await RunFix(() => new GrantFullAccessFix(PathBox.Text.Trim(), GrantCodeBox.Text.Trim()));

    private async void MoveButton_Click(object sender, RoutedEventArgs e) =>
        await RunFix(() => new StubbornMoveToTrashFix(PathBox.Text.Trim(), MoveCodeBox.Text.Trim()));

    private async void TrashButton_Click(object sender, RoutedEventArgs e) =>
        await RunFix(() => new EmptyTrashFix(TrashCodeBox.Text.Trim()));
}
