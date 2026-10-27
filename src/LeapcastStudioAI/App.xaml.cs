using System.Windows;
using System.Windows.Threading;

namespace LeapcastStudioAI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI bug should never take a live stream down with it: log, tell the user, keep running.
        try
        {
            var dir = Path.Combine(StateStore.Folder, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-errors.log"), $"{DateTime.Now:u} {e.Exception}{Environment.NewLine}");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "Leapcast Studio AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
