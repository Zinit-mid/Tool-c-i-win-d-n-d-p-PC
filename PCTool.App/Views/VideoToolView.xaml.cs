using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;
using PCTool.Core.Video;

namespace PCTool.Views;

public partial class VideoToolView : UserControl
{
    private string? _cutFile;
    private string? _lastOutput;
    private bool _busy;

    public VideoToolView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        RefreshSetup();
    }

    private const string VideoFilter = "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mpg;*.mpeg|Tất cả file|*.*";

    private void RefreshSetup()
    {
        var exe = VideoInfo.FindFfmpeg();
        SetupText.Text = exe == null
            ? "FFmpeg: CHƯA cài. Bấm \"Cài FFmpeg\" (cần Internet, cài bằng winget). Nếu đã cài mà vẫn báo chưa có, đóng và mở lại PCTool."
            : "FFmpeg: đã cài (" + exe + ").";
        InstallButton.IsEnabled = exe == null && !_busy;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (VideoInfo.FindFfmpeg() != null) { RefreshSetup(); return; }
        var executor = new FixExecutor(new RestorePointService());
        var fix = new InstallAppFix(VideoInfo.InstallEntry, new ProcessRunner(), "", _ => { });
        StatusText.Text = "Đang xử lý...";
        await RunBusy(async () =>
        {
            StatusText.Text = await FixUi.RunAsync(executor, fix, DryRunBox.IsChecked == true, m => StatusText.Text = m);
        });
        RefreshSetup();
        if (VideoInfo.FindFfmpeg() == null && DryRunBox.IsChecked != true)
            StatusText.Text += "\nChưa thấy FFmpeg. Đóng và mở lại PCTool rồi thử lại; nếu vẫn vậy, mở \"Nhật ký thao tác\" và gửi tôi dòng báo lỗi.";
    }

    private async Task RunBusy(Func<Task> work)
    {
        _busy = true;
        Progress.IsIndeterminate = true;
        try { await work(); }
        finally { _busy = false; Progress.IsIndeterminate = false; }
    }

    private void CutBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = VideoFilter };
        if (dlg.ShowDialog() != true) return;
        if (VideoInfo.CheckVideo(dlg.FileName) is { } err) { StatusText.Text = err; return; }
        _cutFile = dlg.FileName;
        CutPath.Text = _cutFile;
        StatusText.Text = "Đã chọn video. Nhập điểm bắt đầu và kết thúc rồi bấm Cắt.";
    }

    private async Task RunFixAsync(IFix fix, string outputPath)
    {
        var executor = new FixExecutor(new RestorePointService());
        await RunBusy(async () =>
        {
            var msg = await FixUi.RunAsync(executor, fix, DryRunBox.IsChecked == true, m => StatusText.Text = m);
            StatusText.Text = msg;
            if (msg.StartsWith("Xong:", StringComparison.Ordinal)) { _lastOutput = outputPath; RevealButton.IsEnabled = true; }
        });
    }

    private async void Cut_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var exe = VideoInfo.FindFfmpeg();
        if (exe == null) { StatusText.Text = "Chưa cài FFmpeg. Bấm \"Cài FFmpeg\" trước."; return; }
        VideoCutFix fix;
        try { fix = new VideoCutFix(exe, _cutFile ?? "", StartBox.Text, EndBox.Text, CutFastBox.IsChecked == true); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        await RunFixAsync(fix, fix.OutputPath);
    }

    private void MergeAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = VideoFilter, Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
        {
            if (VideoInfo.CheckVideo(f) is { } err) { StatusText.Text = err; continue; }
            if (MergeList.Items.Count >= VideoInfo.MaxMergeFiles) { StatusText.Text = $"Tối đa {VideoInfo.MaxMergeFiles} file."; break; }
            MergeList.Items.Add(f);
        }
    }

    private void MergeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MergeList.SelectedIndex >= 0) MergeList.Items.RemoveAt(MergeList.SelectedIndex);
    }

    private void Move(int delta)
    {
        int i = MergeList.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= MergeList.Items.Count) return;
        var item = MergeList.Items[i];
        MergeList.Items.RemoveAt(i);
        MergeList.Items.Insert(j, item);
        MergeList.SelectedIndex = j;
    }

    private void MergeUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MergeDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var exe = VideoInfo.FindFfmpeg();
        if (exe == null) { StatusText.Text = "Chưa cài FFmpeg. Bấm \"Cài FFmpeg\" trước."; return; }
        VideoMergeFix fix;
        try { fix = new VideoMergeFix(exe, MergeList.Items.Cast<string>().ToList(), MergeFastBox.IsChecked == true); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        await RunFixAsync(fix, fix.OutputPath);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput == null || !File.Exists(_lastOutput)) { StatusText.Text = "Chưa có file kết quả để mở."; return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _lastOutput + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Không mở được thư mục: " + ex.Message; }
    }
}
