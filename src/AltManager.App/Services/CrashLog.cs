using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AltManager.App.Services;

/// <summary>
/// Writes unhandled exceptions to a log file so crashes can be diagnosed after the fact.
/// Logs live in %LocalAppData%\AltManager\logs. UI-thread exceptions are logged and swallowed so a
/// single bad handler doesn't take the whole app down; everything else is logged on its way out.
/// </summary>
public static class CrashLog
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltManager", "logs");

    /// <summary>Hooks the global exception handlers. Call once, as early as possible in startup.</summary>
    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write("Dispatcher", e.Exception);
            e.Handled = true; // keep the app alive; the error is on disk
            MessageBox.Show(
                "Something went wrong, but the app is still running. Details were saved to:\n" + Folder,
                "Alt Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("Task", e.Exception); e.SetObserved(); };
    }

    /// <summary>Appends an entry to today's log. Never throws.</summary>
    public static void Write(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"crash-{DateTime.Now:yyyy-MM-dd}.log");
            var entry = $"""

                ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {source} =====
                {ex}

                """;
            File.AppendAllText(file, entry);
            Prune();
        }
        catch { /* logging must never crash the app */ }
    }

    /// <summary>Keeps only the 10 most recent log files.</summary>
    private static void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(Folder).GetFiles("crash-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
                old.Delete();
        }
        catch { }
    }
}
