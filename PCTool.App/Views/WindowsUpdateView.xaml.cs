using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCTool.Core.Framework;
using PCTool.Core.WindowsUpdate;

namespace PCTool.Views;

public partial class WindowsUpdateView : UserControl
{
    private readonly WindowsUpdateService _service = new();
    private readonly FixExecutor _executor = new(new RestorePointService());
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _reEnableAt;

    public WindowsUpdateView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        _tick.Tick += (_, _) => UpdateCountdown();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        SetBusy(true);
        try
        {
            var status = await _service.GetStatusAsync(CancellationToken.None);
            var state = _service.LoadState();
            var findings = UpdatePauseAnalyzer.Analyze(status, state, DateTimeOffset.Now, new ResumeWindowsUpdateFix(_service));

            StateText.Text = status.IsPaused switch
            {
                true => "Đang TẠM DỪNG (máy không nhận bản cập nhật)",
                false => "Đang hoạt động bình thường",
                _ => "Không đọc được trạng thái"
            };
            DetailText.Text = findings.Count == 0 ? "" : string.Join("\n", findings.Select(f => f.Title + ": " + f.Cause));

            _reEnableAt = status.IsPaused == true ? state?.ReEnableAt : null;
            _tick.IsEnabled = _reEnableAt != null;
            UpdateCountdown();

            PauseButton.IsEnabled = status.IsPaused == false;
            ResumeButton.IsEnabled = status.IsPaused == true;
        }
        catch (Exception ex)
        {
            StateText.Text = "Không đọc được trạng thái";
            ResultText.Text = "Lỗi: " + ex.Message + " (Máy không bị thay đổi gì.)";
            Logger.Error("Đọc trạng thái Windows Update lỗi: " + ex.Message);
        }
        finally
        {
            SetBusy(false, keepActionStates: true);
        }
    }

    private void UpdateCountdown()
    {
        if (_reEnableAt is not { } at) { CountdownText.Text = ""; return; }
        var left = at - DateTimeOffset.Now;
        CountdownText.Text = left > TimeSpan.Zero
            ? $"Tự bật lại sau {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}"
            : "Đã quá giờ hẹn, Windows sẽ bật lại bất cứ lúc nào. Bạn cũng có thể bấm Bật lại ngay.";
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        int minutes = int.Parse((string)((ComboBoxItem)MinutesBox.SelectedItem).Tag);
        await ConfirmAndRunAsync(new PauseWindowsUpdateFix(_service, minutes));
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e) =>
        await ConfirmAndRunAsync(new ResumeWindowsUpdateFix(_service));

    private async Task ConfirmAndRunAsync(IFix fix)
    {
        if (SettingsService.Default.SafeMode)
        {
            ResultText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
            return;
        }

        bool dryRun = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        SetBusy(true);
        ResultText.Text = "Đang thực hiện...";
        try
        {
            var progress = new Progress<string>(m => ResultText.Text = m);
            var result = await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(dryRun),
                progress, CancellationToken.None);
            ResultText.Text = Describe(result);
        }
        catch (Exception ex)
        {
            ResultText.Text = "Lỗi: " + ex.Message;
            Logger.Error("Windows Update: " + ex.Message);
        }
        await RefreshAsync();
    }

    private static string Describe(FixResult r) => r.Outcome switch
    {
        FixOutcome.Succeeded => "Xong: " + r.Message,
        FixOutcome.Simulated => "Chạy thử xong: " + r.Message,
        FixOutcome.Blocked => "Đã khóa: " + r.Message,
        FixOutcome.StillPresent => "Chưa có hiệu lực: " + r.Message,
        FixOutcome.Unverified => "Đã thực hiện nhưng chưa kiểm tra lại được: " + r.Message,
        FixOutcome.Cancelled => "Đã dừng: " + r.Message,
        _ => "Không làm được: " + r.Message
    };

    private void SetBusy(bool busy, bool keepActionStates = false)
    {
        RefreshButton.IsEnabled = !busy;
        if (busy) { PauseButton.IsEnabled = false; ResumeButton.IsEnabled = false; }
        else if (!keepActionStates) { PauseButton.IsEnabled = true; ResumeButton.IsEnabled = true; }
    }
}
