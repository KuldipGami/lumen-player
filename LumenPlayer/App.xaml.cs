using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace LumenPlayer;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        Core.Initialize();

        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Lumen Player", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
