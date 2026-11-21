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

    private void BrowseIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "File ISO (*.iso)|*.iso", Title = "Chọn file ISO Windows" };
        if (dlg.ShowDialog() == true) IsoBox.Text = dlg.FileName;
    }

    private async void CheckIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var path = IsoBox.Text.Trim();
        if (!IsoInspector.IsValidIsoPath(path, out var err))
        {
            StatusText.Text = err + " (Chưa làm gì cả.)";
            return;
        }

        CheckIsoButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Đang kiểm tra ISO (gắn tạm chỉ đọc, có thể mất vài chục giây)...";

        try
        {
            var info = await new IsoInspector().InspectAsync(path, CancellationToken.None);
            var verdict = IsoInspector.Evaluate(info);
            ResultBox.Text = IsoInspector.ToText(info, verdict);
            StatusText.Text = verdict.UsableForUsb
                ? "Xong: ISO dùng được để làm USB cài. Chưa có gì bị ghi hay xóa."
                : "Xong: ISO chưa dùng được, xem lý do ở phần Kết quả.";
            CopyButton.IsEnabled = true;
            Logger.Info($"Kiểm tra ISO (chỉ đọc): {info.Images.Count} edition, dùng được: {verdict.UsableForUsb}.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Chưa kiểm tra được ISO: " + ex.Message + " (Máy không bị thay đổi gì.)";
            Logger.Error("Kiểm tra ISO lỗi: " + ex.Message);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            CheckIsoButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResultBox.Text); StatusText.Text = "Đã sao chép kết quả."; }
        catch (Exception ex) { StatusText.Text = "Không sao chép được: " + ex.Message; }
    }
}
