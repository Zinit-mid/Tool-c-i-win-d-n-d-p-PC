using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCTool.Core.Framework;
using PCTool.Core.Pdf;

namespace PCTool.Views;

public partial class PdfToolsView : UserControl
{
    public PdfToolsView() => InitializeComponent();

    private string OutDirFor(string? firstFile)
    {
        var o = OutBox.Text.Trim();
        if (o.Length > 0) return o;
        return string.IsNullOrEmpty(firstFile) ? "" : (Path.GetDirectoryName(Path.GetFullPath(firstFile)) ?? "");
    }

    private async Task RunAsync(Func<PdfResult> work)
    {
        IsEnabled = false; Progress.IsIndeterminate = true;
        try
        {
            var r = await Task.Run(() => { try { return work(); } catch (Exception ex) { return new PdfResult(false, "Lỗi: " + ex.Message); } });
            ResultBox.Text = (r.Ok ? "" : "KHÔNG THÀNH CÔNG: ") + r.Message + (r.Outputs is { Count: > 0 } o ? Environment.NewLine + string.Join(Environment.NewLine, o) : "");
            Logger.Info("PDF: " + r.Message);
            if (r.Ok && r.Outputs is { Count: > 0 } outs)
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outs[0]}\"") { UseShellExecute = true }); } catch { }
        }
        finally { IsEnabled = true; Progress.IsIndeterminate = false; }
    }

    private void BrowseOut_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục lưu kết quả" };
        if (dlg.ShowDialog() == true) OutBox.Text = dlg.FolderName;
    }

    private static string[]? PickFiles(string filter, bool multi)
    {
        var dlg = new OpenFileDialog { Filter = filter, Multiselect = multi };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    private static void Move(ListBox l, int d)
    {
        int i = l.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= l.Items.Count) return;
        var it = l.Items[i]; l.Items.RemoveAt(i); l.Items.Insert(j, it); l.SelectedIndex = j;
    }

    private static void Remove(ListBox l) { if (l.SelectedIndex >= 0) l.Items.RemoveAt(l.SelectedIndex); }

    private void MergeAdd_Click(object s, RoutedEventArgs e) { if (PickFiles("PDF (*.pdf)|*.pdf", true) is { } f) foreach (var x in f) MergeList.Items.Add(x); }
    private void MergeUp_Click(object s, RoutedEventArgs e) => Move(MergeList, -1);
    private void MergeDown_Click(object s, RoutedEventArgs e) => Move(MergeList, 1);
    private void MergeRemove_Click(object s, RoutedEventArgs e) => Remove(MergeList);

    private async void Merge_Click(object s, RoutedEventArgs e)
    {
        var files = MergeList.Items.Cast<string>().ToList();
        var dir = OutDirFor(files.FirstOrDefault());
        await RunAsync(() => PdfOps.Merge(files, dir, "PDF-gop"));
    }

    private void SrcBrowse_Click(object s, RoutedEventArgs e) { if (PickFiles("PDF (*.pdf)|*.pdf", false) is { } f) SrcBox.Text = f[0]; }

    private async void Extract_Click(object s, RoutedEventArgs e)
    {
        string src = SrcBox.Text.Trim(), pages = PagesBox.Text, dir = OutDirFor(src);
        await RunAsync(() => PdfOps.ExtractPages(src, pages, dir, Path.GetFileNameWithoutExtension(src) + "-trich"));
    }

    private async void SplitEach_Click(object s, RoutedEventArgs e)
    {
        string src = SrcBox.Text.Trim(), dir = OutDirFor(src);
        await RunAsync(() => PdfOps.SplitEach(src, dir, Path.GetFileNameWithoutExtension(src)));
    }

    private async void Rotate_Click(object s, RoutedEventArgs e)
    {
        string src = SrcBox.Text.Trim(), pages = PagesBox.Text, dir = OutDirFor(src);
        int deg = (DegBox.SelectedIndex + 1) * 90;
        await RunAsync(() => PdfOps.Rotate(src, deg, pages, dir, Path.GetFileNameWithoutExtension(src) + "-xoay"));
    }

    private void ImgAdd_Click(object s, RoutedEventArgs e) { if (PickFiles("Ảnh (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp", true) is { } f) foreach (var x in f) ImgList.Items.Add(x); }
    private void ImgUp_Click(object s, RoutedEventArgs e) => Move(ImgList, -1);
    private void ImgDown_Click(object s, RoutedEventArgs e) => Move(ImgList, 1);
    private void ImgRemove_Click(object s, RoutedEventArgs e) => Remove(ImgList);

    private async void ImgToPdf_Click(object s, RoutedEventArgs e)
    {
        var files = ImgList.Items.Cast<string>().ToList();
        var dir = OutDirFor(files.FirstOrDefault());
        await RunAsync(() => PdfOps.ImagesToPdf(files, dir, "Anh-thanh-PDF"));
    }
}
