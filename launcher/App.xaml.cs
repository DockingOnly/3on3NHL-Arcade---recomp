using System.Windows;
using System.Windows.Threading;

namespace Nhl3on3Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "launcher-error.log"), $"{DateTime.Now:u} {e.Exception}\n\n");
        }
        catch (Exception) { }
        MessageBox.Show(e.Exception.Message, "3 on 3 NHL Arcade launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
