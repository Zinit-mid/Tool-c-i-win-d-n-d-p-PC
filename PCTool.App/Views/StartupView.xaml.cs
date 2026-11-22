using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Startup;

namespace PCTool.Views;

public partial class StartupView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());

    private sealed class Row
    {
        public Row(StartupItem i) { Item = i; }
        public StartupItem Item { get; }
        public string Name => Item.Name;
        public string Where => Item.Where;
        public string Command => Item.Command;
        public string StateText => Item.Enabled ? "Đang bật" : "Đã tắt";
    }

    public StartupView() { InitializeComponent(); DryRunBox.IsChecked = SettingsService.Default.DryRunDefault; }

    private async Task ReloadAsync()
    {
        Progress.IsIndeterminate = true;
        try
        {
            var items = await StartupScanner.ScanAsync(null, CancellationToken.None);
            Grid.ItemsSource = items.Select(i => new Row(i)).ToList();
            StatusText.Text = $"{items.Count} mục, đang bật {items.Count(i => i.Enabled)}.";
        }
        catch (Exception ex) { StatusText.Text = "Không quét được: " + ex.Message; }
        finally { Progress.IsIndeterminate = false; }
    }

    private async void Scan_Click(object s, RoutedEventArgs e) => await ReloadAsync();

    private async Task Toggle(bool enable)
    {
        if (Grid.SelectedItem is not Row row) { StatusText.Text = "Chưa chọn mục nào."; return; }
        IFix fix;
        try { fix = new StartupToggleFix(row.Item, enable); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        var dry = DryRunBox.IsChecked == true;
        StatusText.Text = await FixUi.RunAsync(_executor, fix, dry, m => StatusText.Text = m);
        if (!dry) await ReloadAsync();
    }

    private async void Disable_Click(object s, RoutedEventArgs e) => await Toggle(false);
    private async void Enable_Click(object s, RoutedEventArgs e) => await Toggle(true);
}
