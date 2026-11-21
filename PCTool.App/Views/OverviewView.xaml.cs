using System.Text;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Hardware;
using PCTool.Core.Optimize;
using PCTool.Core.Printer;

namespace PCTool.Views;

public partial class OverviewView : UserControl
{
    private sealed record Row(Core.Framework.Finding Finding)
    {
        public string SeverityKey => Finding.Severity.ToString();
        public string SeverityText => Finding.Severity switch
        {
            Severity.Critical => "Nghiêm trọng",
            Severity.Warning => "Cảnh báo",
            _ => "Thông tin"
        };
        public string Title => Finding.Title;
        public string Cause => Finding.Cause;
        public string FixText => Finding.Fixes.Count > 0 ? Finding.Fixes[0].Title : "Chỉ báo";
    }

    private CancellationTokenSource? _cts;
    private ScanReport? _report;

    public OverviewView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
    }

    private static IReadOnlyList<ICheck> BuildChecks() => new ICheck[]
    {
        new MemoryCheck(),
        new StartupCheck(),
        new NetworkCheck(),
        new WindowsUpdateCheck(),
        new HardwareHealthCheck(new WmiHardwareReader()),
        new CleanupCheck(),
        new PrinterCheck()
    };

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        DryRunBox.IsEnabled = !busy;
        FixButton.IsEnabled = !busy && SelectedRow()?.Finding.Fixes.Count > 0;
        CopyButton.IsEnabled = !busy && _report != null;
        Progress.IsIndeterminate = busy;
    }

    private Row? SelectedRow() => FindingsGrid.SelectedItem as Row;

    private void FindingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FixButton.IsEnabled = ScanButton.IsEnabled && SelectedRow()?.Finding.Fixes.Count > 0;

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        FindingsGrid.ItemsSource = null;
        StatusText.Text = "Đang quét...";

        try
        {
            var progress = new Progress<ScanProgress>(p => StatusText.Text = $"({p.Index + 1}/{p.Total}) {p.CheckName}...");
            var report = await ScanRunner.RunAsync(BuildChecks(), progress, _cts.Token);
            _report = report;
            FindingsGrid.ItemsSource = report.Findings.Select(f => new Row(f)).ToList();

            var text = report.Findings.Count == 0
                ? "Xong: không phát hiện vấn đề."
                : $"Xong: phát hiện {report.Findings.Count} mục cần xem.";
            if (report.Failures.Count > 0)
                text += $" Lưu ý: {report.Failures.Count} phép kiểm tra không chạy được nên kết quả chưa đầy đủ (xem nhật ký).";
            foreach (var f in report.Failures) Logger.Warn($"Kiểm tra {f.CheckId} lỗi: {f.Message}");
            StatusText.Text = text;
            Logger.Info(text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Quét lỗi, xem nhật ký.";
            Logger.Error($"Lỗi khi quét: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void FixButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row || row.Finding.Fixes.Count == 0) return;

        if (SettingsService.Default.SafeMode)
        {
            StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
            return;
        }

        var fix = row.Finding.Fixes[0];
        bool dryRun = DryRunBox.IsChecked == true;
        var ask = "Vấn đề: " + row.Title + "\n\nSẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                  (fix.IsUndoable ? "" : "\n\nLưu ý: không hoàn tác được.") +
                  (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                  "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(ask, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var executor = new FixExecutor(new RestorePointService());
            var result = await executor.RunAsync(fix,
                SettingsService.Default.BuildOptions(dryRun, createRestorePoint: fix.RequiresRestorePoint),
                new Progress<string>(m => StatusText.Text = m), _cts.Token);

            StatusText.Text = result.Outcome switch
            {
                FixOutcome.Succeeded => "Xong: " + result.Message + " Bấm Quét ngay để kiểm tra lại.",
                FixOutcome.Simulated => "Chạy thử xong, máy chưa bị thay đổi. Bỏ tick \"Chạy thử\" để làm thật.",
                FixOutcome.Blocked => "Đã khóa: " + result.Message,
                FixOutcome.StillPresent => "Đã làm nhưng vấn đề vẫn còn: " + result.Message,
                FixOutcome.Unverified => "Đã làm nhưng chưa kiểm tra lại được: " + result.Message,
                FixOutcome.Cancelled => "Đã dừng.",
                _ => "Không làm được: " + result.Message
            };
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi, xem nhật ký.";
            Logger.Error($"Lỗi khi sửa: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        var sb = new StringBuilder($"Báo cáo quét máy - {DateTime.Now:dd/MM/yyyy HH:mm}\n\n");
        foreach (var f in _report.Findings)
            sb.AppendLine($"[{new Row(f).SeverityText}] {f.Title}\n    {f.Cause}");
        if (_report.Failures.Count > 0)
            sb.AppendLine($"\n({_report.Failures.Count} phép kiểm tra không chạy được)");
        Clipboard.SetText(sb.ToString());
        StatusText.Text = "Đã sao chép báo cáo vào bộ nhớ tạm.";
    }
}
