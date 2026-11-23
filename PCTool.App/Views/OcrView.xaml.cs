using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCTool.Core.Framework;
using PCTool.Core.Ocr;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PCTool.Views;

public partial class OcrView : UserControl
{
    private CancellationTokenSource? _cts;
    private string? _path;
    private string? _lastText;

    public OcrView()
    {
        InitializeComponent();
        DryRunBox.IsChecked = SettingsService.Default.DryRunDefault;
        RefreshLanguages();
    }

    private static OcrEngine? CreateEngine(string tag)
    {
        try
        {
            var lang = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
            return lang == null ? null : OcrEngine.TryCreateFromLanguage(lang);
        }
        catch (Exception ex)
        {
            Logger.Warn("OCR: không tạo được bộ đọc " + tag + ": " + ex.Message);
            return null;
        }
    }

    private void RefreshLanguages()
    {
        var keep = (LangBox.SelectedItem as ComboBoxItem)?.Tag as string;
        LangBox.Items.Clear();
        foreach (var l in OcrInfo.Languages)
        {
            bool ok = CreateEngine(l.Tag) != null;
            LangBox.Items.Add(new ComboBoxItem { Content = l.Title + (ok ? "" : " (chưa cài gói)"), Tag = l.Tag });
        }

        int idx = LangBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == keep);
        if (idx < 0) idx = LangBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => !((string)i.Content).Contains("chưa cài"));
        LangBox.SelectedIndex = idx < 0 ? 0 : idx;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Ảnh hoặc PDF|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf|Tất cả file|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        if (OcrInfo.CheckInput(dlg.FileName) is { } err) { StatusText.Text = err; return; }
        _path = dlg.FileName;
        PathBox.Text = _path;
        StatusText.Text = "Đã chọn file. Chọn ngôn ngữ rồi bấm \"Đọc chữ\".";
    }

    private async void InstallLang_Click(object sender, RoutedEventArgs e)
    {
        if ((LangBox.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;
        if (CreateEngine(tag) != null) { StatusText.Text = "Gói ngôn ngữ này đã có sẵn."; return; }
        var executor = new FixExecutor(new RestorePointService());
        StatusText.Text = "Đang xử lý...";
        StatusText.Text = await FixUi.RunAsync(executor, new OcrLanguageInstallFix(tag), DryRunBox.IsChecked == true, m => StatusText.Text = m);
        RefreshLanguages();
    }

    private static async Task<SoftwareBitmap> LoadBitmapAsync(IRandomAccessStream stream)
    {
        var dec = await BitmapDecoder.CreateAsync(stream);
        uint w = dec.PixelWidth, h = dec.PixelHeight, max = OcrEngine.MaxImageDimension;
        var tr = new BitmapTransform();
        if (w > max || h > max)
        {
            double f = (double)max / Math.Max(w, h);
            tr.ScaledWidth = (uint)Math.Max(1, w * f);
            tr.ScaledHeight = (uint)Math.Max(1, h * f);
        }

        return await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    }

    private static async Task<string> RecognizeAsync(OcrEngine engine, SoftwareBitmap bmp, CancellationToken ct)
    {
        var res = await engine.RecognizeAsync(bmp).AsTask(ct);
        return string.Join("\r\n", res.Lines.Select(l => l.Text));
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        InstallLangButton.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (OcrInfo.CheckInput(_path) is { } err) { StatusText.Text = err; return; }
        if ((LangBox.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;
        var engine = CreateEngine(tag);
        if (engine == null)
        {
            StatusText.Text = "Máy chưa có gói đọc chữ cho ngôn ngữ này. Bấm \"Cài gói ngôn ngữ này\" hoặc chọn ngôn ngữ khác.";
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        CopyButton.IsEnabled = SaveButton.IsEnabled = false;
        ResultBox.Clear();
        _lastText = null;
        try
        {
            var full = Path.GetFullPath(_path!);
            var file = await StorageFile.GetFileFromPathAsync(full);
            var pages = new List<string>();

            if (OcrInfo.IsPdf(full))
            {
                var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
                if (doc.PageCount > OcrInfo.MaxPdfPages)
                {
                    StatusText.Text = $"File có {doc.PageCount} trang, nhiều hơn {OcrInfo.MaxPdfPages}. Hãy tách PDF ra từng phần rồi đọc.";
                    return;
                }

                for (uint i = 0; i < doc.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusText.Text = $"Đang đọc trang {i + 1}/{doc.PageCount}...";
                    using var page = doc.GetPage(i);
                    using var ms = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(ms, new PdfPageRenderOptions { DestinationWidth = 2200 }).AsTask(ct);
                    ms.Seek(0);
                    using var bmp = await LoadBitmapAsync(ms);
                    pages.Add(await RecognizeAsync(engine, bmp, ct));
                }
            }
            else
            {
                StatusText.Text = "Đang đọc ảnh...";
                using var stream = await file.OpenReadAsync();
                using var bmp = await LoadBitmapAsync(stream);
                pages.Add(await RecognizeAsync(engine, bmp, ct));
            }

            _lastText = OcrInfo.JoinPages(pages);
            ResultBox.Text = _lastText;
            bool empty = string.IsNullOrWhiteSpace(_lastText);
            CopyButton.IsEnabled = SaveButton.IsEnabled = !empty;
            StatusText.Text = empty
                ? "Không đọc ra chữ nào. Thử ảnh nét hơn, đúng chiều, hoặc đổi ngôn ngữ."
                : $"Xong: đọc {pages.Count} trang. Hãy đọc lại, đặc biệt dấu tiếng Việt, số và tên riêng vì có thể sai.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex)
        {
            StatusText.Text = "Không đọc được (file hỏng, có mật khẩu hoặc định dạng lạ): " + ex.Message;
            Logger.Warn("OCR lỗi: " + ex.Message);
        }
        finally { SetBusy(false); }
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
