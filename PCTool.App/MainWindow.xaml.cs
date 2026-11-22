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

        var settings = SettingsService.Default;
        if (settings.Warning != null) Logger.Warn(settings.Warning);
        SafeModeBox.IsChecked = settings.SafeMode;
        SafeBanner.Visibility = settings.SafeMode ? Visibility.Visible : Visibility.Collapsed;

        BuildHome();

        UpdateService.CleanupLeftovers();
        UpdateText.Text = $"Phiên bản {UpdateService.CurrentVersion}";
        Loaded += async (_, _) => await CheckForUpdateAsync(silent: true);
    }

    private record Tile(string Icon, string Title, string Desc, string Page);

    private static readonly (string Group, Tile[] Tiles)[] Catalog =
    {
        ("Máy tính của bạn", new[]
        {
            new Tile("🩺", "Quét lỗi máy", "RAM, mạng, khởi động, Windows Update, ổ cứng", "Tổng quan (quét lỗi)"),
            new Tile("🖥", "Thông số phần cứng", "CPU, RAM, ổ cứng, card đồ họa, pin", "Thông số phần cứng"),
            new Tile("🧹", "Dọn dẹp rác", "Xóa file tạm, cache, thùng rác", "Dọn dẹp rác"),
            new Tile("🗂", "Dọn thư mục thông minh", "Tìm file trùng, sắp xếp Downloads", "Dọn thư mục thông minh"),
            new Tile("🧰", "Gỡ phần mềm cứng đầu", "Gỡ bằng bộ gỡ riêng + dọn thư mục sót", "Gỡ phần mềm cứng đầu"),
            new Tile("🔐", "File cứng đầu / Quyền", "Lấy lại quyền, chuyển file không xóa được", "File cứng đầu / Quyền"),
            new Tile("💽", "Phân vùng ổ cứng", "Tách ổ, mở rộng, đổi ký tự ổ", "Phân vùng ổ cứng"),
            new Tile("⚙", "Thiết lập Windows", "Hiện đuôi file, nguồn điện, giao diện", "Thiết lập Windows"),
        }),
        ("Cài lại Windows", new[]
        {
            new Tile("🪟", "Tạo USB cài Windows", "Ghi ISO ra USB (xóa USB), có autounattend", "Tạo USB cài Windows"),
            new Tile("🔄", "Cài lại Win trực tiếp", "Giữ file / xóa sạch, mở công cụ Microsoft", "Cài lại Win trực tiếp"),
            new Tile("📶", "Sao lưu Driver / Wi-Fi", "Driver, Wi-Fi, Zalo, xem BitLocker", "Sao lưu Driver / Wi-Fi"),
            new Tile("💾", "Sao lưu / Khôi phục app", "Dữ liệu cá nhân + danh sách app", "Sao lưu / Khôi phục app"),
            new Tile("♻", "Khôi phục sau cài Win", "Driver, Wi-Fi, app, dữ liệu, Zalo", "Khôi phục sau cài Win"),
        }),
        ("Cài đặt & tiện ích", new[]
        {
            new Tile("📦", "Cài nhanh ứng dụng", "Cài hàng loạt bằng winget, thêm app gợi ý", "Cài nhanh ứng dụng"),
            new Tile("🧩", "Tiện ích khác", "Kiểm tra thiết bị, file ẩn, Kho Driver IRST", "Tiện ích khác"),
        }),
        ("Sửa lỗi & bảo vệ", new[]
        {
            new Tile("🖨", "Chẩn đoán máy in", "Spooler, hàng đợi, driver, cổng", "Chẩn đoán máy in"),
            new Tile("📄", "Sửa lỗi Word / Excel", "Mở an toàn, đặt lại mẫu Normal", "Sửa lỗi Word / Excel"),
            new Tile("⬆", "Windows Update", "Tạm dừng có hẹn giờ tự bật lại", "Windows Update"),
            new Tile("🛡", "Windows Defender", "Tắt tạm có hẹn giờ tự bật lại", "Windows Defender"),
        }),
        ("PDF", new[]
        {
            new Tile("📑", "Công cụ PDF", "Gộp, tách, xoay trang, ảnh thành PDF", "Công cụ PDF"),
            new Tile("👁", "Xem PDF", "Xem nhanh, xuất trang ra ảnh", "Xem PDF"),
        }),
    };

    private void BuildHome()
    {
        foreach (var (group, tiles) in Catalog)
        {
            HomePanel.Children.Add(new TextBlock
            {
                Text = group, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("AccentDark"), Margin = new Thickness(4, 14, 0, 8)
            });
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
            foreach (var t in tiles)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = t.Icon, FontSize = 26, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji") });
                content.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text = t.Desc, FontSize = 11.5, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap });
                var btn = new System.Windows.Controls.Button { Content = content, Style = (Style)FindResource("TileButton"), Tag = t.Page };
                btn.Click += (_, _) => Navigate(t.Page);
                grid.Children.Add(btn);
            }
            HomePanel.Children.Add(grid);
        }
    }

    private void Navigate(string header)
    {
        for (int i = 0; i < Pages.Items.Count; i++)
            if (Pages.Items[i] is System.Windows.Controls.TabItem ti && ti.Header as string == header)
            {
                Pages.SelectedIndex = i;
                bool home = i == 0;
                BackBar.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
                PageTitle.Text = header;
                return;
            }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => Navigate("Trang chủ");

    private void SafeModeBox_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Default;
        bool want = SafeModeBox.IsChecked == true;

        // Tắt Chế độ an toàn luôn cần xác nhận (tránh bấm nhầm trên máy công ty).
        if (!want && MessageBox.Show(
                "Tắt Chế độ an toàn? Sau đó PCTool có thể thay đổi máy (dọn file, cài app, tắt Defender...).",
                "Tắt Chế độ an toàn", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            SafeModeBox.IsChecked = true;
            return;
        }

        bool saved = settings.SetSafeMode(want);
        SafeBanner.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
        Logger.Info(want ? "Đã BẬT Chế độ an toàn." : "Đã TẮT Chế độ an toàn.");
        if (!saved)
            MessageBox.Show("Không lưu được cài đặt nên lần mở sau có thể trở lại như cũ. Lần mở này vẫn có hiệu lực.",
                "PCTool", MessageBoxButton.OK, MessageBoxImage.Information);
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
