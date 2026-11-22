using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCTool.Core.Framework;
using PCTool.Core.IsoBuild;
using PCTool.Core.Reinstall;

namespace PCTool.Views;

public partial class IsoBuildView : UserControl
{
    private readonly FixExecutor _executor = new(new RestorePointService());
    private IReadOnlyList<IsoImage> _images = Array.Empty<IsoImage>();
    private CancellationTokenSource? _cts;

    public IsoBuildView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        foreach (var a in IsoBuildCatalog.Apps)
            AppPanel.Children.Add(new CheckBox { Content = a.Title, Tag = a.Id, IsChecked = a.Suggested, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var t in IsoBuildCatalog.Tweaks)
            TweakPanel.Children.Add(new CheckBox { Content = t.Title, Tag = t.Id, IsChecked = true, Margin = new Thickness(0, 0, 0, 4) });
        OscText.Text = File.Exists(IsoBuildPlan.DefaultOscdimg)
            ? "Đã thấy oscdimg.exe (Windows ADK)."
            : "CHƯA thấy oscdimg.exe. Cài \"Windows ADK\" từ Microsoft (chỉ chọn Deployment Tools) trước khi bấm Tạo ISO.";
    }

    private static List<string> Ticked(Panel p) =>
        p.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

    private static string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    private void BrowseIso_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "File ISO (*.iso)|*.iso", Title = "Chọn ISO Windows gốc" };
        if (d.ShowDialog() == true) IsoBox.Text = d.FileName;
    }
    private void BrowseDriver_Click(object s, RoutedEventArgs e) { if (PickFolder("Chọn thư mục driver") is { } f) DriverBox.Text = f; }
    private void BrowseWork_Click(object s, RoutedEventArgs e) { if (PickFolder("Chọn thư mục làm việc") is { } f) WorkBox.Text = f; }
    private void BrowseOut_Click(object s, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Filter = "File ISO (*.iso)|*.iso", FileName = "Windows_Custom.iso", OverwritePrompt = false };
        if (d.ShowDialog() == true) OutBox.Text = d.FileName;
    }

    private async void Inspect_Click(object s, RoutedEventArgs e)
    {
        var path = IsoBox.Text.Trim();
        if (!IsoInspector.IsValidIsoPath(path, out var err)) { StatusText.Text = err; return; }
        Progress.IsIndeterminate = true; StatusText.Text = "Đang kiểm tra ISO (gắn chỉ đọc)...";
        try
        {
            var info = await new IsoInspector().InspectAsync(path, CancellationToken.None);
            _images = info.Images;
            EditionBox.ItemsSource = _images.Select(i => $"{i.Index}. {i.Name} ({i.Family}, {i.Arch})").ToList();
            EditionBox.SelectedIndex = _images.Count > 0 ? 0 : -1;
            StatusText.Text = _images.Count == 0 ? "Không đọc được danh sách phiên bản trong ISO này." : $"Thấy {_images.Count} phiên bản. Hãy chọn một.";
        }
        catch (Exception ex) { StatusText.Text = "Không kiểm tra được ISO: " + ex.Message; }
        finally { Progress.IsIndeterminate = false; }
    }

    private async void Build_Click(object s, RoutedEventArgs e)
    {
        int idx = EditionBox.SelectedIndex >= 0 && EditionBox.SelectedIndex < _images.Count ? _images[EditionBox.SelectedIndex].Index : 0;
        var drv = DriverBox.Text.Trim();
        var plan = new IsoBuildPlan(IsoBox.Text.Trim(), OutBox.Text.Trim(), WorkBox.Text.Trim(), idx,
            Ticked(AppPanel), Ticked(TweakPanel), drv.Length == 0 ? null : drv, IsoBuildPlan.DefaultOscdimg);
        if (plan.Validate() is { } bad) { StatusText.Text = bad; return; }

        _cts = new CancellationTokenSource();
        BuildButton.IsEnabled = false; StopButton.IsEnabled = true; Progress.IsIndeterminate = true;
        try
        {
            StatusText.Text = await FixUi.RunAsync(_executor, new BuildIsoFix(plan), DryRunBox.IsChecked == true, m => StatusText.Text = m, _cts.Token);
        }
        finally { BuildButton.IsEnabled = true; StopButton.IsEnabled = false; Progress.IsIndeterminate = false; }
    }

    private void Stop_Click(object s, RoutedEventArgs e) { _cts?.Cancel(); StatusText.Text = "Đang dừng, chờ dọn dẹp..."; }
}
