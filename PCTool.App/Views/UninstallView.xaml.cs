using System.Text;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Folders;
using PCTool.Core.Framework;
using PCTool.Core.Uninstall;

namespace PCTool.Views;

public partial class UninstallView : UserControl
{
    private sealed record Row(InstalledProgram Program, UninstallVerdict Verdict)
    {
        public string Name => Program.Name;
        public string Version => Program.Version;
        public string Publisher => Program.Publisher;
        public string SizeText => Program.SizeText;
        public string Status => Verdict.Allowed ? (Verdict.Warning is null ? "Gỡ được" : "Gỡ được (có cảnh báo)") : "Không cho gỡ";
    }

    private CancellationTokenSource? _cts;
    private List<Row> _rows = new();
    private AppActivity? _activity;
    private IReadOnlyList<LeftoverFolder>? _leftovers;
    private InstalledProgram? _leftoverFor;

    public UninstallView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
    }

    private Row? Selected => ProgramsGrid.SelectedItem as Row;

    private void SetBusy(bool busy)
    {
        LoadButton.IsEnabled = ActivityButton.IsEnabled = UninstallButton.IsEnabled = LeftoverButton.IsEnabled = !busy;
        StopButton.IsEnabled = !busy && _activity is { Any: true };
        MoveButton.IsEnabled = !busy && _leftovers is { Count: > 0 };
        CancelButton.IsEnabled = busy;
        Progress.IsIndeterminate = busy;
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        ProgramsGrid.ItemsSource = string.IsNullOrEmpty(q) ? _rows : _rows.Where(r => r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyFilter(); }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        try
        {
            var list = await InstalledProgramsScanner.RunAsync(null, ct);
            _rows = list.Select(p => new Row(p, UninstallPolicy.Evaluate(p))).ToList();
            ApplyFilter();
            return true;
        }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; return false; }
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang đọc danh sách chương trình (chỉ đọc)...";
        try { if (await LoadAsync(_cts.Token)) StatusText.Text = $"Có {_rows.Count} chương trình. Chọn một dòng rồi làm theo các bước 1-5."; }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Có lỗi, xem nhật ký."; Logger.Error($"Lỗi đọc danh sách chương trình: {ex}"); }
        finally { SetBusy(false); }
    }

    private bool NeedSelection(out Row row)
    {
        row = Selected!;
        if (row is not null) return true;
        StatusText.Text = "Hãy chọn một chương trình trong danh sách.";
        return false;
    }

    private async void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var row)) return;
        _activity = null;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var a = await AppActivityScanner.ScanAsync(row.Program.InstallLocation, null, _cts.Token);
            _activity = a;
            var sb = new StringBuilder();
            sb.AppendLine(a.Any ? $"\"{row.Name}\" đang chạy:" : $"Không thấy tiến trình hay dịch vụ nào của \"{row.Name}\" đang chạy từ thư mục cài đặt.");
            if (a.Processes.Count > 0) sb.AppendLine("Tiến trình: " + string.Join(", ", a.Processes));
            if (a.Services.Count > 0) sb.AppendLine("Dịch vụ: " + string.Join(", ", a.Services));
            ResultBox.Text = sb.ToString();
            StatusText.Text = a.Any ? "Có thể bấm \"2. Dừng\" rồi \"3. Gỡ\"." : "Có thể bấm \"3. Gỡ\".";
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        finally { SetBusy(false); }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var row) || _activity is not { Any: true }) return;
        try { await RunAsync(new StopAppFix(row.Program, _activity), refreshAfter: false); }
        catch (ArgumentException ex) { StatusText.Text = "Chưa thể dừng: " + ex.Message; }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var row)) return;
        if (!row.Verdict.Allowed) { StatusText.Text = row.Verdict.Reason; return; }
        try { await RunAsync(new UninstallFix(row.Program), refreshAfter: true); }
        catch (ArgumentException ex) { StatusText.Text = "Chưa thể gỡ: " + ex.Message; }
    }

    private async void LeftoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var row)) return;
        _leftovers = null; _leftoverFor = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        StatusText.Text = "Đang quét thư mục còn sót (chỉ đọc)...";
        try
        {
            var now = await InstalledProgramsScanner.RunAsync(null, ct);
            var found = await Task.Run(() => LeftoverScanner.Scan(row.Program, now, null, ct), ct);
            _leftovers = found; _leftoverFor = row.Program;
            ResultBox.Text = found.Count == 0
                ? "Không thấy thư mục nào còn sót."
                : "Thư mục còn sót:\n" + string.Join("\n", found.Select(f => $"• {f.Path}  ({f.Files} file, {SizeText.Of(f.Bytes)})"));
            StatusText.Text = found.Count == 0 ? "Xong." : "Xem danh sách rồi bấm \"5. Chuyển tàn dư\" nếu đồng ý (không xóa, chỉ chuyển sang nơi riêng).";
        }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Có lỗi, xem nhật ký."; Logger.Error($"Lỗi quét tàn dư: {ex}"); }
        finally { SetBusy(false); }
    }

    private async void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_leftovers is not { Count: > 0 } || _leftoverFor is null) { StatusText.Text = "Hãy quét tàn dư trước."; return; }
        try { await RunAsync(new LeftoverMoveFix(_leftoverFor.Name, _leftovers), refreshAfter: false); }
        catch (ArgumentException ex) { StatusText.Text = "Chưa thể chuyển: " + ex.Message; }
    }

    private async Task RunAsync(IFix fix, bool refreshAfter)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }

        bool dry = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Đang thực hiện...";
        try
        {
            var executor = new FixExecutor(new RestorePointService());
            var progress = new Progress<string>(m => StatusText.Text = m);
            var result = await executor.RunAsync(fix, SettingsService.Default.BuildOptions(dry), progress, _cts.Token);
            StatusText.Text = result.Outcome switch
            {
                FixOutcome.Succeeded => result.Message,
                FixOutcome.Simulated => "Chạy thử xong, máy chưa bị thay đổi. Bỏ tick \"Chạy thử\" để làm thật.",
                FixOutcome.Blocked => "Đã khóa: " + result.Message,
                FixOutcome.StillPresent => "Đã chạy nhưng kiểm tra lại thấy chưa xong (chương trình vẫn còn hoặc chưa dừng hết): " + result.Message,
                FixOutcome.Unverified => "Đã chạy nhưng chưa kiểm tra lại được: " + result.Message,
                FixOutcome.Cancelled => "Đã dừng.",
                _ => "Không làm được: " + result.Message
            };
            if (refreshAfter && result.Outcome != FixOutcome.Simulated && result.Outcome != FixOutcome.Blocked) await LoadAsync(_cts.Token);
            _activity = null;
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Có lỗi, xem nhật ký."; Logger.Error($"Lỗi gỡ phần mềm: {ex}"); }
        finally { SetBusy(false); }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
