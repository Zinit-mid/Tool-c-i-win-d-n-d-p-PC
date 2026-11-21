using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Reinstall;

namespace PCTool.Views;

public partial class ReinstallView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());
    private UsbScanResult? _lastScan;
    private IsoInfo? _lastIso;

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
            _lastScan = result;
            UsbCombo.ItemsSource = result.Candidates.Where(c => c.Eligible)
                .Select(c => new UsbChoice(c, $"Ổ số {c.Disk.Number} - {c.Disk.Name} - {UsbReport.Size(c.Disk.Size)} - serial {c.Disk.Serial}")).ToList();
            UsbCombo.DisplayMemberPath = nameof(UsbChoice.Text);
            if (UsbCombo.Items.Count > 0) UsbCombo.SelectedIndex = 0;

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
            _lastIso = verdict.UsableForUsb ? info : null;
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

    private void DownloadPage_Click(object sender, RoutedEventArgs e)
    {
        var url = (string)((Button)sender).Tag == "11" ? IsoDownloadPages.Windows11 : IsoDownloadPages.Windows10;
        try
        {
            IsoDownloadPages.Open(url, u => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }));
            StatusText.Text = "Đã mở trang tải của Microsoft. Bạn tự tải ISO, rồi chọn file ở ô File ISO.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "Không mở được trình duyệt. Hãy tự vào: " + url;
        }
    }

    private sealed record UsbChoice(UsbCandidate Candidate, string Text);

    private async void WriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Default.SafeMode)
        {
            StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
            return;
        }
        if (_lastScan is null || UsbCombo.SelectedItem is not UsbChoice choice)
        {
            StatusText.Text = "Chưa có ổ USB để chọn. Hãy bấm Quét ổ USB trước (và cắm USB từ 32 GB).";
            return;
        }
        if (_lastIso is null || !string.Equals(_lastIso.Path, IsoBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Chưa có ISO hợp lệ: hãy nhập đường dẫn ISO rồi bấm Kiểm tra ISO (ISO phải dùng được).";
            return;
        }
        if (AckBox.IsChecked != true)
        {
            StatusText.Text = "Hãy tick ô xác nhận \"Tôi hiểu: toàn bộ dữ liệu trên ổ USB sẽ bị xóa\".";
            return;
        }

        UsbWriteFix fix;
        try
        {
            UsbWriteOptions? options = null;
            if (AnswerBox.IsChecked == true || BypassBox.IsChecked == true)
            {
                var x64 = _lastIso.Images.Where(i => i.Arch == "x64").ToList();
                var lang = x64.SelectMany(i => i.Languages).FirstOrDefault() ?? "";
                options = new UsbWriteOptions(new AnswerFileOptions(lang, null, BypassBox.IsChecked == true));
            }
            fix = new UsbWriteFix(choice.Candidate, ConfirmBox.Text, _lastIso, BackupBox.Text.Trim().Length > 0 ? BackupBox.Text.Trim() : null, null, options);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = "Chưa thể ghi: " + ex.Message + " (Chưa làm gì cả.)";
            return;
        }

        bool dryRun = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, USB không bị thay đổi.)" : "\n\nĐÂY LÀ THAO TÁC XÓA, KHÔNG KHÔI PHỤC ĐƯỢC.") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        WriteButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        CheckIsoButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Đang thực hiện... (đừng rút USB, đừng đóng app)";
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            var r = await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(dryRun), progress, CancellationToken.None);
            StatusText.Text = r.Outcome switch
            {
                FixOutcome.Succeeded => "Xong: " + r.Message + " Bước tiếp theo (hướng dẫn khởi động từ USB) sẽ có ở giai đoạn sau; hiện hãy tự thử trên máy ảo hoặc máy phụ.",
                FixOutcome.Simulated => "Chạy thử xong, USB chưa bị thay đổi gì. Bỏ tick \"Chạy thử\" để ghi thật.",
                FixOutcome.Blocked => "Đã khóa: " + r.Message,
                FixOutcome.StillPresent => "Ghi xong nhưng kiểm tra lại thấy chưa đạt: " + r.Message + " Xem nhật ký bên dưới.",
                FixOutcome.Unverified => "Đã ghi nhưng chưa kiểm tra lại được: " + r.Message,
                FixOutcome.Cancelled => "Đã dừng. USB có thể đang dở dang, hãy ghi lại.",
                _ => "Không làm được: " + r.Message
            };
            ConfirmBox.Text = "";
            AckBox.IsChecked = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi, xem nhật ký. USB có thể đang dở dang.";
            Logger.Error($"Ghi USB lỗi: {ex}");
        }
        finally
        {
            Progress.IsIndeterminate = false;
            WriteButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
            CheckIsoButton.IsEnabled = true;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResultBox.Text); StatusText.Text = "Đã sao chép kết quả."; }
        catch (Exception ex) { StatusText.Text = "Không sao chép được: " + ex.Message; }
    }
}
