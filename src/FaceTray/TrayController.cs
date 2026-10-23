using System.Drawing;
using System.Windows.Threading;
using FaceTray.Core;
using Forms = System.Windows.Forms;

namespace FaceTray;

/// <summary>
/// The tray icon and its right-click menu. Handlers only raise events /
/// invoke App callbacks on the dispatcher — nothing is disposed inside an
/// event dispatch (the ytTrayDL lesson); disposal happens once, from the
/// app's exit path, after the message loop is leaving.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly DiagnosticLog _log;
    private readonly Dispatcher _dispatcher;

    private Forms.NotifyIcon? _icon;
    private Icon? _currentIcon;
    private int _unreadCount = -1; // forces the first render
    private bool _disposed;

    // Wired by App.
    public Action? ToggleTickerRequested;
    public Action? OpenFacebookRequested;
    public Action? OpenNotificationsRequested;
    public Action? RefreshRequested;
    public Action? MarkAllReadRequested;
    public Action? TogglePauseRequested;
    public Action? SettingsRequested;
    public Action? ExitRequested;

    public TrayController(DiagnosticLog log, Dispatcher dispatcher)
    {
        _log = log;
        _dispatcher = dispatcher;
    }

    public void Build()
    {
        try
        {
            _icon = new Forms.NotifyIcon { Visible = true, Text = "FaceTray" };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Facebook", null, (_, _) => Dispatch(() => OpenFacebookRequested?.Invoke()));
            menu.Items.Add("Notifications page", null, (_, _) => Dispatch(() => OpenNotificationsRequested?.Invoke()));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Refresh Now", null, (_, _) => Dispatch(() => RefreshRequested?.Invoke()));
            menu.Items.Add("Mark all read", null, (_, _) => Dispatch(() => MarkAllReadRequested?.Invoke()));
            var pauseItem = new Forms.ToolStripMenuItem("Pause polling");
            pauseItem.Click += (_, _) => Dispatch(() => TogglePauseRequested?.Invoke());
            menu.Items.Add(pauseItem);
            PauseMenuItem = pauseItem;
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Settings…", null, (_, _) => Dispatch(() => SettingsRequested?.Invoke()));
            menu.Items.Add("Exit", null, (_, _) => Dispatch(() => ExitRequested?.Invoke()));
            _icon.ContextMenuStrip = menu;
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    Dispatch(() => ToggleTickerRequested?.Invoke());
            };
            SetUnreadCount(0);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray creation failed: {ex.Message}");
            _icon = null;
        }
    }

    public Forms.ToolStripMenuItem? PauseMenuItem { get; private set; }

    public void SetPaused(bool paused)
    {
        if (PauseMenuItem is not null)
            PauseMenuItem.Text = paused ? "Resume polling" : "Pause polling";
    }

    private void Dispatch(Action action)
    {
        try { _dispatcher.BeginInvoke(action); }
        catch { /* shutting down */ }
    }

    /// <summary>Re-renders the counter icon when the count changes, and refreshes the tooltip.</summary>
    public void SetUnreadCount(int unreadCount)
    {
        if (_icon is null || unreadCount == _unreadCount)
            return;
        _unreadCount = unreadCount;
        try
        {
            var old = _currentIcon;
            _currentIcon = TrayIconRenderer.Render(unreadCount);
            _icon.Icon = _currentIcon;
            // The old icon is no longer referenced by the NotifyIcon; safe to dispose now.
            old?.Dispose();
            _icon.Text = BadgeText.TooltipFor(unreadCount);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray icon render failed: {ex.Message}");
        }
    }

    /// <summary>Balloon for new notifications (only called when toasts are enabled in Settings).</summary>
    public void ShowNewNotificationsBalloon(int newCount)
    {
        if (_icon is null)
            return;
        try
        {
            _icon.BalloonTipTitle = "FaceTray";
            _icon.BalloonTipText = newCount == 1
                ? "1 new Facebook notification"
                : $"{newCount} new Facebook notifications";
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _log.Warn($"Balloon failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_icon is not null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
            _currentIcon?.Dispose();
            _currentIcon = null;
        }
        catch { /* shutdown must not hang on cleanup */ }
    }
}
