using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Printer;

namespace PCTool.Views;

public partial class PrinterView : UserControl
{
    private CancellationTokenSource? _cts;
    private PrinterReport? _report;

    public PrinterView()
    {
        InitializeComponent();
    }

    private void SetBusy(bool busy)
    {
        DiagnoseButton.IsEnabled = !busy;
        RestartSpoolerButton.IsEnabled = !busy;
        DryRunBox.IsEnabled = !busy;
        ClearQueueButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        CopyButton.IsEnabled = !busy && _report != null;
        Progress.IsIndeterminate = busy;
        if (!busy) Progress.Value = 0;
    }

    private async void DiagnoseButton_Click(object sender, RoutedEventArgs e) => await DiagnoseAsync();

    private async Task DiagnoseAsync()
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang chẩn đoán máy in...";

        try
        {
            var report = await PrinterDiagnostics.RunAsync(_cts.Token);
            _report = report;

            PrintersGrid.ItemsSource = report.Printers;
            FindingsGrid.ItemsSource = report.Findings;

            int errors = report.Findings.Count(f => f.Level == "Error");
            int warns = report.Findings.Count(f => f.Level == "Warn");
            StatusText.Text = errors + warns == 0
                ? "Xong: không phát hiện vấn đề."
                : $"Xong: {errors} lỗi, {warns} cảnh báo.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng.";
            Logger.Warn("Đã dừng chẩn đoán máy in.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Chẩn đoán lỗi, xem nhật ký.";
            Logger.Error($"Lỗi chẩn đoán máy in: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void RestartSpoolerButton_Click(object sender, RoutedEventArgs e) =>
        await ConfirmAndRunAsync(new RestartSpoolerFix());

    private async void ClearQueueButton_Click(object sender, RoutedEventArgs e) =>
        await ConfirmAndRunAsync(new ClearPrintQueueFix());

    /// <summary>Mọi cách sửa đi qua khung chung: hỏi xác nhận → chạy thử/chạy thật → kiểm tra lại → nhật ký.</summary>
    private async Task ConfirmAndRunAsync(IFix fix)
    {
        bool dryRun = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang thực hiện...";
        try
        {
            var executor = new FixExecutor(new RestorePointService());
            var progress = new Progress<string>(m => StatusText.Text = m);
            var result = await executor.RunAsync(fix, new FixOptions { DryRun = dryRun, CreateRestorePoint = false },
                progress, _cts.Token);

            StatusText.Text = result.Outcome switch
            {
                FixOutcome.Succeeded => result.Message + " Bấm Chẩn đoán để kiểm tra lại.",
                FixOutcome.Simulated => "Chạy thử xong, máy chưa bị thay đổi. Bỏ tick \"Chạy thử\" để làm thật.",
                FixOutcome.Blocked => "Đã khóa: " + result.Message,
                FixOutcome.StillPresent => "Đã làm nhưng chưa có hiệu lực: " + result.Message,
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
            Logger.Error($"Lỗi sửa máy in: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:printers") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Không mở được cài đặt máy in: {ex.Message}");
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        try
        {
            Clipboard.SetText(_report.ToText());
            StatusText.Text = "Đã sao chép báo cáo vào clipboard.";
        }
        catch (Exception ex)
        {
            Logger.Error($"Không sao chép được báo cáo: {ex.Message}");
        }
    }
}
