using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.AppInstaller;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Restore;

namespace PCTool.Views;

public partial class RestoreView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());
    private RestorePlan? _plan;

    public RestoreView() => InitializeComponent();

    private void SetBusy(bool busy)
    {
        foreach (var b in new[] { DriverButton, WifiButton, PlanReadButton, AppsButton, DataButton, ZaloButton }) b.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        if (tag == "Plan")
        {
            var f = new Microsoft.Win32.OpenFileDialog { Title = "Chọn restore-plan.json", Filter = "restore-plan.json|*.json" };
            if (f.ShowDialog() == true) PlanBox.Text = f.FileName;
            return;
        }
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục" };
        if (d.ShowDialog() != true) return;
        (tag switch { "Driver" => DriverBox, "Wifi" => WifiBox, "Zalo" => ZaloBox, _ => DataBox }).Text = d.FolderName;
    }

    private bool Dry => DryRunBox.IsChecked == true;

    private bool Locked()
    {
        if (!SettingsService.Default.SafeMode) return false;
        StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
        return true;
    }

    private bool Confirm(IFix fix)
    {
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (Dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") + "\n\nĐồng ý thực hiện?";
        return MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async Task<FixResult> Run(IFix fix)
    {
        var progress = new Progress<string>(m => StatusText.Text = m);
        return await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(Dry), progress, CancellationToken.None);
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

    private async Task Guard(Func<Task> work, string what)
    {
        if (Locked()) return;
        SetBusy(true);
        try { await work(); }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi: " + ex.Message;
            Logger.Error($"{what} lỗi: {ex}");
        }
        finally { SetBusy(false); }
    }

    private async void DriverButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var fix = new DriverRestoreFix(DriverBox.Text.Trim());
        if (!Confirm(fix)) { StatusText.Text = "Đã hủy."; return; }
        var res = await Run(fix);
        StatusText.Text = Describe(res);
        ResultBox.Text = res.Message;
    }, "Khôi phục driver");

    private async void WifiButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var fix = new WifiRestoreFix(WifiBox.Text.Trim());
        if (!Confirm(fix)) { StatusText.Text = "Đã hủy."; return; }
        var res = await Run(fix);
        StatusText.Text = Describe(res);
        ResultBox.Text = res.Message;
    }, "Khôi phục Wi-Fi");

    private void PlanReadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _plan = RestorePlanReader.Load(PlanBox.Text.Trim());
            ResultBox.Text = $"Máy cũ: {(_plan.ComputerName.Length == 0 ? "(không rõ)" : _plan.ComputerName)}\n" +
                             $"Có {_plan.WingetIds.Count} app cài lại tự động được bằng winget:\n" + string.Join("\n", _plan.WingetIds) +
                             $"\n\nMáy cũ có tất cả {_plan.InstalledCount} app. App không nằm trong danh sách winget ở trên thì bạn cài tay (mở lại file restore-plan.json để xem tên).";
            StatusText.Text = "Đã đọc danh sách (chưa cài gì).";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException)
        {
            _plan = null;
            StatusText.Text = "Chưa đọc được: " + ex.Message;
        }
    }

    private async void AppsButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_plan == null) { StatusText.Text = "Hãy bấm Đọc danh sách trước."; return; }
        var entries = RestorePlanReader.ToEntries(_plan);
        if (entries.Count == 0) { StatusText.Text = "Không có app winget nào để cài."; return; }
        var preview = string.Join("\n• ", entries.Take(25).Select(x => x.WingetId));
        var text = $"Sẽ cài lần lượt {entries.Count} app bằng winget (im lặng):\n• {preview}" + (entries.Count > 25 ? "\n• ..." : "") +
                   (Dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng.)" : "") + "\n\nĐồng ý?";
        if (MessageBox.Show(text, "Cài các app", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { StatusText.Text = "Đã hủy."; return; }

        var runner = new ProcessRunner();
        var lines = new List<string>();
        int ok = 0, i = 0;
        foreach (var entry in entries)
        {
            i++;
            StatusText.Text = $"({i}/{entries.Count}) {entry.WingetId}...";
            var fix = new InstallAppFix(entry, runner, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), _ => { });
            var res = await Run(fix);
            if (res.IsSuccess) ok++;
            lines.Add($"{entry.WingetId}: {Describe(res)}");
            ResultBox.Text = string.Join("\n", lines);
        }
        StatusText.Text = $"Xong: {ok}/{entries.Count} app thành công. Xem chi tiết ở Kết quả.";
    }, "Khôi phục app");

    private async void ZaloButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var fix = new ZaloRestoreFix(ZaloBox.Text.Trim());
        if (!Confirm(fix)) { StatusText.Text = "Đã hủy."; return; }
        var res = await Run(fix);
        StatusText.Text = Describe(res);
        ResultBox.Text = res.Message;
    }, "Khôi phục Zalo");

    private async void DataButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var folders = new List<UserFolder>();
        if (DesktopBox.IsChecked == true) folders.Add(UserFolder.Desktop);
        if (DocumentsBox.IsChecked == true) folders.Add(UserFolder.Documents);
        if (DownloadsBox.IsChecked == true) folders.Add(UserFolder.Downloads);
        if (PicturesBox.IsChecked == true) folders.Add(UserFolder.Pictures);
        if (VideosBox.IsChecked == true) folders.Add(UserFolder.Videos);
        if (MusicBox.IsChecked == true) folders.Add(UserFolder.Music);
        if (folders.Count == 0) { StatusText.Text = "Hãy tick ít nhất một thư mục."; return; }

        var src = System.IO.Path.Combine(DataBox.Text.Trim().TrimEnd('\\'), "userdata");
        var fix = new UserDataRestoreFix(src, folders);
        if (!Confirm(fix)) { StatusText.Text = "Đã hủy."; return; }
        var res = await Run(fix);
        StatusText.Text = Describe(res);
        ResultBox.Text = res.Message;
    }, "Khôi phục dữ liệu");
}
