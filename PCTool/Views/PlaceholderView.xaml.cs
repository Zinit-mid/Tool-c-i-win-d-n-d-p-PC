using System.Windows;
using System.Windows.Controls;

namespace PCTool.Views;

public partial class PlaceholderView : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PlaceholderView),
            new PropertyMetadata("", (d, e) => ((PlaceholderView)d).TitleText.Text = (string)e.NewValue));

    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(nameof(Detail), typeof(string), typeof(PlaceholderView),
            new PropertyMetadata("", (d, e) => ((PlaceholderView)d).DetailText.Text = (string)e.NewValue));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    public PlaceholderView()
    {
        InitializeComponent();
    }
}
