using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PCTool.Core.Framework;
using PCTool.Core.Pdf;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PCTool.Views;

public partial class PdfViewerView : UserControl
{
    private Windows.Data.Pdf.PdfDocument? _doc;
    private string? _path;
    private int _index;

    public PdfViewerView() => InitializeComponent();

    private static async Task<BitmapImage> RenderAsync(Windows.Data.Pdf.PdfDocument doc, int index, uint width)
    {
        using var page = doc.GetPage((uint)index);
        using var ms = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ms, new PdfPageRenderOptions { DestinationWidth = width });
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms.AsStream();
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf" };
        if (dlg.ShowDialog() != true) return;
        if (PdfOps.CheckInput(dlg.FileName, ".pdf") is { } err) { StatusText.Text = err; return; }
        try
        {
            var sf = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(dlg.FileName));
            _doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(sf);
            _path = dlg.FileName;
            PathBox.Text = _path;
            _index = 0;
            await ShowAsync();
            SaveOneButton.IsEnabled = SaveAllButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _doc = null;
            StatusText.Text = "Không mở được file (có thể có mật khẩu hoặc file hỏng): " + ex.Message;
            Logger.Warn("PDF xem: " + ex.Message);
        }
    }

    private async Task ShowAsync()
    {
        if (_doc == null) return;
        PageImage.Source = await RenderAsync(_doc, _index, 1100);
        PageText.Text = $"{_index + 1} / {_doc.PageCount}";
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < (int)_doc.PageCount - 1;
        StatusText.Text = Path.GetFileName(_path);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) { if (_index > 0) { _index--; await ShowAsync(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (_doc != null && _index < (int)_doc.PageCount - 1) { _index++; await ShowAsync(); } }

    private static void SavePng(BitmapImage img, string file)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
        enc.Save(fs);
    }

    private async void SaveOne_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _path == null) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            var file = PdfOps.UniquePath(dir, $"{Path.GetFileNameWithoutExtension(_path)}-trang{_index + 1}", ".png");
            SavePng(await RenderAsync(_doc, _index, 2000), file);
            StatusText.Text = "Đã lưu: " + file;
        }
        catch (Exception ex) { StatusText.Text = "Không lưu được: " + ex.Message; }
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _path == null) return;
        if (_doc.PageCount > 200) { StatusText.Text = "File có hơn 200 trang, hãy dùng \"Lưu trang này\" hoặc tách PDF trước."; return; }
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            var dir = parent;
            for (int i = 1; ; i++)
            {
                dir = Path.Combine(parent, Path.GetFileNameWithoutExtension(_path) + (i == 1 ? "-anh" : $"-anh ({i})"));
                if (!Directory.Exists(dir)) break;
            }
            Directory.CreateDirectory(dir);
            for (int i = 0; i < (int)_doc.PageCount; i++)
            {
                StatusText.Text = $"Đang lưu trang {i + 1}/{_doc.PageCount}...";
                SavePng(await RenderAsync(_doc, i, 2000), Path.Combine(dir, $"trang-{i + 1:000}.png"));
            }
            StatusText.Text = $"Đã lưu {_doc.PageCount} ảnh vào: {dir}";
        }
        catch (Exception ex) { StatusText.Text = "Không lưu được: " + ex.Message; }
    }
}
