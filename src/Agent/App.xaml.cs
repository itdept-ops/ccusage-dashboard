using System.IO;
using System.Windows;
using System.Windows.Threading;
using UsageIq.Agent.Services;
using UsageIq.Agent.Views;

namespace UsageIq.Agent;

/// <summary>
/// App entry point. Uses <c>OnExplicitShutdown</c> so the app keeps living in the tray after the main
/// window is closed (closing only hides it). A single-instance mutex prevents a second copy fighting
/// over the tray / state file. The <c>--tray</c> argument (used by the run-at-logon entry) starts the
/// app hidden; a <c>StartMinimized</c> config flag does the same.
///
/// A distributable desktop app must never die silently: every unhandled exception (startup, UI dispatcher,
/// background task, or AppDomain) is written to <c>~/.usage-iq/agent-log.txt</c> and shown in a dialog, so a
/// field crash is diagnosable instead of a window that just vanishes. Dispatcher faults are marked handled
/// so a transient UI hiccup degrades gracefully rather than tearing the process down.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstance;

    private AgentController? _controller;
    private TrayIcon? _tray;
    private MainWindow? _main;

    /// <summary>The crash/diagnostics log path, shared with the rest of the agent's state under ~/.usage-iq.</summary>
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".usage-iq", "agent-log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Wire crash capture FIRST, before anything can throw.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            LogCrash("AppDomain", ev.ExceptionObject as Exception, fatal: ev.IsTerminating);
        DispatcherUnhandledException += OnDispatcherException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogCrash("Task", ev.Exception, fatal: false);
            ev.SetObserved();
        };

        try
        {
            base.OnStartup(e);
            StartUp(e);
        }
        catch (Exception ex)
        {
            // A failure here means the tray/window never came up — show it and exit cleanly.
            LogCrash("Startup", ex, fatal: true);
            Shutdown(1);
        }
    }

    /// <summary>The real startup sequence (wrapped by <see cref="OnStartup"/>'s crash guard).</summary>
    private void StartUp(StartupEventArgs e)
    {
        // ---- single instance ----
        _singleInstance = new Mutex(initiallyOwned: true, "UsageIqAgent.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Usage IQ Agent is already running (look in the system tray).",
                "Usage IQ Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var args = e.Args ?? Array.Empty<string>();
        var trayFlag = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        // ---- core: controller + window + tray ----
        _controller = new AgentController();
        _main = new MainWindow(_controller);
        _tray = new TrayIcon(_controller, _main, ShutdownApp);

        // Load config and (if it validates) start watching automatically.
        var opt = _controller.Reload();
        var startMinimized = trayFlag || opt.StartMinimized;

        if (opt.Validate() is null)
            _controller.Start();

        if (startMinimized)
            _main.HideToTray(); // stay in the tray; user opens from the menu
        else
            _main.ShowFromTray();

        // Reflect initial state in the tray tooltip/menu.
        _tray.Refresh();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs ev)
    {
        // Keep the process alive on a UI-thread fault (log it, show it once), rather than vanishing.
        LogCrash("Dispatcher", ev.Exception, fatal: false);
        ev.Handled = true;
    }

    /// <summary>Append an exception to the agent log and surface it to the user. Never throws.</summary>
    private static void LogCrash(string source, Exception? ex, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{(fatal ? " (fatal)" : "")}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never itself crash */ }

        try
        {
            MessageBox.Show(
                $"Usage IQ Agent hit an error{(fatal ? " and has to close" : "")}.\n\n" +
                $"{ex?.GetType().Name}: {ex?.Message}\n\n" +
                $"Full details were written to:\n{LogPath}",
                "Usage IQ Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* a dialog may be impossible very early; the log still has it */ }
    }

    /// <summary>Clean teardown: stop the engine, remove the tray icon, then exit the process.</summary>
    public void ShutdownApp()
    {
        try { _main?.AllowClose(); } catch { /* ignore */ }
        try { _controller?.Stop(); } catch { /* ignore */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _controller?.Dispose(); } catch { /* ignore */ }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { /* not owned */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
