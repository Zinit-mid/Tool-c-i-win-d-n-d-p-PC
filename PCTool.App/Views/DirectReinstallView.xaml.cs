using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Backup;
using PCTool.Core.Framework;
using PCTool.Core.Reinstall;

namespace PCTool.Views;

public partial class DirectReinstallView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());
    private readonly DirectFactsScanner _scanner = new();
    private IsoInfo? _iso;
    private DirectPlan? _plan;
    private string _planIso = "";
    private string _planBackup = "";

    public DirectReinstallView() => InitializeComponent();

    private DirectMode Mode => C1Radio.IsChecked == true ? DirectMode.RepairKeepAll
        : C2Radio.IsChecked == true ? DirectMode.KeepFilesOnly
        : C3Radio.IsChecked == true ? DirectMode.CleanAll : DirectMode.ResetPc;

    private void SetBusy(bool busy)
    {
        foreach (var b in new[] { IsoButton, KitButton, CheckButton, LaunchButton }) b.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private bool Locked()
    {
        if (!SettingsService.Default.SafeMode) return false;
        StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
        return true;
    }

    private void IsoBrowse_Click(object sender, RoutedEventArgs e)
    {
        var f = new Microsoft.Win32.OpenFileDialog { Title = "Chọn file ISO Windows (tải từ Microsoft)", Filter = "ISO|*.iso" };
        if (f.ShowDialog() == true) IsoBox.Text = f.FileName;
    }

    private void BackupBrowse_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục sao lưu theo ngày giờ" };
        if (d.ShowDialog() == true) BackupBox.Text = d.FolderName;
    }

    private async void IsoButton_Click(object sender, RoutedEventArgs e)
    {
        var path = IsoBox.Text.Trim();
        if (!IsoInspector.IsValidIsoPath(path, out var err)) { StatusText.Text = err + " (Chưa làm gì cả.)"; return; }
        SetBusy(true);
        try
        {
            StatusText.Text = "Đang kiểm tra ISO (chỉ đọc)...";
            var info = await new IsoInspector().InspectAsync(path, CancellationToken.None);
            _iso = info;
            ResultBox.Text = IsoInspector.ToText(info, IsoInspector.Evaluate(info));
            StatusText.Text = "Đã đọc ISO. Bấm Kiểm tra điều kiện để so với máy này.";
        }
        catch (Exception ex)
        {
            _iso = null;
            StatusText.Text = "Chưa kiểm tra được ISO: " + ex.Message;
            Logger.Error("Kiểm tra ISO (cài trực tiếp) lỗi: " + ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async void KitButton_Click(object sender, RoutedEventArgs e)
    {
        if (Locked()) return;
        SetBusy(true);
        try
        {
            var exe = Environment.ProcessPath ?? "";
            var fix = new RestoreKitFix(BackupBox.Text.Trim(), exe);
            if (!Confirm(fix)) { StatusText.Text = "Đã hủy."; return; }
            var res = await Run(fix);
            StatusText.Text = Describe(res);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            StatusText.Text = ex.Message + " (Chưa làm gì cả.)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi, xem nhật ký. (" + ex.Message + ")";
            Logger.Error("Chép bộ khôi phục lỗi: " + ex);
        }
        finally { SetBusy(false); }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        var mode = Mode;
        SetBusy(true);
        try
        {
            StatusText.Text = "Đang đọc thông tin máy (chỉ đọc)...";
            var sys = await _scanner.ScanSystemAsync(CancellationToken.None);
            BackupFacts? backup = null;
            var bpath = BackupBox.Text.Trim();
            if (bpath.Length > 0)
            {
                if (!BackupPath.IsValid(bpath, out var err)) { StatusText.Text = err + " (Chưa làm gì cả.)"; return; }
                backup = await _scanner.ScanBackupAsync(bpath, CancellationToken.None);
            }
            _plan = DirectPlanner.Evaluate(mode, sys, DirectModes.NeedsIso(mode) ? _iso : null, backup);
            _planIso = IsoBox.Text.Trim();
            _planBackup = bpath;
            ResultBox.Text = DirectPlanner.ToText(_plan);
            StatusText.Text = _plan.CanRun ? "Đủ điều kiện (chưa mở gì)." : "Chưa đủ điều kiện, xem Kết quả. (Chưa thay đổi gì.)";
            Logger.Info($"Kiểm tra cài lại trực tiếp ({mode}): chạy được = {_plan.CanRun}, {_plan.Blockers.Count} chặn, {_plan.Warnings.Count} cảnh báo.");
        }
        catch (Exception ex)
        {
            _plan = null;
            StatusText.Text = "Chưa kiểm tra được: " + ex.Message + " (Máy không bị thay đổi gì.)";
            Logger.Error("Kiểm tra cài lại trực tiếp lỗi: " + ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (Locked()) return;
        if (_plan is null || _plan.Mode != Mode)
        { StatusText.Text = "Hãy bấm Kiểm tra điều kiện cho đúng kiểu đã chọn trước. (Chưa làm gì cả.)"; return; }
        if (!string.Equals(_planIso, IsoBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_planBackup, BackupBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        { StatusText.Text = "ISO hoặc thư mục sao lưu đã đổi sau lần kiểm tra. Hãy kiểm tra lại. (Chưa làm gì cả.)"; return; }

        SetBusy(true);
        try
        {
            var acks = new DirectAcks(BackupAck.IsChecked == true, BitLockerAck.IsChecked == true, PhraseBox.Text.Trim());
            var fix = new LaunchReinstallFix(_plan, acks, DirectModes.NeedsIso(_plan.Mode) ? _planIso : null);
            var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                       "\n\nSau đó, ở màn hình của Microsoft:\n- " + string.Join("\n- ", DirectModes.Guide(_plan.Mode)) +
                       (DryRunBox.IsChecked == true ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, không mở gì.)" : "") +
                       "\n\nĐồng ý mở công cụ?";
            if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { StatusText.Text = "Đã hủy."; return; }
            var res = await Run(fix);
            StatusText.Text = Describe(res);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message + " (Chưa làm gì cả.)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi, xem nhật ký. (" + ex.Message + ")";
            Logger.Error("Mở công cụ cài lại lỗi: " + ex);
        }
        finally { SetBusy(false); }
    }

    private bool Confirm(IFix fix)
    {
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (DryRunBox.IsChecked == true ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") + "\n\nĐồng ý?";
        return MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private Task<FixResult> Run(IFix fix) =>
        _executor.RunAsync(fix, SettingsService.Default.BuildOptions(DryRunBox.IsChecked == true), new Progress<string>(m => StatusText.Text = m), CancellationToken.None);

    private static string Describe(FixResult res) => res.Outcome switch
    {
        FixOutcome.Succeeded => "Xong: " + res.Message,
        FixOutcome.Simulated => "Chạy thử xong, chưa mở/thay đổi gì. Bỏ tick \"Chạy thử\" để làm thật.",
        FixOutcome.Blocked => "Đã khóa: " + res.Message,
        FixOutcome.StillPresent => "Chưa thấy bộ cài chạy khi kiểm tra lại (có thể đang khởi động): " + res.Message,
        FixOutcome.Unverified => "Đã làm nhưng chưa kiểm tra lại được: " + res.Message,
        FixOutcome.Cancelled => "Đã dừng.",
        _ => "Không làm được: " + res.Message
    };
}
