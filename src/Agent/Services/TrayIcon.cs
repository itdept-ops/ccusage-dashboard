using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;
using UsageIq.Agent.Views;

namespace UsageIq.Agent.Services;

/// <summary>
/// The system-tray presence: a <see cref="WinForms.NotifyIcon"/> (the only WinForms UI in the app) plus a
/// native <b>WPF</b> context menu — Open, Quick add, Pause/Resume, Sync now, Settings, Quit — and a status
/// tooltip that tracks the agent state (e.g. "Usage IQ — synced 14.2M tokens").
///
/// <para>The menu is deliberately a WPF <see cref="ContextMenu"/>, NOT a WinForms <c>ContextMenuStrip</c>:
/// a WinForms ToolStrip hosted in a pure-WPF process spins up a nested modal message loop that the WPF
/// dispatcher never pumps, so the tray menu would pop up and then <i>freeze</i>. A WPF ContextMenu rides
/// the WPF Popup/Dispatcher and behaves. All controller events arrive on a background thread, so menu and
/// tooltip updates are marshaled onto the WPF Dispatcher before touching UI.</para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly AgentController _controller;
    private readonly MainWindow _main;
    private readonly Action _quit;

    private readonly WinForms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _pauseResume;
    private readonly MenuItem _syncNow;

    /// <summary>Guards against stacking two Quick-Add windows when the menu item is clicked repeatedly.</summary>
    private QuickAddWindow? _quickAdd;

    public TrayIcon(AgentController controller, MainWindow main, Action quit)
    {
        _controller = controller;
        _main = main;
        _quit = quit;

        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        var open = new MenuItem { Header = "Open", FontWeight = FontWeights.Bold };
        open.Click += (_, _) => _main.ShowFromTray();

        var quickAdd = new MenuItem { Header = "Quick add…" };
        quickAdd.Click += (_, _) => OpenQuickAdd();

        _pauseResume = new MenuItem { Header = "Pause" };
        _pauseResume.Click += (_, _) => TogglePause();

        _syncNow = new MenuItem { Header = "Sync now" };
        _syncNow.Click += (_, _) => _controller.SyncNow();

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => _main.OpenSettings();

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => _quit();

        _menu.Items.Add(open);
        _menu.Items.Add(quickAdd);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_pauseResume);
        _menu.Items.Add(_syncNow);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(settings);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(quitItem);

        _icon = new WinForms.NotifyIcon
        {
            Icon = BrandIcon.Load(),
            Text = "Usage IQ",
            Visible = true,
        };
        // Right-click shows the WPF menu; left double-click opens the window (the familiar tray convention).
        _icon.MouseUp += OnIconMouseUp;
        _icon.DoubleClick += (_, _) => OnUi(_main.ShowFromTray);

        _controller.StatusChanged += OnStatusChanged;
    }

    /// <summary>
    /// Show the WPF context menu at the cursor on right-click. The NotifyIcon's mouse events arrive on the
    /// UI thread, but we marshal defensively. We bring a foreground window up first (the classic tray trick)
    /// so the menu's popup takes mouse capture and dismisses on click-away instead of lingering.
    /// </summary>
    private void OnIconMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Right) return;
        OnUi(() =>
        {
            try { SetForegroundWindow(new WindowInteropHelper(_main).EnsureHandle()); } catch { /* best effort */ }
            _menu.IsOpen = true;
        });
    }

    private void TogglePause()
    {
        // Pause() blocks briefly while the watch loop unwinds (it Waits on the loop task). That MUST NOT run
        // on the UI thread or the tray/menu freezes — run it off-thread; status changes marshal back via the
        // StatusChanged event. Snapshot the running state on the UI thread before handing off.
        var shouldResume = !_controller.IsRunning;
        Task.Run(() =>
        {
            if (shouldResume) _controller.Resume();
            else _controller.Pause();
        });
    }

    /// <summary>
    /// Open the single-field Quick-Add window (focusing the existing one if it's already up). On submit it
    /// POSTs to /api/family/quick-add with the agent's configured key + URL; the outcome is reported here as
    /// a tray balloon. Runs on the UI thread (the menu click is already on it).
    /// </summary>
    private void OpenQuickAdd()
    {
        if (_quickAdd is { IsVisible: true })
        {
            _quickAdd.Activate();
            return;
        }

        _quickAdd = new QuickAddWindow(NotifyQuickAdd);
        _quickAdd.Closed += (_, _) => _quickAdd = null;
        _quickAdd.Show();
        _quickAdd.Activate();
    }

    /// <summary>Surface a Quick-Add outcome as a tray balloon (success → info, failure → warning).</summary>
    private void NotifyQuickAdd(string message, bool success) => OnUi(() =>
    {
        _icon.ShowBalloonTip(
            4000, "Usage IQ — Quick add", message,
            success ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning);
    });

    private void OnStatusChanged(AgentStatus status) => OnUi(() => Apply(status));

    /// <summary>Force the menu/tooltip to reflect the controller's current status.</summary>
    public void Refresh() => OnUi(() => Apply(_controller.Status));

    private void Apply(AgentStatus status)
    {
        _icon.Text = status.ToTooltip();

        var running = status.State is AgentState.Running or AgentState.Syncing;
        _pauseResume.Header = running ? "Pause" : "Resume";

        var configured = status.State != AgentState.Unconfigured;
        _pauseResume.IsEnabled = configured;
        _syncNow.IsEnabled = configured;
    }

    /// <summary>Marshal an action onto the WPF UI thread (NotifyIcon callbacks may arrive off it).</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
