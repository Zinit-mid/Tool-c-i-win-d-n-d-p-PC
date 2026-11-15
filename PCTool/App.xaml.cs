using System.Windows;

namespace PCTool;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Services.Logger.Error("Lỗi không xử lý: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "PCTool - Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
