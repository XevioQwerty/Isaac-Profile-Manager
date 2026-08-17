using System.Windows;
using System.Windows.Threading;

namespace IsaacProfileManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Anything that escapes a command handler is a bug, but it must not take
        // the window down mid-switch without saying what happened.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message + "\n\n" + e.Exception.GetType().Name,
            "Isaac Profile Manager — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
