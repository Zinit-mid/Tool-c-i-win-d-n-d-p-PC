using System.Text;
using PCTool.Core.Pdf;

namespace PCTool.Core.Ocr;

/// <summary>Quy tắc chung cho chức năng đọc chữ từ ảnh/PDF (đọc chữ thật bằng Tesseract, xem Tesseract.cs).</summary>
public static class OcrInfo
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
    public static string[] InputExtensions { get; } = ImageExtensions.Append(".pdf").ToArray();

    /// <summary>File PDF nhiều hơn số trang này thì từ chối (OCR chậm); bạn có thể tách PDF trước.</summary>
    public const int MaxPdfPages = 100;

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static string? CheckInput(string? path) => PdfOps.CheckInput(path, InputExtensions);

    /// <summary>File chữ nằm cạnh file gốc, không ghi đè file nào: "ten-chu.txt", "ten-chu (2).txt"...</summary>
    public static string OutputPath(string input)
    {
        var full = Path.GetFullPath(input);
        return PdfOps.UniquePath(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + "-chu", ".txt");
    }

    /// <summary>Ghép chữ của từng trang. Một trang thì giữ nguyên; nhiều trang thì có dòng phân cách.</summary>
    public static string JoinPages(IReadOnlyList<string> pages)
    {
        if (pages.Count == 0) return "";
        if (pages.Count == 1) return pages[0].Trim();
        var sb = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) sb.Append("\r\n\r\n");
            sb.Append($"--- Trang {i + 1} ---\r\n").Append(pages[i].Trim());
        }

        return sb.ToString();
    }
}
