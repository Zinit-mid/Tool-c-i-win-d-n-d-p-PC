using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCTool.Services;

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

    private async void RestartSpoolerButton_Click(object sender, RoutedEventArgs e)
    {
        var ask = MessageBox.Show(
            "Khởi động lại dịch vụ Print Spooler?\nCác lệnh in đang chạy có thể bị gián đoạn.",
            "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang khởi động lại Print Spooler...";
        try
        {
            await PrinterFixes.RestartSpoolerAsync(_cts.Token);
            StatusText.Text = "Đã khởi động lại Print Spooler. Bấm Chẩn đoán để kiểm tra lại.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không khởi động lại được Spooler, xem nhật ký.";
            Logger.Error($"Lỗi khởi động lại Spooler: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        var ask = MessageBox.Show(
            "Xóa TOÀN BỘ lệnh in đang chờ của mọi máy in?\n" +
            "Spooler sẽ được tắt rồi bật lại. Các tài liệu đang chờ in sẽ mất, phải in lại.",
            "Xác nhận xóa hàng đợi in", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang xóa hàng đợi in...";
        try
        {
            int n = await PrinterFixes.ClearQueueAsync(_cts.Token);
            StatusText.Text = $"Đã xóa hàng đợi in ({n} file). Bấm Chẩn đoán để kiểm tra lại.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không xóa được hàng đợi in, xem nhật ký.";
            Logger.Error($"Lỗi xóa hàng đợi in: {ex}");
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
