using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ReTex.App;

public partial class App : Application
{
    public App()
    {
        // Safety net: a WPF [RelayCommand] async method rethrows unhandled exceptions on the UI
        // dispatcher, which by default terminates the process. Catch them here so a failure (e.g. a
        // pack error) shows a dialog and is logged instead of silently crashing the app.
        DispatcherUnhandledException += (_, e) => { Report("UI", e.Exception); e.Handled = true; };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Report("Background", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report("Fatal", ex);
        };
    }

    /// <summary>Logs an unexpected exception to %AppData%\ReTex\crash.log and shows a non-fatal dialog.</summary>
    private static void Report(string kind, Exception ex)
    {
        string logPath = "";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReTex");
            Directory.CreateDirectory(dir);
            logPath = Path.Combine(dir, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:u}] {kind}\n{ex}\n\n");
        }
        catch { /* logging is best-effort */ }

        try
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{ex.Message}\n\nThe app will keep running." +
                (logPath.Length > 0 ? $"\n\nDetails were written to:\n{logPath}" : ""),
                "ReTex - error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* nothing more we can do */ }
    }
}
