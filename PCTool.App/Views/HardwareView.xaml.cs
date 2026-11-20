using System.Text;
using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Framework;
using PCTool.Core.Hardware;

namespace PCTool.Views;

public partial class HardwareView : UserControl
{
    private sealed record Row(string SeverityText, string Title, string Cause);

    private string _reportText = "";

    public HardwareView()
    {
        InitializeComponent();
    }

    private async void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        ReadButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Đang đọc thông số máy...";

        try
        {
            var report = await new WmiHardwareReader().ReadAsync(CancellationToken.None);
            var findings = HardwareAnalyzer.Analyze(report, DateTimeOffset.Now);

            SpecsBox.Text = report.ToText();
            FindingsGrid.ItemsSource = findings
                .Select(f => new Row(SeverityText(f.Severity), f.Title, f.Cause)).ToList();

            var sb = new StringBuilder(report.ToText());
            sb.AppendLine().AppendLine(findings.Count == 0 ? "Sức khỏe: không phát hiện vấn đề." : "Vấn đề phát hiện:");
            foreach (var f in findings) sb.AppendLine($"- [{SeverityText(f.Severity)}] {f.Title}: {f.Cause}");
            _reportText = sb.ToString();

            StatusText.Text = findings.Count == 0
                ? "Xong: phần cứng ổn, không phát hiện vấn đề."
                : $"Xong: phát hiện {findings.Count} mục cần xem.";
            CopyButton.IsEnabled = true;
            Logger.Info($"Đọc thông số phần cứng xong ({findings.Count} mục cần xem).");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Chưa đọc được thông số: " + ex.Message + " (Máy không bị thay đổi gì. Bấm Đọc thông số để thử lại.)";
            Logger.Error("Đọc thông số phần cứng lỗi: " + ex.Message);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            ReadButton.IsEnabled = true;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reportText.Length == 0) return;
        Clipboard.SetText(_reportText);
        StatusText.Text = "Đã sao chép báo cáo vào bộ nhớ tạm.";
    }

    private static string SeverityText(Severity s) => s switch
    {
        Severity.Critical => "Nghiêm trọng",
        Severity.Warning => "Cảnh báo",
        _ => "Thông tin"
    };
}
