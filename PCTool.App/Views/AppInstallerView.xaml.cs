using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;

namespace PCTool.Views;

public partial class AppInstallerView : UserControl
{
    private sealed class AppRow : INotifyPropertyChanged
    {
        private bool _selected;
        private string _result = "";

        public AppRow(AppEntry entry)
        {
            Entry = entry;
            _selected = entry.SelectedByDefault;
        }

        public AppEntry Entry { get; }
        public string Name => Entry.Name;
        public string Group => Entry.Group ?? "";

        public string KindText => Entry.Kind switch
        {
            AppKind.Winget => "Cài bằng winget",
            AppKind.WebShortcut => "Lối tắt trang web",
            _ => "Mở trang tải"
        };

        public bool Selected { get => _selected; set { _selected = value; Raise(); } }
        public string Result { get => _result; set { _result = value; Raise(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly AppCatalog _catalog = new();
    private readonly List<AppRow> _rows = new();
    private bool _busy;
    private bool _selectAllChanging;
    private CancellationTokenSource? _cts;
    private bool _readOnly;

    public AppInstallerView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        ProfileBox.ItemsSource = AppProfiles.All.Select(x => x.Name).ToList();
        ProfileBox.SelectedIndex = 0;
        Reload();
    }

    private void Reload()
    {
        var state = _catalog.LoadAll();
        _readOnly = state.ReadOnly;

        // Giữ lại các ô đã tick khi nạp lại danh sách.
        var ticked = _rows.Where(r => r.Selected).Select(r => r.Entry.Id).ToHashSet();
        bool first = _rows.Count == 0;
        _rows.Clear();
        foreach (var e in state.Items)
        {
            var row = new AppRow(e);
            if (!first) row.Selected = ticked.Contains(e.Id);
            _rows.Add(row);
        }

        WarningText.Text = string.Join("\n", state.Warnings);
        WarningText.Visibility = state.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = ImportButton.IsEnabled = RestoreButton.IsEnabled = !_readOnly;
        Render();
    }

    // ------------------------------------------------------------------ giao diện dạng bảng menu theo nhóm

    // Thứ tự nhóm giống một bảng menu: nhóm lạ (người dùng tự đặt) xếp sau cùng, "Khác" cuối hẳn.
    private static readonly string[] GroupOrder =
        { "Trình duyệt", "Tiện ích", "Lưu trữ", "Xem phim", "Cắt ghép video", "Đọc PDF", "Điều khiển từ xa", "Liên lạc" };

    private static string GlyphFor(string group) => group switch
    {
        "Trình duyệt" => "\uE774",
        "Tiện ích" => "\uE713",
        "Lưu trữ" => "\uE753",
        "Xem phim" => "\uE714",
        "Cắt ghép video" => "\uE714",
        "Đọc PDF" => "\uE8A5",
        "Điều khiển từ xa" => "\uE7F4",
        "Liên lạc" => "\uE715",
        _ => "\uE71D"
    };

    private static readonly Color[] BadgeColors =
    {
        Color.FromRgb(0x2B, 0x6C, 0xB0), Color.FromRgb(0xC0, 0x39, 0x2B), Color.FromRgb(0x1E, 0x8E, 0x5A), Color.FromRgb(0x8E, 0x44, 0xAD),
        Color.FromRgb(0xD3, 0x7B, 0x12), Color.FromRgb(0x16, 0x7C, 0x8A), Color.FromRgb(0x5D, 0x6D, 0x7E), Color.FromRgb(0xB0, 0x3A, 0x7A)
    };

    // Biểu tượng là ô màu + chữ cái đầu của tên (không phải logo chính thức của hãng).
    internal static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length >= 2) return (char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[1][0])).Trim();
        var w = words[0];
        var second = w.Skip(1).FirstOrDefault(char.IsUpper);
        return second == default ? char.ToUpperInvariant(w[0]).ToString() : char.ToUpperInvariant(w[0]).ToString() + second;
    }

    private void Render()
    {
        GroupsPanel.Children.Clear();
        int Order(string g) { var i = Array.IndexOf(GroupOrder, g); return g == "Khác" ? 1000 : i < 0 ? 100 : i; }

        var groups = _rows.GroupBy(r => r.Group.Length == 0 ? "Khác" : r.Group).OrderBy(g => Order(g.Key));
        foreach (var g in groups) GroupsPanel.Children.Add(MakeCard(g.Key, g.ToList()));
    }

    private Border MakeCard(string group, List<AppRow> rows)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock
        {
            Text = GlyphFor(group), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        header.Children.Add(new TextBlock
        {
            Text = group.ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), VerticalAlignment = VerticalAlignment.Center
        });

        var stack = new StackPanel();
        stack.Children.Add(header);
        foreach (var r in rows) stack.Children.Add(MakeItem(r));

        return new Border
        {
            Width = 250, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Child = stack
        };
    }

    private CheckBox MakeItem(AppRow row)
    {
        var idSum = row.Entry.Id.Sum(c => c);
        var badge = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Margin = new Thickness(2, 0, 8, 0),
            Background = new SolidColorBrush(BadgeColors[idSum % BadgeColors.Length]),
            Child = new TextBlock
            {
                Text = Initials(row.Name), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var name = new TextBlock { Text = row.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var result = new TextBlock
        {
            Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 170,
            Text = row.Result, Visibility = row.Result.Length == 0 ? Visibility.Collapsed : Visibility.Visible
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(result);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(badge);
        content.Children.Add(text);

        var cb = new CheckBox
        {
            Content = content, IsChecked = row.Selected, Margin = new Thickness(0, 3, 0, 3), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = $"{row.Name} · {row.KindText}"
        };
        cb.Checked += (_, _) => row.Selected = true;
        cb.Unchecked += (_, _) => row.Selected = false;
        row.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(AppRow.Selected)) cb.IsChecked = row.Selected;
            else if (a.PropertyName == nameof(AppRow.Result))
            {
                result.Text = row.Result;
                result.Visibility = row.Result.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        };

        var menu = new ContextMenu();
        var remove = new MenuItem { Header = row.Entry.BuiltIn ? "Ẩn mục này (hiện lại bằng nút bên dưới)" : "Xóa mục này" };
        remove.Click += (_, _) => RemoveRow(row);
        menu.Items.Add(remove);
        cb.ContextMenu = menu;
        return cb;
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectAllChanging) return;
        bool on = SelectAllBox.IsChecked == true;
        foreach (var r in _rows) r.Selected = on;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        AddButton.IsEnabled = ImportButton.IsEnabled = RestoreButton.IsEnabled = !busy && !_readOnly;
        ExportButton.IsEnabled = !busy;
        DryRunBox.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Default.SafeMode)
        {
            StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage;
            return;
        }

        var chosen = _rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show("Chưa tick ứng dụng nào để cài.", "PCTool");
            return;
        }

        bool dryRun = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", chosen.Select(r => new InstallAppFix(r.Entry, new ProcessRunner(), "", _ => { }).Actions[0])) +
                   (dryRun ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, "Xác nhận cài ứng dụng", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        foreach (var r in chosen) r.Result = "";

        var runner = new ProcessRunner();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var publicDesktop = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
        var executor = new FixExecutor(new RestorePointService());
        var options = SettingsService.Default.BuildOptions(dryRun);
        int ok = 0, bad = 0;

        try
        {
            for (int i = 0; i < chosen.Count; i++)
            {
                var row = chosen[i];
                row.Result = "Đang xử lý...";
                StatusText.Text = $"({i + 1}/{chosen.Count}) {row.Name}";
                try
                {
                    var fix = new InstallAppFix(row.Entry, runner, desktop, OpenUrl, publicDesktop);
                    var result = await executor.RunAsync(fix, options, new Progress<string>(m => StatusText.Text = m), ct);
                    row.Result = Describe(result);
                    if (result.IsSuccess) ok++; else bad++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    row.Result = "Lỗi: " + ex.Message;
                    Logger.Error($"Cài {row.Name} lỗi: {ex.Message}");
                    bad++;
                }
            }

            StatusText.Text = dryRun
                ? $"[Chạy thử] Đã mô phỏng {ok} mục, máy chưa bị thay đổi."
                : $"Xong: {ok} mục thành công" + (bad > 0 ? $", {bad} mục chưa được (xem cột Kết quả)." : ".");
            Logger.Info(StatusText.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng. Các mục chưa làm vẫn chưa được cài.";
            Logger.Warn("Đã dừng cài ứng dụng.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string Describe(FixResult r) => r.Outcome switch
    {
        FixOutcome.Succeeded => "✔ " + r.Message,
        FixOutcome.Simulated => "Chạy thử: sẽ làm như trong hộp xác nhận",
        FixOutcome.Blocked => "Đã khóa: " + r.Message,
        FixOutcome.StillPresent => "Chưa thấy trên máy sau khi cài: " + r.Message,
        FixOutcome.Unverified => "Đã làm nhưng chưa kiểm tra lại được: " + r.Message,
        FixOutcome.Cancelled => "Đã dừng",
        _ => "✖ " + r.Message
    };

    private void OpenDesktop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            StatusText.Text = "Đã mở thư mục Desktop: " + dir;
        }
        catch (Exception ex) { StatusText.Text = "Không mở được thư mục Desktop: " + ex.Message; }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddAppWindow(_rows.Select(r => r.Entry.Id).ToList()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        var r = _catalog.AddOrUpdate(dlg.Result);
        StatusText.Text = r.Message;
        Reload();
    }

    private void RemoveRow(AppRow row)
    {
        if (_busy) return;
        var what = row.Entry.BuiltIn ? "ẩn (mục có sẵn, có thể hiện lại sau)" : "xóa";
        if (MessageBox.Show($"Bạn muốn {what} \"{row.Name}\"?", "Xác nhận", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        StatusText.Text = _catalog.Remove(row.Entry.Id).Message;
        Reload();
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var prof = AppProfiles.All.FirstOrDefault(x => x.Name == ProfileBox.SelectedItem as string);
        if (prof == null) return;
        int n = 0;
        foreach (var r in _rows) { r.Selected = prof.Ids.Contains(r.Entry.Id); if (r.Selected) n++; }
        StatusText.Text = $"Bộ \"{prof.Name}\": đã tick {n}/{prof.Ids.Count} app (app không có trong danh sách sẽ bị bỏ qua). Chưa cài gì, bấm \"Cài các mục đã chọn\".";
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _catalog.RestoreHiddenDefaults().Message;
        Reload();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = "pctool-apps-export.json", Filter = "Danh sách app (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _catalog.Export(DateTimeOffset.Now));
            StatusText.Text = "Đã xuất danh sách (chỉ gồm các app bạn tự thêm) ra: " + dlg.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Không ghi được file: " + ex.Message;
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Danh sách app (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > AppCatalog.MaxImportBytes)
            {
                StatusText.Text = "File quá lớn, không phải danh sách app.";
                return;
            }

            var result = _catalog.Import(File.ReadAllText(dlg.FileName), (existing, incoming) =>
                MessageBox.Show(
                    $"Đã có mục \"{existing.Name}\" (mã {existing.Id}).\nThay bằng bản \"{incoming.Name}\" trong file?",
                    "Trùng mục", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);

            StatusText.Text = $"Đã nhập: thêm {result.Added}, thay {result.Replaced}, giữ bản cũ {result.KeptExisting}" +
                              (result.Skipped.Count > 0 ? $", bỏ qua {result.Skipped.Count} mục sai: " + string.Join("; ", result.Skipped.Take(3)) : "") + ".";
            Reload();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "Không nhập được: " + ex.Message;
        }
    }
}
