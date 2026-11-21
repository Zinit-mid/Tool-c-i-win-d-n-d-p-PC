using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Reinstall;

namespace PCTool.Views;

public partial class ReinstallView : UserControl
{
    public ReinstallView()
    {
        InitializeComponent();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Đang đọc danh sách ổ đĩa (chỉ đọc)...";

        try
        {
            var ctx = new UsbScanContext(IsoBox.Text, BackupBox.Text);
            var result = await new UsbDriveScanner().ScanAsync(ctx, CancellationToken.None);
            ResultBox.Text = UsbReport.ToText(result);

            var ok = result.Candidates.Count(c => c.Eligible);
            StatusText.Text = result.Candidates.Count == 0
                ? "Không thấy ổ USB nào. Cắm USB rồi bấm Quét lại."
                : $"Xong: thấy {result.Candidates.Count} ổ USB, {ok} ổ đủ điều kiện. Chưa có gì bị ghi hay xóa.";
            CopyButton.IsEnabled = true;
            Logger.Info($"Quét ổ USB (chỉ đọc): {result.Candidates.Count} ổ USB, {ok} đủ điều kiện, {result.HiddenNonUsbCount} ổ không phải USB bị ẩn.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Chưa đọc được danh sách ổ: " + ex.Message + " (Máy không bị thay đổi gì. Bấm Quét ổ USB để thử lại.)";
            Logger.Error("Quét ổ USB lỗi: " + ex.Message);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            ScanButton.IsEnabled = true;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResultBox.Text); StatusText.Text = "Đã sao chép kết quả."; }
        catch (Exception ex) { StatusText.Text = "Không sao chép được: " + ex.Message; }
    }
}
