using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCTool.Core.Drivers;
using PCTool.Core.Framework;
using PCTool.Core.Hardware;
using PCTool.Core.License;
using PCTool.Core.Report;
using PCTool.Core.Startup;

namespace PCTool.Views;

public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();

    private void Busy(bool on) { ReportButton.IsEnabled = LicenseButton.IsEnabled = !on; Progress.IsIndeterminate = on; }

    private static async Task<T?> Try<T>(Func<Task<T>> f) where T : class
    {
        try { return await f(); } catch (Exception ex) { Logger.Warn("Báo cáo: một phần không đọc được: " + ex.Message); return null; }
    }

    private async void License_Click(object s, RoutedEventArgs e)
    {
        Busy(true);
        try
        {
            var l = await LicenseChecker.ReadAsync(null, CancellationToken.None);
            OutBox.Text = l.Count == 0 ? "Không đọc được sản phẩm nào có khóa." : string.Join(Environment.NewLine, l.Select(x => $"{x.Name}: {x.StatusText}"));
            StatusText.Text = "Chỉ xem. PCTool không kích hoạt, không đổi khóa.";
        }
        catch (Exception ex) { StatusText.Text = "Không đọc được: " + ex.Message; }
        finally { Busy(false); }
    }

    private async void Report_Click(object s, RoutedEventArgs e)
    {
        Busy(true);
        StatusText.Text = "Đang thu thập thông tin (có thể mất 1 phút)...";
        try
        {
            var hw = await Try(() => new WmiHardwareReader().ReadAsync(CancellationToken.None));
            var drv = await Try(() => DriverScanner.ScanAsync(null, CancellationToken.None));
            var lic = await Try(() => LicenseChecker.ReadAsync(null, CancellationToken.None));
            var st = await Try(() => StartupScanner.ScanAsync(null, CancellationToken.None));
            List<string>? tail = null;
            try { if (File.Exists(Logger.LogFile)) tail = File.ReadLines(Logger.LogFile).TakeLast(30).ToList(); } catch (IOException) { }
            OutBox.Text = MachineReport.Build(DateTimeOffset.Now, hw?.ToText() ?? "", drv, lic, st, tail);
            StatusText.Text = "Xong. Bấm Sao chép hoặc Lưu ra file để gửi.";
        }
        catch (Exception ex) { StatusText.Text = "Không tạo được: " + ex.Message; }
        finally { Busy(false); }
    }

    private void Copy_Click(object s, RoutedEventArgs e)
    {
        if (OutBox.Text.Length == 0) { StatusText.Text = "Chưa có nội dung."; return; }
        Clipboard.SetText(OutBox.Text);
        StatusText.Text = "Đã sao chép.";
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        if (OutBox.Text.Length == 0) { StatusText.Text = "Chưa có nội dung."; return; }
        var dlg = new SaveFileDialog { FileName = $"bao-cao-may-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "Văn bản (*.txt)|*.txt" };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, OutBox.Text); StatusText.Text = "Đã lưu: " + dlg.FileName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "Không lưu được: " + ex.Message; }
    }
}
