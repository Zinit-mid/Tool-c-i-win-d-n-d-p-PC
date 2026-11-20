using System.Windows;
using System.Windows.Controls;
using PCTool.Core.AppInstaller;

namespace PCTool.Views;

public partial class AddAppWindow : Window
{
    private readonly IReadOnlyCollection<string> _existingIds;

    public AppEntry? Result { get; private set; }

    public AddAppWindow(IReadOnlyCollection<string> existingIds)
    {
        InitializeComponent();
        _existingIds = existingIds;
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ValueLabel == null) return;   // sự kiện bắn ra lúc đang dựng cửa sổ
        bool winget = KindBox.SelectedIndex == 0;
        ValueLabel.Text = winget ? "ID winget (ví dụ: 7zip.7zip)" : "Link (bắt buộc https://)";
        HintText.Text = winget
            ? "Tìm ID bằng lệnh winget search tên-app, hoặc xem trên winget.run."
            : "Chỉ nhận link bắt đầu bằng https://";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var kind = KindBox.SelectedIndex switch
        {
            0 => AppKind.Winget,
            1 => AppKind.WebShortcut,
            _ => AppKind.DownloadPage
        };
        var name = NameBox.Text.Trim();
        var value = ValueBox.Text.Trim();

        var entry = new AppEntry
        {
            Id = AppEntryValidator.MakeId(name, _existingIds),
            Name = name,
            Kind = kind,
            WingetId = kind == AppKind.Winget ? value : null,
            Url = kind == AppKind.Winget ? null : value,
            Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim(),
            SelectedByDefault = false
        };

        var errors = AppEntryValidator.Validate(entry);
        if (errors.Count > 0)
        {
            ErrorText.Text = errors[0];
            return;
        }

        Result = entry;
        DialogResult = true;
    }
}
