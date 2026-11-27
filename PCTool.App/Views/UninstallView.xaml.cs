using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCTool.Core.Folders;
using PCTool.Core.Framework;
using PCTool.Core.Uninstall;

namespace PCTool.Views;

public partial class UninstallView : UserControl
{
    private sealed class Row : INotifyPropertyChanged
    {
        public Row(InstalledProgram? program, StoreApp? store, UninstallVerdict verdict)
        {
            Program = program; Store = store; Verdict = verdict;
        }

        public InstalledProgram? Program { get; }
        public StoreApp? Store { get; }
        public UninstallVerdict Verdict { get; }
        public ImageSource? Icon { get; set; }
        public string Name => Program?.Name ?? Store!.Name;
        public string Version => Program?.Version ?? Store!.Version;
        public string Publisher => Program?.Publisher ?? Store!.PublisherText;
        public string SizeText => Program?.SizeText ?? "";
        public long SizeKb => Program?.SizeKb ?? 0;
        public string DateText => ProgramDates.Text(Program?.InstallDate);
        public string DateSort => ProgramDates.Parse(Program?.InstallDate)?.ToString("yyyyMMdd") ?? "";
        public string Kind => Store is null ? "" : "Store";
        public bool CanUninstall => Verdict.Allowed;

        private bool _checked;
        public bool Checked
        {
            get => _checked;
            set { if (_checked != value) { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked))); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Lấy icon nhỏ của file .exe/.ico/.dll như Windows làm; lỗi thì không có icon (không làm hỏng danh sách).</summary>
    private static ImageSource? LoadIcon(string? path)
    {
        if (path is null) return null;
        var small = new IntPtr[1];
        try
        {
            if (ExtractIconEx(path, 0, null, small, 1) == 0 || small[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(small[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception) { return null; }
        finally { if (small[0] != IntPtr.Zero) DestroyIcon(small[0]); }
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
        bool all = ShowAllBox.IsChecked == true;
        ProgramsGrid.ItemsSource = _rows
            .Where(r => (all || r.Verdict.Allowed) && (q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        SelectAllBox.IsChecked = false;
        UpdateTicked();
    }

    private IEnumerable<Row> ShownRows => ProgramsGrid.ItemsSource as IEnumerable<Row> ?? Enumerable.Empty<Row>();

    private void UpdateTicked()
    {
        int n = _rows.Count(r => r.Checked);
        UninstallButton.Content = n > 0 ? $"Gỡ các mục đã tick ({n})" : "Gỡ các mục đã tick";
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateTicked();
    private void ShowAllBox_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void SelectAllBox_Click(object sender, RoutedEventArgs e)
    {
        bool on = SelectAllBox.IsChecked == true;
        foreach (var r in ShownRows) r.Checked = on && r.CanUninstall;
        UpdateTicked();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyFilter(); }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        try
        {
            var list = ProgramList.Dedupe(await InstalledProgramsScanner.RunAsync(null, ct));
            _rows = list.Select(p => new Row(p, null, UninstallPolicy.Evaluate(p))).ToList();
            try
            {
                var store = await StoreAppsScanner.RunAsync(null, ct);
                _rows.AddRange(store.Select(a => new Row(null, a, StoreAppPolicy.Evaluate(a))));
                _rows = _rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (InvalidOperationException ex) { Logger.Error("Không đọc được ứng dụng Store: " + ex.Message); }   // vẫn dùng được danh sách chương trình thường
            // Icon chỉ cần cho các dòng sắp hiện; đọc ở luồng nền cho đỡ đơ giao diện.
            var cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
            await Task.Run(() =>
            {
                foreach (var r in _rows.Where(r => r.Program is not null && r.Verdict.Allowed))
                {
                    var path = ProgramIcon.PathFrom(r.Program!.DisplayIcon, r.Program.UninstallString);
                    if (path is null) continue;
                    if (!cache.TryGetValue(path, out var img)) cache[path] = img = LoadIcon(path);
                    r.Icon = img;
                }
            }, ct);
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
        try { if (await LoadAsync(_cts.Token)) StatusText.Text = $"Có {_rows.Count(r => r.Verdict.Allowed)} mục gỡ được. Tick ô bên cạnh tên rồi bấm \"Gỡ các mục đã tick\"."; }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Có lỗi, xem nhật ký."; Logger.Error($"Lỗi đọc danh sách chương trình: {ex}"); }
        finally { SetBusy(false); }
    }

    private bool NeedSelection(out Row row, bool programOnly = false)
    {
        row = Selected!;
        if (row is null) { StatusText.Text = "Hãy chọn một chương trình trong danh sách."; return false; }
        if (programOnly && row.Program is null)
        {
            StatusText.Text = "Bước này chỉ dùng cho chương trình thường. Ứng dụng Store: tick rồi bấm \"Gỡ các mục đã tick\" là xong, không có thư mục sót để quét.";
            return false;
        }

        return true;
    }

    private async void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var row, programOnly: true)) return;
        _activity = null;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var a = await AppActivityScanner.ScanAsync(row.Program!.InstallLocation, null, _cts.Token);
            _activity = a;
            var sb = new StringBuilder();
            sb.AppendLine(a.Any ? $"\"{row.Name}\" đang chạy:" : $"Không thấy tiến trình hay dịch vụ nào của \"{row.Name}\" đang chạy từ thư mục cài đặt.");
            if (a.Processes.Count > 0) sb.AppendLine("Tiến trình: " + string.Join(", ", a.Processes));
            if (a.Services.Count > 0) sb.AppendLine("Dịch vụ: " + string.Join(", ", a.Services));
            ResultBox.Text = sb.ToString();
            StatusText.Text = a.Any ? "Có thể bấm \"Dừng chương trình\" rồi gỡ." : "Có thể tick và gỡ.";
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        finally { SetBusy(false); }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var row, programOnly: true) || _activity is not { Any: true }) return;
        try { await RunAsync(new StopAppFix(row.Program!, _activity), refreshAfter: false); }
        catch (ArgumentException ex) { StatusText.Text = "Chưa thể dừng: " + ex.Message; }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = _rows.Where(r => r.Checked).ToList();
        if (targets.Count == 0 && Selected is { } sel) targets.Add(sel);
        if (targets.Count == 0) { StatusText.Text = "Hãy tick ô bên cạnh tên chương trình muốn gỡ."; return; }

        var blocked = targets.FirstOrDefault(r => !r.Verdict.Allowed);
        if (blocked is not null) { StatusText.Text = $"{blocked.Name}: {blocked.Verdict.Reason}"; return; }

        var items = new List<(Row Row, IFix Fix)>();
        try
        {
            foreach (var r in targets)
                items.Add((r, r.Store is not null ? (IFix)new StoreAppFix(r.Store) : new UninstallFix(r.Program!)));
        }
        catch (ArgumentException ex) { StatusText.Text = "Chưa thể gỡ: " + ex.Message; return; }

        await RunBatchAsync(items);
    }

    /// <summary>Gỡ lần lượt từng mục (mỗi trình gỡ mở cửa sổ riêng nên không chạy song song). Chỉ hỏi xác nhận một lần cho cả lô.</summary>
    private async Task RunBatchAsync(List<(Row Row, IFix Fix)> items)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }

        bool dry = DryRunBox.IsChecked == true;
        var sb = new StringBuilder();
        sb.AppendLine($"Sẽ gỡ {items.Count} mục, lần lượt từng cái:");
        foreach (var (row, fix) in items)
        {
            sb.AppendLine($"• {row.Name}{(row.Version.Length > 0 ? " " + row.Version : "")}");
            var warn = fix.Actions.FirstOrDefault(a => a.StartsWith("CẢNH BÁO", StringComparison.Ordinal));
            if (warn is not null) sb.AppendLine("   " + warn);
        }

        sb.AppendLine();
        sb.AppendLine("Chương trình thường sẽ mở trình gỡ riêng của nó, bạn xác nhận trong từng cửa sổ đó. Ứng dụng Store bị gỡ ngay.");
        if (dry) sb.AppendLine("\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)");
        sb.Append("\nĐồng ý thực hiện?");
        if (MessageBox.Show(sb.ToString(), "Gỡ các mục đã chọn", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var report = new StringBuilder();
        try
        {
            var executor = new FixExecutor(new RestorePointService());
            int i = 0;
            foreach (var (row, fix) in items)
            {
                if (_cts.IsCancellationRequested) { report.AppendLine("Đã dừng, các mục còn lại chưa gỡ."); break; }
                i++;
                StatusText.Text = $"({i}/{items.Count}) Đang gỡ {row.Name}...";
                var progress = new Progress<string>(m => StatusText.Text = $"({i}/{items.Count}) {m}");
                var result = await executor.RunAsync(fix, SettingsService.Default.BuildOptions(dry), progress, _cts.Token);
                report.AppendLine($"• {row.Name}: " + result.Outcome switch
                {
                    FixOutcome.Succeeded => "đã gỡ.",
                    FixOutcome.Simulated => "chạy thử, chưa thay đổi gì.",
                    FixOutcome.Blocked => "đã khóa: " + result.Message,
                    FixOutcome.StillPresent => "đã chạy nhưng kiểm tra lại vẫn thấy trong danh sách (có thể bạn đã bấm Hủy trong trình gỡ, hoặc cần khởi động lại máy).",
                    FixOutcome.Unverified => "đã chạy nhưng chưa kiểm tra lại được: " + result.Message,
                    FixOutcome.Cancelled => "đã dừng.",
                    _ => "không gỡ được: " + result.Message
                });
                if (result.Outcome == FixOutcome.Succeeded) row.Checked = false;
            }

            ResultBox.Text = report.ToString();
            StatusText.Text = "Xong lượt gỡ. Xem kết quả bên dưới.";
            if (!dry) await LoadAsync(_cts.Token);
            _activity = null;
        }
        catch (OperationCanceledException) { ResultBox.Text = report.ToString(); StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Có lỗi, xem nhật ký."; Logger.Error($"Lỗi gỡ phần mềm: {ex}"); }
        finally { SetBusy(false); }
    }

    private async void LeftoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var row, programOnly: true)) return;
        _leftovers = null; _leftoverFor = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        StatusText.Text = "Đang quét thư mục còn sót (chỉ đọc)...";
        try
        {
            var now = await InstalledProgramsScanner.RunAsync(null, ct);
            var found = await Task.Run(() => LeftoverScanner.Scan(row.Program!, now, null, ct), ct);
            _leftovers = found; _leftoverFor = row.Program;
            ResultBox.Text = found.Count == 0
                ? "Không thấy thư mục nào còn sót."
                : "Thư mục còn sót:\n" + string.Join("\n", found.Select(f => $"• {f.Path}  ({f.Files} file, {SizeText.Of(f.Bytes)})"));
            StatusText.Text = found.Count == 0 ? "Xong." : "Xem danh sách rồi bấm \"Chuyển tàn dư\" nếu đồng ý (không xóa, chỉ chuyển sang nơi riêng).";
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
