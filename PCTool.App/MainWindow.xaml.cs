using System.Windows;
using PCTool.Core.Framework;
using PCTool.Services;

namespace PCTool;

public partial class MainWindow : Window
{
    private UpdateInfo? _update;
    private bool _updating;

    public MainWindow()
    {
        InitializeComponent();

        Logger.LineWritten += line => Dispatcher.BeginInvoke(new Action(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }));

        Logger.Info("PCTool đã khởi động. File log: " + Logger.LogFile);

        UpdateService.CleanupLeftovers();
        UpdateText.Text = $"Phiên bản {UpdateService.CurrentVersion}";
        Loaded += async (_, _) => await CheckForUpdateAsync(silent: true);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdateAsync(silent: false);

    private async Task CheckForUpdateAsync(bool silent)
    {
        if (_updating) return;
        CheckUpdateButton.IsEnabled = false;
        UpdateText.Text = "Đang kiểm tra cập nhật...";
        try
        {
            var info = await UpdateService.CheckAsync(CancellationToken.None);
            if (info == null)
            {
                _update = null;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                UpdateText.Text = $"Phiên bản {UpdateService.CurrentVersion} (mới nhất)";
                if (!silent) Logger.Info("Đang dùng bản mới nhất.");
            }
            else
            {
                _update = info;
                InstallUpdateButton.Visibility = Visibility.Visible;
                UpdateText.Text = $"Phiên bản {UpdateService.CurrentVersion} - có bản mới {info.Version}";
                Logger.Info($"Có bản mới {info.Version} (đang dùng {UpdateService.CurrentVersion}).");
            }
        }
        catch (Exception ex)
        {
            UpdateText.Text = $"Phiên bản {UpdateService.CurrentVersion} (không kiểm tra được cập nhật)";
            Logger.Warn($"Không kiểm tra được cập nhật: {ex.Message}");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = !_updating;
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var info = _update;
        if (info == null || _updating) return;

        var notes = info.Notes.Length > 400 ? info.Notes[..400] + "…" : info.Notes;
        var ask = MessageBox.Show(
            $"Cập nhật PCTool {UpdateService.CurrentVersion} → {info.Version}?\n\n" +
            (notes.Length > 0 ? notes + "\n\n" : "") +
            "App sẽ tải bản mới, tự khởi động lại và mở lại với quyền Administrator.",
            "Cập nhật PCTool", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        _updating = true;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateText.Text = $"Đang tải bản {info.Version}...";

        try
        {
            await UpdateService.InstallAsync(info, new Progress<double>(v => UpdateProgress.Value = v), CancellationToken.None);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Error($"Cập nhật thất bại: {ex}");
            UpdateText.Text = "Cập nhật thất bại, xem nhật ký.";
            MessageBox.Show("Cập nhật thất bại:\n" + ex.Message, "PCTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            _updating = false;
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }
}
