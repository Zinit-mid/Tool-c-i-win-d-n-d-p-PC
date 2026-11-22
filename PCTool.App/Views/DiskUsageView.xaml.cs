using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Disk;
using PCTool.Core.Hardware;

namespace PCTool.Views;

public partial class DiskUsageView : UserControl
{
    private CancellationTokenSource? _cts;

    private sealed class Row
    {
        public Row(FolderSize f, long total) { Item = f; Total = total; }
        public FolderSize Item { get; }
        private long Total { get; }
        public string Name => Item.Name;
        public string SizeText => HardwareReport.FormatBytes(Item.Bytes);
        public string Percent => Total <= 0 ? "" : (Item.Bytes * 100.0 / Total).ToString("0.#") + "%";
    }

    public DiskUsageView() => InitializeComponent();

    private void Browse_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục cần xem dung lượng" };
        if (d.ShowDialog() == true) PathBox.Text = d.FolderName;
    }

    private async void Scan_Click(object s, RoutedEventArgs e)
    {
        var root = PathBox.Text.Trim();
        if (DiskUsage.CheckRoot(root) is { } err) { StatusText.Text = err; return; }
        _cts = new CancellationTokenSource();
        ScanButton.IsEnabled = false; StopButton.IsEnabled = true; Progress.IsIndeterminate = true;
        StatusText.Text = "Đang quét...";
        try
        {
            var ct = _cts.Token;
            var res = await Task.Run(() => DiskUsage.Scan(root, ct));
            long total = res.Sum(x => x.Bytes);
            Grid.ItemsSource = res.Select(x => new Row(x, total)).ToList();
            StatusText.Text = $"Xong. Tổng các mục hiển thị: {HardwareReport.FormatBytes(total)}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Không quét được: " + ex.Message; }
        finally { ScanButton.IsEnabled = true; StopButton.IsEnabled = false; Progress.IsIndeterminate = false; }
    }

    private void Stop_Click(object s, RoutedEventArgs e) => _cts?.Cancel();

    private void Open_Click(object s, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is Row r && DiskUsage.CheckRoot(r.Item.Path) == null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{r.Item.Path}\"") { UseShellExecute = true });
        else StatusText.Text = "Chọn một dòng trong danh sách trước.";
    }
}
