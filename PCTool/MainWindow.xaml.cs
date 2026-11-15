using System.Windows;
using PCTool.Services;

namespace PCTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Logger.LineWritten += line => Dispatcher.BeginInvoke(new Action(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }));

        Logger.Info("PCTool đã khởi động. File log: " + Logger.LogFile);
    }
}
