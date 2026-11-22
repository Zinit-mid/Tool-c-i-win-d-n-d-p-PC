using System.Windows;
using System.Windows.Controls;
using PCTool.Core.Checklist;

namespace PCTool.Views;

public partial class ChecklistView : UserControl
{
    private readonly PostInstallChecklist _store = new();

    public ChecklistView() { InitializeComponent(); Build(); }

    private void Build()
    {
        ListPanel.Children.Clear();
        var done = _store.LoadDone();
        foreach (var item in PostInstallChecklist.Items)
        {
            var box = new CheckBox { IsChecked = done.Contains(item.Id), Margin = new Thickness(0, 0, 0, 10), Tag = item.Id };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Text, FontSize = 14 });
            text.Children.Add(new TextBlock { Text = item.Hint, Foreground = System.Windows.Media.Brushes.Gray, FontSize = 12 });
            box.Content = text;
            box.Click += (_, _) => { _store.Set(item.Id, box.IsChecked == true); UpdateCount(); };
            ListPanel.Children.Add(box);
        }
        UpdateCount();
    }

    private void UpdateCount() => CountText.Text = $"Đã xong {_store.LoadDone().Count}/{PostInstallChecklist.Items.Count} việc";

    private void Reset_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show("Bỏ tick tất cả các việc?", "PCTool", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _store.Reset();
        Build();
    }
}
