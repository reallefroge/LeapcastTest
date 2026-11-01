using System.Windows;
using System.Windows.Threading;

namespace LeapcastStudioAI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // hidden helper windows (browser sources) must not keep the app alive after the studio closes
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnUnhandled;
        // The studio moves every frame between its capture helpers, the compositor and the outputs: like OBS's
        // "Above normal" process priority, this keeps those hand-offs on time while a game uses the whole CPU.
        if (Environment.GetEnvironmentVariable("LEAPCAST_NO_PRIORITY_BOOST") != "1")
        {
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }
        }
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
