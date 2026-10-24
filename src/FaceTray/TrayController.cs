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
///
/// v0.2.0: the menu is REBUILT whenever the poller reports in, so it can
/// carry the active-account header, an “Update available” entry and the
/// five most recent notifications. The old balloon tip is gone — the
/// custom toast window replaced it.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly DiagnosticLog _log;
    private readonly Dispatcher _dispatcher;

    private Forms.NotifyIcon? _icon;
    private Icon? _currentIcon;
    private int _unreadCount = -1; // forces the first render
    private bool _paused;
    private bool _disposed;

    private IReadOnlyList<NotificationItem> _recentItems = Array.Empty<NotificationItem>();
    private string? _profileLabel;
    private string? _updateLabel;
    private string _accentHex = AccentPalette.DefaultHex;

    // Wired by App.
    public Action? ToggleTickerRequested;
    public Action? OpenFacebookRequested;
    public Action? OpenNotificationsRequested;
    public Action? RefreshRequested;
    public Action? MarkAllReadRequested;
    public Action? TogglePauseRequested;
    public Action? SettingsRequested;
    public Action? ExitRequested;
    public Action<NotificationItem>? RecentItemClicked;
    public Action? UpdateClicked;

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
            _icon.ContextMenuStrip = BuildMenu();
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

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        if (_profileLabel is not null)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem($"Account: {_profileLabel}") { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        if (_updateLabel is not null)
        {
            menu.Items.Add(_updateLabel, null, (_, _) => Dispatch(() => UpdateClicked?.Invoke()));
            menu.Items.Add(new Forms.ToolStripSeparator());
        }

        menu.Items.Add("Open Facebook", null, (_, _) => Dispatch(() => OpenFacebookRequested?.Invoke()));
        menu.Items.Add("Notifications page", null, (_, _) => Dispatch(() => OpenNotificationsRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());

        if (_recentItems.Count > 0)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem("Recent") { Enabled = false });
            foreach (var item in _recentItems.Take(5))
            {
                var captured = item;
                var entry = new Forms.ToolStripMenuItem(DisplayText.Truncate(item.Text, 48));
                entry.Click += (_, _) => Dispatch(() => RecentItemClicked?.Invoke(captured));
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
        }

        menu.Items.Add("Refresh Now", null, (_, _) => Dispatch(() => RefreshRequested?.Invoke()));
        menu.Items.Add("Mark all read", null, (_, _) => Dispatch(() => MarkAllReadRequested?.Invoke()));
        menu.Items.Add(_paused ? "Resume polling" : "Pause polling", null,
            (_, _) => Dispatch(() => TogglePauseRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => Dispatch(() => SettingsRequested?.Invoke()));
        menu.Items.Add("Exit", null, (_, _) => Dispatch(() => ExitRequested?.Invoke()));
        return menu;
    }

    private void RebuildMenu()
    {
        if (_icon is null)
            return;
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    /// <summary>Feeds the menu's dynamic parts; called whenever the poller reports in.</summary>
    public void SetRecentItems(IReadOnlyList<NotificationItem> items) => _recentItems = items;

    /// <summary>Shows the active account in the menu + tooltip; null when there is only one profile.</summary>
    public void SetProfileLabel(string? profileName)
    {
        if (_profileLabel == profileName)
            return;
        _profileLabel = profileName;
        RefreshTooltip();
        RebuildMenu();
    }

    public void SetUpdateAvailable(string? versionLabel)
    {
        var label = versionLabel is null ? null : $"Update available: {versionLabel}";
        if (_updateLabel == label)
            return;
        _updateLabel = label;
        RebuildMenu();
    }

    /// <summary>Rebuilds the menu after dynamic state (recent items, pause) changed.</summary>
    public void RefreshMenu() => RebuildMenu();

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
            return;
        _paused = paused;
        RebuildMenu();
    }

    private void Dispatch(Action action)
    {
        try { _dispatcher.BeginInvoke(action); }
        catch { /* shutting down */ }
    }

    /// <summary>
    /// The active profile's bubble colour (v0.3.0). Changing it forces a
    /// re-render even when the count itself has not moved.
    /// </summary>
    public void SetAccentColor(string? accentHex)
    {
        var normalized = AccentPalette.NormalizeOrDefault(accentHex);
        if (_accentHex == normalized)
            return;
        _accentHex = normalized;
        var count = Math.Max(0, _unreadCount);
        _unreadCount = -1; // force the render through SetUnreadCount
        SetUnreadCount(count);
    }

    /// <summary>Re-renders the counter icon when the count changes, and refreshes the tooltip.</summary>
    public void SetUnreadCount(int unreadCount)
    {
        if (_icon is null)
            return;
        if (unreadCount != _unreadCount)
        {
            _unreadCount = unreadCount;
            try
            {
                var old = _currentIcon;
                _currentIcon = TrayIconRenderer.Render(unreadCount, _accentHex);
                _icon.Icon = _currentIcon;
                // The old icon is no longer referenced by the NotifyIcon; safe to dispose now.
                old?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"Tray icon render failed: {ex.Message}");
            }
        }
        RefreshTooltip();
    }

    private void RefreshTooltip()
    {
        if (_icon is null || _unreadCount < 0)
            return;
        var text = BadgeText.TooltipFor(_unreadCount);
        if (_profileLabel is not null)
            text = _unreadCount <= 0
                ? $"FaceTray ({_profileLabel})"
                : $"FaceTray ({_profileLabel}) — {_unreadCount} unread";
        // NotifyIcon text is capped at 63 chars on modern Windows.
        _icon.Text = text.Length <= 63 ? text : text[..63];
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
                _icon.ContextMenuStrip?.Dispose();
                _icon.Dispose();
                _icon = null;
            }
            _currentIcon?.Dispose();
            _currentIcon = null;
        }
        catch { /* shutdown must not hang on cleanup */ }
    }
}
