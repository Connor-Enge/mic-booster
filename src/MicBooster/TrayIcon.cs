using System.Drawing;
using System.Windows.Forms;

namespace MicBooster;

/// <summary>
/// Notification-area presence: an icon, a tooltip that reflects engine state, and a menu
/// so the microphone can be muted without bringing the window back.
/// </summary>
/// <remarks>
/// All events are raised on the thread that created the instance, because
/// <see cref="NotifyIcon"/> puts its message window on that thread and WPF's dispatcher
/// pumps it. Create this from the UI thread and handle its events directly.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _engineItem;

    // Only an icon we extracted ourselves may be disposed; SystemIcons handles are shared.
    private readonly Icon? _extractedIcon;

    private bool _muted;
    private bool _running;
    private bool _disposed;

    /// <summary>The user asked for the main window back.</summary>
    public event Action? ShowRequested;

    /// <summary>The user asked to quit for real, rather than hide.</summary>
    public event Action? ExitRequested;

    /// <summary>The user asked to flip the mute state.</summary>
    public event Action? MuteToggleRequested;

    /// <summary>The user asked to start or stop processing.</summary>
    public event Action? EngineToggleRequested;

    /// <summary>Creates the icon and its menu, and makes it visible immediately.</summary>
    public TrayIcon()
    {
        _showItem = new ToolStripMenuItem("Show Mic Booster");
        _showItem.Click += (_, _) => ShowRequested?.Invoke();

        _muteItem = new ToolStripMenuItem("Mute microphone");
        _muteItem.Click += (_, _) => MuteToggleRequested?.Invoke();

        _engineItem = new ToolStripMenuItem("Start processing");
        _engineItem.Click += (_, _) => EngineToggleRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_muteItem);
        menu.Items.Add(_engineItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _extractedIcon = TryExtractProcessIcon();

        _icon = new NotifyIcon
        {
            Icon = _extractedIcon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "Mic Booster",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();

        Refresh();
    }

    /// <summary>Tells the menu and tooltip whether the microphone is currently muted.</summary>
    public void SetMuted(bool muted)
    {
        if (_disposed || _muted == muted)
        {
            return;
        }

        _muted = muted;
        Refresh();
    }

    /// <summary>Tells the menu and tooltip whether the engine is currently processing.</summary>
    public void SetRunning(bool running)
    {
        if (_disposed || _running == running)
        {
            return;
        }

        _running = running;
        Refresh();
    }

    /// <summary>
    /// Shows a transient balloon. Silently does nothing when the shell refuses it, which it
    /// does whenever notifications are suppressed by focus assist or policy.
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _icon.ShowBalloonTip(4000, string.IsNullOrWhiteSpace(title) ? "Mic Booster" : title, text, ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // A refused notification is never worth interrupting the user over.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Hiding before disposal is what stops a dead icon from being left in the tray.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _extractedIcon?.Dispose();
    }

    private void Refresh()
    {
        _muteItem.Text = _muted ? "Unmute microphone" : "Mute microphone";
        _muteItem.Checked = _muted;
        _engineItem.Text = _running ? "Stop processing" : "Start processing";

        // The shell truncates this at 63 characters, so keep it short.
        _icon.Text = _running
            ? (_muted ? "Mic Booster - running, muted" : "Mic Booster - running")
            : "Mic Booster - stopped";
    }

    private static Icon? TryExtractProcessIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception)
        {
            // Single-file publishing, a missing icon resource, or a locked file all land here;
            // the caller falls back to a stock icon rather than failing to show a tray entry.
            return null;
        }
    }
}
