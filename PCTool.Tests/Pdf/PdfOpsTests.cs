using PCTool.Core.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PCTool.Tests.Pdf;

public class PdfOpsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pct-pdf-" + Guid.NewGuid().ToString("N"));
    public PdfOpsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakePdf(string name, int pages)
    {
        var p = Path.Combine(_dir, name);
        using var d = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < pages; i++) d.AddPage();
        d.Save(p);
        return p;
    }

    private static int Pages(string path) => PdfOps.PageCount(path) ?? -1;

    [Fact]
    public void PageSpec_Valid_And_Invalid()
    {
        Assert.Equal(new[] { 0, 1, 2, 4, 5 }, PageSpec.Parse("1-3,5-", 6).Pages);
        Assert.Equal(5, PageSpec.Parse("", 5).Pages!.Count);
        foreach (var bad in new[] { "0", "3-1", "7", "a", "1-2-3", "1,,x" })
            Assert.NotNull(PageSpec.Parse(bad, 6).Error);
        Assert.NotNull(PageSpec.Parse("1", 0).Error);
    }

    [Fact]
    public void Merge_CountsPages_NeverTouchesOriginals()
    {
        var a = MakePdf("a.pdf", 2); var b = MakePdf("b.pdf", 3);
        var sizeA = new FileInfo(a).Length;
        var r = PdfOps.Merge(new[] { a, b }, _dir, "gop");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(5, Pages(r.Outputs![0]));
        Assert.Equal(sizeA, new FileInfo(a).Length);
        var r2 = PdfOps.Merge(new[] { a, b }, _dir, "gop");
        Assert.NotEqual(r.Outputs[0], r2.Outputs![0]);
    }

    [Fact]
    public void Merge_Rejects_FewInputs_BadExt_Missing()
    {
        var a = MakePdf("a.pdf", 1);
        Assert.False(PdfOps.Merge(new[] { a }, _dir, "x").Ok);
        var txt = Path.Combine(_dir, "t.txt"); File.WriteAllText(txt, "x");
        Assert.False(PdfOps.Merge(new[] { a, txt }, _dir, "x").Ok);
        Assert.False(PdfOps.Merge(new[] { a, Path.Combine(_dir, "none.pdf") }, _dir, "x").Ok);
        Assert.False(PdfOps.Merge(new[] { a, a }, Path.Combine(_dir, "khong-co"), "x").Ok);
    }

    [Fact]
    public void Merge_CorruptFile_Fails_Cleanly()
    {
        var a = MakePdf("a.pdf", 1);
        var bad = Path.Combine(_dir, "bad.pdf"); File.WriteAllText(bad, "khong phai pdf");
        var r = PdfOps.Merge(new[] { a, bad }, _dir, "x");
        Assert.False(r.Ok);
        Assert.Empty(Directory.GetFiles(_dir, "x*.pdf"));
    }

    [Fact]
    public void Extract_And_SplitEach()
    {
        var a = MakePdf("a.pdf", 5);
        var r = PdfOps.ExtractPages(a, "2-3,5", _dir, "lay");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, Pages(r.Outputs![0]));
        Assert.False(PdfOps.ExtractPages(a, "9", _dir, "lay").Ok);

        var s = PdfOps.SplitEach(a, _dir, "tach");
        Assert.True(s.Ok, s.Message);
        Assert.Equal(5, s.Outputs!.Count);
        Assert.All(s.Outputs, o => Assert.Equal(1, Pages(o)));
    }

    [Fact]
    public void Rotate_SelectedPagesOnly()
    {
        var a = MakePdf("a.pdf", 3);
        var r = PdfOps.Rotate(a, 90, "2", _dir, "xoay");
        Assert.True(r.Ok, r.Message);
        using var d = PdfReader.Open(r.Outputs![0], PdfDocumentOpenMode.Import);
        Assert.Equal(0, d.Pages[0].Rotate);
        Assert.Equal(90, d.Pages[1].Rotate);
        Assert.Equal(0, d.Pages[2].Rotate);
        Assert.False(PdfOps.Rotate(a, 45, "", _dir, "x").Ok);
    }

    [Fact]
    public void ImagesToPdf_Png()
    {
        var png = Path.Combine(_dir, "a.png");
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var r = PdfOps.ImagesToPdf(new[] { png, png }, _dir, "anh");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, Pages(r.Outputs![0]));
        var bad = Path.Combine(_dir, "a.gif"); File.WriteAllText(bad, "x");
        Assert.False(PdfOps.ImagesToPdf(new[] { bad }, _dir, "x").Ok);
    }

    [Fact]
    public void OutputDir_SystemFolders_Rejected()
    {
        Assert.NotNull(PdfOps.CheckOutputDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.NotNull(PdfOps.CheckOutputDir(@"\\server\share"));
        Assert.Null(PdfOps.CheckOutputDir(_dir));
    }
}
