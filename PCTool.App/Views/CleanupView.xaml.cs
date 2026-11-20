using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Optimize;

namespace PCTool.Views;

public partial class CleanupView : UserControl
{
    private List<CleanupItem> _items = CleanupScanner.BuildItems();
    private CancellationTokenSource? _cts;

    public CleanupView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _items.Any(i => i.SizeBytes > 0 || i.IsRecycleBin);
        CancelButton.IsEnabled = busy;
        DryRunBox.IsEnabled = !busy;
        if (!busy) Progress.Value = 0;
    }

    /// <summary>
    /// Làm mới DataGrid. Nếu người dùng đang tick/sửa dở một ô (lúc quét lâu họ vẫn bấm được),
    /// Items.Refresh() sẽ ném InvalidOperationException ("Refresh is not allowed during an
    /// AddNew or EditItem transaction"), nên phải commit (hoặc hủy) giao dịch sửa trước.
    /// </summary>
    private void RefreshGrid()
    {
        if (!ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            ItemsGrid.CancelEdit(DataGridEditingUnit.Row);

        if (ItemsGrid.Items is System.ComponentModel.IEditableCollectionView ecv)
        {
            if (ecv.IsAddingNew) ecv.CancelNew();
            if (ecv.IsEditingItem) ecv.CancelEdit();
        }

        ItemsGrid.Items.Refresh();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        StatusText.Text = "Đang quét...";
        Logger.Info("Bắt đầu quét rác.");

        try
        {
            var items = _items.ToList();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                StatusText.Text = $"Đang quét: {item.Name}";
                await Task.Run(() => CleanupScanner.Scan(item, ct), ct);
                Progress.Value = (i + 1) * 100.0 / items.Count;
            }

            RefreshGrid();
            long total = _items.Sum(i => i.SizeBytes);
            StatusText.Text = $"Quét xong. Tổng có thể dọn: {CleanupItem.FormatSize(total)}";
            Logger.Info($"Quét xong, tổng {CleanupItem.FormatSize(total)}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng.";
            Logger.Warn("Đã dừng quét.");
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

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _items.Where(i => i.Selected).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show("Chưa chọn mục nào để dọn.", "PCTool");
            return;
        }

        bool dry = DryRunBox.IsChecked == true;
        long expect = chosen.Sum(i => i.SizeBytes);

        if (!dry)
        {
            var names = string.Join("\n - ", chosen.Select(i => i.Name));
            var ask = MessageBox.Show(
                $"Sẽ xóa các mục sau (khoảng {CleanupItem.FormatSize(expect)}):\n - {names}\n\nTiếp tục?",
                "Xác nhận dọn dẹp", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        Logger.Info(dry ? "Bắt đầu dọn (CHẠY THỬ)." : "Bắt đầu dọn thật.");

        var total = new CleanupResult();
        try
        {
            for (int i = 0; i < chosen.Count; i++)
            {
                var item = chosen[i];
                StatusText.Text = $"Đang dọn: {item.Name}";
                var r = await Task.Run(() => CleanupScanner.Clean(item, dry, ct), ct);
                total.FreedBytes += r.FreedBytes;
                total.DeletedFiles += r.DeletedFiles;
                total.SkippedFiles += r.SkippedFiles;
                Progress.Value = (i + 1) * 100.0 / chosen.Count;
            }

            StatusText.Text = $"{(dry ? "[Chạy thử] " : "")}Xong: {total.DeletedFiles} file, " +
                              $"{CleanupItem.FormatSize(total.FreedBytes)}, bỏ qua {total.SkippedFiles} file đang dùng.";
            Logger.Info(StatusText.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng.";
            Logger.Warn("Đã dừng dọn.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
