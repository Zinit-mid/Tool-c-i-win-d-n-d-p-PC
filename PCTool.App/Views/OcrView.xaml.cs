using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;
using PCTool.Core.Ocr;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PCTool.Views;

public partial class OcrView : UserControl
{
    private CancellationTokenSource? _cts;
    private string? _path;

    public OcrView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        foreach (var m in TesseractInfo.Modes) ModeBox.Items.Add(new ComboBoxItem { Content = m.Title, Tag = m.Spec });
        ModeBox.SelectedIndex = 0;
        RefreshSetup();
    }

    private void RefreshSetup()
    {
        var exe = TesseractInfo.FindExe();
        if (exe == null)
        {
            SetupText.Text = "Tesseract: CHƯA cài. Bấm \"Cài Tesseract\" (cần Internet, cài bằng winget), rồi \"Tải dữ liệu Việt + Anh\".";
            InstallDataButton.IsEnabled = false;
            return;
        }

        bool vie = TesseractInfo.HasData(exe, "vie"), eng = TesseractInfo.HasData(exe, "eng");
        SetupText.Text = $"Tesseract: đã cài. Dữ liệu tiếng Việt: {(vie ? "có" : "CHƯA có")}, tiếng Anh: {(eng ? "có" : "CHƯA có")}." +
                         (vie && eng ? "" : " Bấm \"Tải dữ liệu Việt + Anh\".");
        InstallDataButton.IsEnabled = !(vie && eng);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Ảnh hoặc PDF|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf|Tất cả file|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (OcrInfo.CheckInput(dlg.FileName) is { } err) { StatusText.Text = err; return; }
        _path = dlg.FileName;
        PathBox.Text = _path;
        StatusText.Text = "Đã chọn file. Chọn kiểu đọc rồi bấm \"Đọc chữ\".";
    }

    private async void InstallTess_Click(object sender, RoutedEventArgs e)
    {
        if (TesseractInfo.FindExe() != null) { StatusText.Text = "Tesseract đã được cài."; RefreshSetup(); return; }
        var executor = new FixExecutor(new RestorePointService());
        var fix = new InstallAppFix(TesseractInfo.InstallEntry, new ProcessRunner(), "", _ => { });
        StatusText.Text = "Đang xử lý...";
        StatusText.Text = await FixUi.RunAsync(executor, fix, DryRunBox.IsChecked == true, m => StatusText.Text = m);
        RefreshSetup();
        if (TesseractInfo.FindExe() == null && DryRunBox.IsChecked != true)
            StatusText.Text += "\nChưa thấy Tesseract ở thư mục Program Files. Thử đóng và mở lại PCTool; nếu vẫn vậy, mở \"Nhật ký thao tác\" ở dưới và gửi tôi dòng báo lỗi.";
    }

    private async void InstallData_Click(object sender, RoutedEventArgs e)
    {
        var exe = TesseractInfo.FindExe();
        if (exe == null) { StatusText.Text = "Cần cài Tesseract trước."; return; }
        var executor = new FixExecutor(new RestorePointService());
        StatusText.Text = "Đang xử lý...";
        StatusText.Text = await FixUi.RunAsync(executor, new TesseractDataFix(exe, TesseractInfo.LangCodes), DryRunBox.IsChecked == true, m => StatusText.Text = m);
        RefreshSetup();
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        InstallTessButton.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private static async Task<List<string>> RenderPdfPagesAsync(string pdfPath, string tempDir, CancellationToken ct, Action<string> status)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        if (doc.PageCount > OcrInfo.MaxPdfPages)
            throw new InvalidOperationException($"File có {doc.PageCount} trang, nhiều hơn {OcrInfo.MaxPdfPages}. Hãy tách PDF ra từng phần rồi đọc.");

        var files = new List<string>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            status($"Đang chuẩn bị trang {i + 1}/{doc.PageCount}...");
            using var page = doc.GetPage(i);
            var png = Path.Combine(tempDir, $"trang-{i + 1:000}.png");
            using (var ms = new InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(ms, new PdfPageRenderOptions { DestinationWidth = 2200 }).AsTask(ct);
                ms.Seek(0);
                using var src = ms.AsStreamForRead();
                using var fs = new FileStream(png, FileMode.CreateNew, FileAccess.Write);
                await src.CopyToAsync(fs, ct);
            }

            files.Add(png);
        }

        return files;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (OcrInfo.CheckInput(_path) is { } err) { StatusText.Text = err; return; }
        var exe = TesseractInfo.FindExe();
        if (exe == null) { StatusText.Text = "Chưa cài Tesseract. Bấm \"Cài Tesseract\" trước."; return; }
        var spec = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "vie+eng";
        var missing = spec.Split('+').Where(c => !TesseractInfo.HasData(exe, c)).ToList();
        if (missing.Count > 0)
        {
            StatusText.Text = "Thiếu dữ liệu ngôn ngữ: " + string.Join(", ", missing) + ". Bấm \"Tải dữ liệu Việt + Anh\".";
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        CopyButton.IsEnabled = SaveButton.IsEnabled = false;
        ResultBox.Clear();
        string? tempDir = null;
        try
        {
            var full = Path.GetFullPath(_path!);
            List<string> images;
            if (OcrInfo.IsPdf(full))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "PCTool-ocr-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                images = await RenderPdfPagesAsync(full, tempDir, ct, m => StatusText.Text = m);
            }
            else images = new List<string> { full };

            var runner = new ProcessRunner();
            var pages = new List<string>();
            for (int i = 0; i < images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                StatusText.Text = $"Đang đọc {(images.Count > 1 ? $"trang {i + 1}/{images.Count}" : "ảnh")}...";
                var r = await TesseractInfo.RecognizeAsync(runner, exe, images[i], spec, ct);
                if (!r.Ok) { StatusText.Text = "Tesseract báo lỗi: " + r.Error; Logger.Warn("OCR lỗi: " + r.Error); return; }
                pages.Add(r.Text);
            }

            var text = OcrInfo.JoinPages(pages);
            ResultBox.Text = text;
            bool empty = string.IsNullOrWhiteSpace(text);
            CopyButton.IsEnabled = SaveButton.IsEnabled = !empty;
            StatusText.Text = empty
                ? "Không đọc ra chữ nào. Thử ảnh nét hơn, đúng chiều, hoặc đổi kiểu đọc."
                : $"Xong: đọc {pages.Count} trang. Hãy đọc lại, đặc biệt dấu tiếng Việt, số và tên riêng vì có thể sai.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex)
        {
            StatusText.Text = "Không đọc được (file hỏng, có mật khẩu hoặc định dạng lạ): " + ex.Message;
            Logger.Warn("OCR lỗi: " + ex.Message);
        }
        finally
        {
            if (tempDir != null) { try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            SetBusy(false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResultBox.Text); StatusText.Text = "Đã sao chép."; }
        catch (Exception ex) { StatusText.Text = "Không sao chép được: " + ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        try
        {
            var target = OcrInfo.OutputPath(_path);
            File.WriteAllText(target, ResultBox.Text, new UTF8Encoding(true));
            StatusText.Text = "Đã lưu: " + target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Không lưu được: " + ex.Message;
        }
    }
}
