using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Folders;
using PCTool.Core.Framework;

namespace PCTool.Views;

public partial class FolderCleanupView : UserControl
{
    private CancellationTokenSource? _cts;
    private IReadOnlyList<DuplicateGroup>? _groups;
    private IReadOnlyList<MoveItem>? _plan;
    private string? _scannedFolder;

    public FolderCleanupView()
    {
        InitializeComponent();
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        FolderBox.TextChanged += (_, _) => ResetResults();
    }

    private void ResetResults()
    {
        _groups = null; _plan = null; _scannedFolder = null;
        MoveDupButton.IsEnabled = false;
        OrganizeButton.IsEnabled = false;
    }

    private void SetBusy(bool busy)
    {
        BrowseButton.IsEnabled = ScanDupButton.IsEnabled = PlanButton.IsEnabled = !busy;
        FolderBox.IsEnabled = !busy;
        MoveDupButton.IsEnabled = !busy && _groups is { Count: > 0 };
        OrganizeButton.IsEnabled = !busy && _plan is { Count: > 0 };
        CancelButton.IsEnabled = busy;
        Progress.IsIndeterminate = busy;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục cần dọn" };
        if (dlg.ShowDialog() == true) FolderBox.Text = dlg.FolderName;
    }

    private bool TryFolder(out string folder)
    {
        folder = FolderBox.Text.Trim();
        if (FolderGuard.Check(folder, out var err)) return true;
        StatusText.Text = err;
        return false;
    }

    private async void ScanDupButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryFolder(out var folder)) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        StatusText.Text = "Đang quét file trùng (chỉ đọc, thư mục lớn có thể mất vài phút)...";
        try
        {
            var groups = await Task.Run(() => DuplicateScanner.Scan(folder, ct), ct);
            _groups = groups; _scannedFolder = folder;
            var sb = new StringBuilder();
            if (groups.Count == 0) sb.AppendLine("Không thấy file trùng (từ 1 KB trở lên).");
            else
            {
                sb.AppendLine($"Có {groups.Count} nhóm file trùng, chiếm thừa {SizeText.Of(groups.Sum(g => g.WastedBytes))}.");
                sb.AppendLine("Mỗi nhóm giữ lại bản cũ nhất; các bản còn lại sẽ được chuyển đi (không xóa).");
                sb.AppendLine();
                foreach (var g in groups.Take(100))
                {
                    sb.AppendLine($"[{SizeText.Of(g.Length)} x {g.Files.Count}] GIỮ: {g.Keep.Path}");
                    foreach (var x in g.Extras) sb.AppendLine($"        chuyển: {x.Path}");
                }
                if (groups.Count > 100) sb.AppendLine($"... và {groups.Count - 100} nhóm nữa.");
            }
            ResultBox.Text = sb.ToString();
            StatusText.Text = groups.Count == 0 ? "Xong, không có file trùng." : "Xong. Xem danh sách rồi bấm \"Chuyển file trùng\" nếu đồng ý.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Quét lỗi, xem nhật ký."; Logger.Error($"Lỗi quét file trùng: {ex}"); }
        finally { SetBusy(false); }
    }

    private async void PlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryFolder(out var folder)) return;
        SetBusy(true);
        try
        {
            var plan = await Task.Run(() => OrganizePlanner.Plan(folder, DateTime.Now));
            _plan = plan; _scannedFolder = folder;
            var sb = new StringBuilder();
            if (plan.Count == 0) sb.AppendLine("Không có file nào cần sắp xếp.");
            else
            {
                sb.AppendLine($"Gợi ý chuyển {plan.Count} file nằm trực tiếp trong thư mục vào thư mục theo loại:");
                foreach (var g in plan.GroupBy(m => Path.GetFileName(m.DestFolder)).OrderBy(g => g.Key))
                {
                    sb.AppendLine();
                    sb.AppendLine($"→ {g.Key}: {g.Count()} file ({SizeText.Of(g.Sum(m => m.Length))})");
                    foreach (var m in g.Take(20)) sb.AppendLine("     " + m.FileName);
                    if (g.Count() > 20) sb.AppendLine($"     ... và {g.Count() - 20} file nữa");
                }
            }
            ResultBox.Text = sb.ToString();
            StatusText.Text = plan.Count == 0 ? "Xong." : "Xong. Xem gợi ý rồi bấm \"Sắp xếp theo loại\" nếu đồng ý.";
        }
        catch (Exception ex) { StatusText.Text = "Có lỗi, xem nhật ký."; Logger.Error($"Lỗi lập gợi ý sắp xếp: {ex}"); }
        finally { SetBusy(false); }
    }

    private async void MoveDupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_groups is null || _scannedFolder is null || !TryFolder(out var folder) || folder != _scannedFolder) { StatusText.Text = "Hãy quét lại trước."; return; }
        try { await RunAsync(new DuplicateMoveFix(folder, _groups)); }
        catch (ArgumentException ex) { StatusText.Text = "Chưa thể chuyển: " + ex.Message; }
    }

    private async void OrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _scannedFolder is null || !TryFolder(out var folder) || folder != _scannedFolder) { StatusText.Text = "Hãy xem gợi ý trước."; return; }
        try { await RunAsync(new OrganizeFix(folder, _plan)); }
        catch (ArgumentException ex) { StatusText.Text = "Chưa thể sắp xếp: " + ex.Message; }
    }

    private async Task RunAsync(IFix fix)
    {
        if (SettingsService.Default.SafeMode) { StatusText.Text = "Đã khóa: " + SettingsService.SafeModeMessage; return; }

        bool dry = DryRunBox.IsChecked == true;
        var text = "Sẽ làm:\n• " + string.Join("\n• ", fix.Actions) +
                   (dry ? "\n\n(Chế độ chạy thử: chỉ mô phỏng, máy không bị thay đổi.)" : "") +
                   "\n\nĐồng ý thực hiện?";
        if (MessageBox.Show(text, fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

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
                FixOutcome.StillPresent => "Đã chạy nhưng kiểm tra lại chưa khớp: " + result.Message,
                FixOutcome.Unverified => "Đã chạy nhưng chưa kiểm tra lại được: " + result.Message,
                FixOutcome.Cancelled => "Đã dừng.",
                _ => "Không làm được: " + result.Message
            };
            if (result.Outcome == FixOutcome.Succeeded) ResetResults();   // kết quả quét cũ không còn đúng
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Có lỗi, xem nhật ký."; Logger.Error($"Lỗi dọn thư mục: {ex}"); }
        finally { SetBusy(false); }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
