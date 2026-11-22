using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Drivers;
using PCTool.Core.Framework;

namespace PCTool.Views;

public partial class DriverScanView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());

    public DriverScanView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        FillVendors(null);
    }

    private void FillVendors(string? maker)
    {
        VendorPanel.Children.Clear();
        foreach (var p in DriverVendorPages.ForMaker(maker))
        {
            var b = new Button { Content = p.Title, Margin = new Thickness(0, 0, 8, 6), Tag = p.Url };
            b.Click += (_, _) =>
            {
                try { DriverVendorPages.Open((string)b.Tag, u => Process.Start(new ProcessStartInfo(u) { UseShellExecute = true })); }
                catch (Exception ex) { StatusText.Text = "Không mở được trang: " + ex.Message; }
            };
            VendorPanel.Children.Add(b);
        }
    }

    private void Busy(bool on) { ScanButton.IsEnabled = InstallButton.IsEnabled = !on; Progress.IsIndeterminate = on; }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        try
        {
            var r = await DriverScanner.ScanAsync(null, CancellationToken.None);
            MakerText.Text = $"Hãng/mẫu máy: {r.Maker} {r.Model}".Trim();
            FillVendors(r.Maker);
            ListBoxText.Text = r.Problems.Count == 0
                ? "Không có thiết bị nào báo lỗi hay thiếu driver."
                : string.Join(Environment.NewLine, r.Problems.Select(p => $"• {p.Name} [{p.DeviceClass}]: {p.Meaning}"));
            StatusText.Text = $"Thiếu hoặc lỗi driver: {r.MissingCount} thiết bị.";
            Logger.Info("Quét driver: " + StatusText.Text);
        }
        catch (Exception ex) { StatusText.Text = "Không quét được: " + ex.Message; }
        finally { Busy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        try { StatusText.Text = await FixUi.RunAsync(_executor, new WindowsUpdateDriverFix(), DryRunBox.IsChecked == true, m => StatusText.Text = m); }
        finally { Busy(false); }
    }
}
