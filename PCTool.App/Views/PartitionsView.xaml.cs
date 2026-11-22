using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Partitions;

namespace PCTool.Views;

public partial class PartitionsView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());
    private IReadOnlyList<VolumeInfo> _volumes = Array.Empty<VolumeInfo>();

    public PartitionsView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
    }

    private bool Dry => DryRunBox.IsChecked == true;

    private void SetBusy(bool busy)
    {
        foreach (var b in new[] { ScanButton, ShrinkButton, ExtButton, LetterButton }) b.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private static string Describe(FixResult res) => res.Outcome switch
    {
        FixOutcome.Succeeded => "Xong: " + res.Message + " Bấm Đọc danh sách ổ để xem lại.",
        FixOutcome.Simulated => "Chạy thử xong, chưa thay đổi gì. Bỏ tick \"Chạy thử\" để làm thật.",
        FixOutcome.Blocked => "Đã khóa: " + res.Message,
        FixOutcome.StillPresent => "Chưa đạt khi kiểm tra lại: " + res.Message,
        FixOutcome.Unverified => "Đã làm nhưng chưa kiểm tra lại được: " + res.Message,
        FixOutcome.Cancelled => "Đã dừng.",
        _ => "Không làm được: " + res.Message
    };

    private VolumeInfo? Find(string letter)
    {
        var l = letter.Trim().ToUpperInvariant();
        var v = _volumes.FirstOrDefault(x => x.Letter == l);
        if (v == null) StatusText.Text = "Hãy bấm Đọc danh sách ổ và nhập đúng ký tự ổ có trong danh sách.";
        return v;
    }

    private async Task RunFix(Func<IFix> make)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }
        IFix fix;
        try { fix = make(); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) + (Dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng.)" : "") + "\n\nĐồng ý?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { StatusText.Text = "Đã hủy."; return; }
        SetBusy(true);
        try
        {
            var res = await _executor.RunAsync(fix, SettingsService.Default.BuildOptions(Dry), new Progress<string>(m => StatusText.Text = m), CancellationToken.None);
            StatusText.Text = Describe(res);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Có lỗi: " + ex.Message;
            Logger.Error($"{fix.Title} lỗi: {ex}");
        }
        finally { SetBusy(false); }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "Đang đọc...";
        try
        {
            _volumes = await VolumeScanner.ReadAsync(null, CancellationToken.None);
            ListBox.Text = _volumes.Count == 0 ? "Không thấy ổ cứng nào." : string.Join("\n", _volumes.Select(v => v.Describe()));
            StatusText.Text = "Đã đọc (chỉ đọc, chưa đổi gì).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { StatusText.Text = "Chưa đọc được: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void ShrinkButton_Click(object sender, RoutedEventArgs e)
    {
        var v = Find(SrcBox.Text);
        if (v == null) return;
        if (!long.TryParse(MbBox.Text.Trim(), out var mb)) { StatusText.Text = "Dung lượng phải là số MB."; return; }
        await RunFix(() => new ShrinkAndCreateFix(v, mb, NewBox.Text.Trim().ToUpperInvariant(), LabelBox.Text.Trim(), ShrinkCodeBox.Text.Trim()));
    }

    private async void ExtButton_Click(object sender, RoutedEventArgs e)
    {
        var v = Find(ExtBox.Text);
        if (v == null) return;
        await RunFix(() => new ExtendIntoFreeSpaceFix(v, ExtCodeBox.Text.Trim()));
    }

    private async void LetterButton_Click(object sender, RoutedEventArgs e)
    {
        var v = Find(OldBox.Text);
        if (v == null) return;
        await RunFix(() => new ChangeLetterFix(v, ToBox.Text.Trim().ToUpperInvariant(), LetterCodeBox.Text.Trim()));
    }
}
