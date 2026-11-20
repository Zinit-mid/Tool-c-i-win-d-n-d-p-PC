using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ObservableCollection<AppRow> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _readOnly;

    public AppInstallerView()
    {
        InitializeComponent();
        AppsGrid.ItemsSource = _rows;
        Reload();
    }

    private void Reload()
    {
        var state = _catalog.Load();
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
        AddButton.IsEnabled = RemoveButton.IsEnabled = ImportButton.IsEnabled = RestoreButton.IsEnabled = !_readOnly;
    }

    private void SetBusy(bool busy)
    {
        InstallButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        AddButton.IsEnabled = RemoveButton.IsEnabled = ImportButton.IsEnabled = RestoreButton.IsEnabled = !busy && !_readOnly;
        ExportButton.IsEnabled = !busy;
        DryRunBox.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
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
        var executor = new FixExecutor(new RestorePointService());
        var options = new FixOptions { DryRun = dryRun, CreateRestorePoint = false };
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
                    var fix = new InstallAppFix(row.Entry, runner, desktop, OpenUrl);
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

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddAppWindow(_rows.Select(r => r.Entry.Id).ToList()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        var r = _catalog.AddOrUpdate(dlg.Result);
        StatusText.Text = r.Message;
        Reload();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row)
        {
            MessageBox.Show("Bấm chọn một dòng trước.", "PCTool");
            return;
        }

        var what = row.Entry.BuiltIn ? "ẩn (mục mặc định, có thể hiện lại sau)" : "xóa";
        if (MessageBox.Show($"Bạn muốn {what} \"{row.Name}\"?", "Xác nhận", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        StatusText.Text = _catalog.Remove(row.Entry.Id).Message;
        Reload();
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
