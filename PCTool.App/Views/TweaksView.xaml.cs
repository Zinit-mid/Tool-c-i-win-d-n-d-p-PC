using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Tweaks;

namespace PCTool.Views;

public partial class TweaksView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());
    private readonly Dictionary<string, TextBlock> _state = new();

    public TweaksView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        foreach (var t in TweakCatalog.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);
            var on = new Button { Content = "Bật", Width = 70, Height = 28, Margin = new Thickness(0, 0, 6, 0), Tag = (t.Id, true) };
            var off = new Button { Content = "Tắt", Width = 70, Height = 28, Tag = (t.Id, false) };
            on.Click += Tweak_Click; off.Click += Tweak_Click;
            buttons.Children.Add(on); buttons.Children.Add(off);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var st = new TextBlock { Text = "(chưa đọc)", Foreground = System.Windows.Media.Brushes.Gray };
            _state[t.Id] = st;
            text.Children.Add(st);
            row.Children.Add(buttons); row.Children.Add(text);
            Rows.Children.Add(row);
        }
    }

    private bool Dry => DryRunBox.IsChecked == true;

    private static string Describe(FixResult res) => res.Outcome switch
    {
        FixOutcome.Succeeded => "Xong: " + res.Message,
        FixOutcome.Simulated => "Chạy thử xong, chưa thay đổi gì. Bỏ tick \"Chạy thử\" để làm thật.",
        FixOutcome.Blocked => "Đã khóa: " + res.Message,
        FixOutcome.StillPresent => "Chưa đạt khi kiểm tra lại: " + res.Message,
        FixOutcome.Unverified => "Đã làm nhưng chưa kiểm tra lại được: " + res.Message,
        FixOutcome.Cancelled => "Đã dừng.",
        _ => "Không làm được: " + res.Message
    };

    private async Task RunFix(IFix fix)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) + (Dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng.)" : "") + "\n\nĐồng ý?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { StatusText.Text = "Đã hủy."; return; }
        Progress.IsIndeterminate = true;
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
        finally { Progress.IsIndeterminate = false; }
    }

    private async void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        Progress.IsIndeterminate = true;
        try
        {
            var states = await TweakCatalog.ReadAsync(null, CancellationToken.None);
            foreach (var (id, st) in states)
                _state[id].Text = st switch { TweakCatalog.State.On => "Đang BẬT", TweakCatalog.State.Off => "Đang TẮT", _ => "Mặc định của Windows (chưa đặt)" };
            StatusText.Text = "Đã đọc (chưa đổi gì).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { StatusText.Text = "Chưa đọc được: " + ex.Message; }
        finally { Progress.IsIndeterminate = false; }
    }

    private async void Tweak_Click(object sender, RoutedEventArgs e)
    {
        var (id, on) = ((string, bool))((Button)sender).Tag;
        await RunFix(new ApplyTweakFix(id, on));
    }

    private async void Power_Click(object sender, RoutedEventArgs e) =>
        await RunFix(new SetPowerPlanFix(Enum.Parse<PowerPlan>((string)((Button)sender).Tag)));
}
