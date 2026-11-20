using System.Windows;
using PCTool.Core.Framework;

namespace PCTool;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Logger.Error("Lỗi không xử lý: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "PCTool - Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
