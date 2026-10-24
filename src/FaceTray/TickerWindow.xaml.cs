using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FaceTray.Core;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

namespace FaceTray;

/// <summary>How long a row's snooze lasts, chosen from the row's menu.</summary>
public enum SnoozeOption
{
    OneHour,
    UntilTomorrow,
}

/// <summary>
/// The notification ticker, styled after Facebook's own notifications
/// menu (SEAN's direction, 2026-10-23): “Notifications” header with a ···
/// menu, All/Unread pills, Today/Earlier sections, circular avatars with
/// a coloured type badge, blue unread dot, and the “See previous
/// notifications” footer pill. The default palette is Facebook dark;
/// v0.2.0 adds a Light palette and a Follow-Windows mode (light theme /
/// high contrast) plus a text-size setting, search, type filters, deeper
/// history with “Show more”, stacked group avatars, per-row mark-as-read
/// and snooze, and the extraction-health warning banner.
///
/// Rows are built in code (not a ListBox template) so the multi-part row
/// layout is exact, and each row is a focusable element with an
/// accessible name.
/// </summary>
public partial class TickerWindow : Window
{
    private sealed record TickerPalette(
        Brush Surface, Brush Border, Brush Text, Brush Muted, Brush Read,
        Brush Hover, Brush PillSelected, Brush PillSelectedText,
        Brush InputBackground, Brush BannerBackground, Brush BannerText);

    private static TickerPalette DarkPalette => new(
        Freeze(0x24, 0x25, 0x26), Freeze(0x3A, 0x3B, 0x3C),
        Freeze(0xE4, 0xE6, 0xEB), Freeze(0xB0, 0xB3, 0xB8), Freeze(0xC9, 0xCC, 0xD1),
        Freeze(0x3A, 0x3B, 0x3C), Freeze(0x3A, 0x3B, 0x3C), Freeze(0xE4, 0xE6, 0xEB),
        Freeze(0x3A, 0x3B, 0x3C), Freeze(0x4A, 0x3A, 0x12), Freeze(0xF5, 0xC5, 0x42));

    private static TickerPalette LightPalette => new(
        Freeze(0xFF, 0xFF, 0xFF), Freeze(0xDD, 0xDF, 0xE2),
        Freeze(0x05, 0x05, 0x05), Freeze(0x65, 0x66, 0x6B), Freeze(0x3E, 0x3F, 0x42),
        Freeze(0xF2, 0xF2, 0xF2), Freeze(0xE4, 0xE6, 0xEB), Freeze(0x05, 0x05, 0x05),
        Freeze(0xF0, 0xF2, 0xF5), Freeze(0xFF, 0xF4, 0xE5), Freeze(0x7A, 0x4A, 0x00));

    private static TickerPalette HighContrastPalette => new(
        SystemColors.WindowBrush, SystemColors.WindowTextBrush,
        SystemColors.WindowTextBrush, SystemColors.GrayTextBrush, SystemColors.WindowTextBrush,
        SystemColors.WindowBrush, SystemColors.HighlightBrush, SystemColors.HighlightTextBrush,
        SystemColors.WindowBrush, SystemColors.WindowBrush, SystemColors.WindowTextBrush);

    private static readonly Brush UnreadDot = Freeze(0x2D, 0x88, 0xFF);
    private static readonly Brush AccentBlue = Freeze(0x2D, 0x88, 0xFF);

    private readonly PollerService _poller;
    private readonly DiagnosticLog? _log;
    private bool _unreadOnly;
    private bool _menuOpen;
    private string _searchQuery = "";
    private int _visibleCount = PageSize;
    private TickerPalette _palette = DarkPalette;

    private const int PageSize = 20;

    private AppSettings _displaySettings = new();

    /// <summary>Settings that drive display (theme, text size, hidden types). Set by App.</summary>
    public AppSettings DisplaySettings
    {
        get => _displaySettings;
        set
        {
            _displaySettings = value ?? new AppSettings();
            ApplyAppearance();
        }
    }

    private double Scale => _displaySettings.TickerTextScalePercent / 100.0;

    // Wired by App.
    public Action? SignInRequested;
    public Action? RefreshRequested;
    public Action? MarkAllReadRequested;
    public Action? SettingsRequested;
    public Action? TogglePauseRequested;
    public Action? ExitRequested;
    public Action<string>? OpenNotificationsPageRequested;
    public Action<NotificationItem>? NotificationClicked;
    public Action<NotificationItem>? MarkReadRequested;
    public Action<NotificationItem, SnoozeOption>? SnoozeRequested;

    public TickerWindow(PollerService poller, DiagnosticLog? log = null)
    {
        _poller = poller;
        _log = log;
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            if (!_menuOpen)
                Hide();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Refresh();
        };
        ApplyAppearance();
        UpdatePills();
    }

    private static Brush Freeze(byte r, byte g, byte b) => Freeze(Color.FromRgb(r, g, b));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush BadgeBrushFor(NotificationType type) => type switch
    {
        NotificationType.Comment or NotificationType.Mention => Freeze(Color.FromRgb(0x42, 0xB8, 0x83)),
        NotificationType.Like => Freeze(Color.FromRgb(0x2D, 0x88, 0xFF)),
        NotificationType.Birthday => Freeze(Color.FromRgb(0xF5, 0x53, 0x6D)),
        _ => Freeze(Color.FromRgb(0x60, 0x67, 0x70)),
    };

    private static string BadgeGlyphFor(NotificationType type) => type switch
    {
        NotificationType.Comment => "\U0001F4AC",   // 💬 speech balloon
        NotificationType.Mention => "\U0001F4AC",   // 💬 (Facebook uses the comment badge for mentions too)
        NotificationType.Like => "\U0001F44D",      // 👍 thumbs up
        NotificationType.Birthday => "\U0001F382",  // 🎂 birthday cake
        _ => "\U0001F514",                          // 🔔 bell
    };

    // ---------- appearance (theme + text size) ----------

    private TickerPalette ResolvePalette()
    {
        switch (_displaySettings.TickerTheme)
        {
            case TickerThemes.Light:
                return LightPalette;
            case TickerThemes.FollowWindows:
                if (SystemParameters.HighContrast)
                    return HighContrastPalette;
                return WindowsAppsUseLightTheme() ? LightPalette : DarkPalette;
            default:
                return DarkPalette;
        }
    }

    private static bool WindowsAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Re-resolves the palette and text scale and restyles the XAML chrome.</summary>
    public void ApplyAppearance()
    {
        _palette = ResolvePalette();
        Width = 380 + (int)((Scale - 1.0) * 100);

        RootBorder.Background = _palette.Surface;
        RootBorder.BorderBrush = _palette.Border;
        TitleText.Foreground = _palette.Text;
        MenuButton.Background = _palette.Hover;
        MenuButton.Foreground = _palette.Text;
        SearchBorder.Background = _palette.InputBackground;
        SearchBox.Foreground = _palette.Text;
        SearchBox.CaretBrush = _palette.Text;
        SearchHint.Foreground = _palette.Muted;
        RefreshingText.Foreground = _palette.Muted;
        HealthBanner.Background = _palette.BannerBackground;
        HealthBannerText.Foreground = _palette.BannerText;
        ShowMoreButton.Background = _palette.Hover;
        ShowMoreButton.Foreground = _palette.Text;
        FooterButton.Background = _palette.Hover;
        FooterButton.Foreground = _palette.Text;
        SignInText.Foreground = _palette.Text;
        LoadingPanel.Foreground = _palette.Muted;
        CaughtUpPanel.Foreground = _palette.Text;
        ErrorText.Foreground = _palette.Text;
        RetryButton.Background = _palette.Hover;
        UpdatePills();
        if (IsVisible)
            Refresh();
    }

    // ---------- showing ----------

    /// <summary>Shows the popup anchored at the bottom-right of the work area, above the taskbar.</summary>
    public void ShowTicker()
    {
        ApplyAppearance();
        Refresh();
        if (!IsVisible)
            Show();
        Activate();
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 10;
        Top = Math.Max(wa.Top + 4, wa.Bottom - ActualHeight - 10);
    }

    public void ToggleTicker()
    {
        if (IsVisible) Hide();
        else ShowTicker();
    }

    // ---------- list building ----------

    /// <summary>Rebuilds the whole popup from the poller's current state.</summary>
    public void Refresh() => Refresh(keepScroll: false);

    private void Refresh(bool keepScroll)
    {
        var scrollOffset = keepScroll ? ListScroll.VerticalOffset : 0;
        var hidden = _displaySettings.HiddenTypeGroups.ToHashSet();
        var filtered = NotificationVisibility.FilterForDisplay(
            _poller.Items, _searchQuery, hidden, _unreadOnly);
        var slice = filtered.Take(_visibleCount).ToList();

        SignInPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        CaughtUpPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ListScroll.Visibility = Visibility.Collapsed;
        ShowMoreButton.Visibility = Visibility.Collapsed;
        HealthBanner.Visibility =
            _poller.HealthWarning && _poller.State != PollerState.NotSignedIn
                ? Visibility.Visible : Visibility.Collapsed;
        RefreshingText.Visibility =
            _poller.State == PollerState.Loading && _poller.Items.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        FooterButton.IsEnabled = true;

        switch (_poller.State)
        {
            case PollerState.NotSignedIn:
                SignInPanel.Visibility = Visibility.Visible;
                return;
            case PollerState.Error when _poller.Items.Count == 0:
                ErrorText.Text = _poller.LastError ?? "Something went wrong reading Facebook.";
                ErrorPanel.Visibility = Visibility.Visible;
                return;
            case PollerState.Starting:
            case PollerState.Loading when _poller.Items.Count == 0:
                LoadingPanel.Visibility = Visibility.Visible;
                return;
        }

        if (slice.Count == 0)
        {
            CaughtUpPanel.Text =
                _searchQuery.Length > 0 ? "No notifications match your search."
                : _unreadOnly && _poller.Items.Count > 0 ? "No unread notifications."
                : "You're all caught up.";
            CaughtUpPanel.Visibility = Visibility.Visible;
            return;
        }

        ItemsPanel.Children.Clear();
        foreach (var group in NotificationGrouping.GroupForDisplay(slice, DateTimeOffset.Now))
        {
            ItemsPanel.Children.Add(BuildSectionHeader(group.Title));
            foreach (var item in group.Items)
                ItemsPanel.Children.Add(BuildRow(item));
        }
        ListScroll.Visibility = Visibility.Visible;
        if (filtered.Count > slice.Count)
            ShowMoreButton.Visibility = Visibility.Visible;
        if (keepScroll)
            ListScroll.ScrollToVerticalOffset(scrollOffset);
        else
            ListScroll.ScrollToTop();
    }

    private UIElement BuildSectionHeader(string title)
    {
        var grid = new Grid { Margin = new Thickness(4, 8, 4, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = title,
            FontSize = 17 * Scale,
            FontWeight = FontWeights.Bold,
            Foreground = _palette.Text,
        };
        grid.Children.Add(text);

        if (title == NotificationGrouping.EarlierTitle)
        {
            var seeAll = new TextBlock
            {
                Text = "See all",
                FontSize = 14 * Scale,
                Foreground = AccentBlue,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            seeAll.MouseLeftButtonUp += (_, _) => OpenNotificationsPageRequested?.Invoke(UrlGuard.NotificationsUrl);
            AutomationProperties.SetName(seeAll, "See all notifications on Facebook");
            Grid.SetColumn(seeAll, 1);
            grid.Children.Add(seeAll);
        }
        return grid;
    }

    private UIElement BuildRow(NotificationItem item)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6 * Scale, 8, 6 * Scale),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Focusable = true,
            Cursor = Cursors.Hand,
            MinHeight = 68 * Scale,
        };
        AutomationProperties.SetName(row,
            $"{item.Text}{(string.IsNullOrEmpty(item.RelativeTimeText) ? "" : ", " + item.RelativeTimeText)}" +
            (item.IsUnread ? ", unread" : ""));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(BuildAvatarStack(item));

        // Text block: bold actor + regular remainder, time below.
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15 * Scale,
            Foreground = item.IsUnread ? _palette.Text : _palette.Read,
            LineHeight = 19 * Scale,
        };
        if (!string.IsNullOrEmpty(item.ActorText) && item.Text.StartsWith(item.ActorText, StringComparison.Ordinal))
        {
            body.Inlines.Add(new System.Windows.Documents.Run(item.ActorText)
            { FontWeight = FontWeights.Bold, Foreground = _palette.Text });
            body.Inlines.Add(new System.Windows.Documents.Run(item.Text[item.ActorText.Length..]));
        }
        else
        {
            body.Inlines.Add(new System.Windows.Documents.Run(item.Text));
        }
        textStack.Children.Add(body);
        if (!string.IsNullOrEmpty(item.RelativeTimeText))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = item.RelativeTimeText,
                FontSize = 13 * Scale,
                Foreground = item.IsUnread ? AccentBlue : _palette.Muted,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        // Actions column: unread dot, plus hover/focus actions (mark read, snooze).
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hoverActions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
        };
        if (item.IsUnread)
        {
            hoverActions.Children.Add(ActionButton("✓", "Mark as read",
                () => MarkReadRequested?.Invoke(item)));
        }
        var snoozeButton = new Button
        {
            Content = "\U0001F552", // 🕒 clock
            Width = 26,
            Height = 26,
            FontSize = 13,
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            Foreground = _palette.Muted,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "Snooze",
        };
        AutomationProperties.SetName(snoozeButton, "Snooze");
        snoozeButton.PreviewMouseLeftButtonUp += (_, e) =>
        {
            // Handle here so the click never bubbles into the row's open action.
            e.Handled = true;
            OpenSnoozeMenu(snoozeButton, item);
        };
        snoozeButton.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                OpenSnoozeMenu(snoozeButton, item);
                e.Handled = true;
            }
        };
        hoverActions.Children.Add(snoozeButton);
        actions.Children.Add(hoverActions);
        if (item.IsUnread)
        {
            actions.Children.Add(new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = UnreadDot,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        row.Child = grid;
        row.MouseEnter += (_, _) =>
        {
            row.Background = _palette.Hover;
            hoverActions.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            if (!row.IsKeyboardFocusWithin)
                hoverActions.Visibility = Visibility.Collapsed;
        };
        row.GotKeyboardFocus += (_, _) => hoverActions.Visibility = Visibility.Visible;
        row.LostKeyboardFocus += (_, _) =>
        {
            if (!row.IsMouseOver)
                hoverActions.Visibility = Visibility.Collapsed;
        };
        row.MouseLeftButtonUp += (_, _) => NotificationClicked?.Invoke(item);
        row.KeyDown += (_, e) =>
        {
            if (e.OriginalSource == row && e.Key is Key.Enter or Key.Space)
            {
                NotificationClicked?.Invoke(item);
                e.Handled = true;
            }
        };
        row.ContextMenu = BuildRowContextMenu(item);
        return row;
    }

    private Button ActionButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 26,
            Height = 26,
            FontSize = 13,
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            Foreground = _palette.Muted,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
        };
        AutomationProperties.SetName(button, tooltip);
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            // Handle here so the click never bubbles into the row's open action.
            e.Handled = true;
            onClick();
        };
        button.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                onClick();
                e.Handled = true;
            }
        };
        return button;
    }

    private ContextMenu BuildRowContextMenu(NotificationItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open in browser", () => NotificationClicked?.Invoke(item)));
        if (item.IsUnread)
            menu.Items.Add(MenuItem("Mark as read", () => MarkReadRequested?.Invoke(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Snooze 1 hour", () => SnoozeRequested?.Invoke(item, SnoozeOption.OneHour)));
        menu.Items.Add(MenuItem("Snooze until tomorrow", () => SnoozeRequested?.Invoke(item, SnoozeOption.UntilTomorrow)));
        TrackMenu(menu);
        return menu;
    }

    private void OpenSnoozeMenu(Button target, NotificationItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Snooze 1 hour", () => SnoozeRequested?.Invoke(item, SnoozeOption.OneHour)));
        menu.Items.Add(MenuItem("Snooze until tomorrow", () => SnoozeRequested?.Invoke(item, SnoozeOption.UntilTomorrow)));
        TrackMenu(menu);
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Keeps the popup from hiding itself while one of its menus is open.</summary>
    private void TrackMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!IsActive && IsVisible)
                Hide();
        };
    }

    // ---------- avatars ----------

    private UIElement BuildAvatarStack(NotificationItem item)
    {
        var container = new Grid
        {
            Width = 56,
            Height = 56,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var urls = item.AvatarUrls.Take(3).ToList();
        if (urls.Count <= 1)
        {
            container.Children.Add(CircleAvatar(item.PrimaryAvatarUrl, 56, 0, 0));
        }
        else if (urls.Count == 2)
        {
            container.Children.Add(CircleAvatar(urls[1], 34, 22, 11));
            container.Children.Add(CircleAvatar(urls[0], 34, 0, 11));
        }
        else
        {
            container.Children.Add(CircleAvatar(urls[2], 30, 26, 13));
            container.Children.Add(CircleAvatar(urls[1], 30, 13, 13));
            container.Children.Add(CircleAvatar(urls[0], 30, 0, 13));
        }

        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = BadgeBrushFor(item.Type),
            BorderBrush = _palette.Surface,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = BadgeGlyphFor(item.Type),
                FontSize = 13,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        container.Children.Add(badge);
        return container;
    }

    /// <summary>One circular avatar (grey placeholder + optional photo), positioned in its parent.</summary>
    private UIElement CircleAvatar(string? avatarUrl, double size, double x, double y)
    {
        var circle = new Grid
        {
            Width = size,
            Height = size,
            Margin = new Thickness(x, y, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        circle.Children.Add(new Ellipse
        {
            Fill = _palette.Hover,
            Stroke = _palette.Surface,
            StrokeThickness = size < 56 ? 2 : 0,
        });

        if (avatarUrl is not null && Uri.TryCreate(avatarUrl, UriKind.Absolute, out _))
        {
            try
            {
                var image = new Image
                {
                    Source = new BitmapImage(new Uri(avatarUrl)),
                    Stretch = Stretch.UniformToFill,
                    Width = size,
                    Height = size,
                    Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2),
                };
                // Field diagnostics (v0.1.1): a captured-but-failed avatar
                // logs here (host+path only), separating capture failures
                // from download failures.
                image.ImageFailed += (_, e) => _log?.Warn(
                    $"Avatar image failed to load: {UrlGuard.RedactForLog(avatarUrl)}" +
                    (e.ErrorException is null ? "" : $" ({e.ErrorException.Message})"));
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                circle.Children.Add(image);
            }
            catch
            {
                // A broken avatar URL just leaves the grey circle.
            }
        }
        return circle;
    }

    // ---------- chrome handlers ----------

    private void UpdatePills()
    {
        AllPill.Background = _unreadOnly ? Brushes.Transparent : _palette.PillSelected;
        AllPill.Foreground = _unreadOnly ? _palette.Text : _palette.PillSelectedText;
        UnreadPill.Background = _unreadOnly ? _palette.PillSelected : Brushes.Transparent;
        UnreadPill.Foreground = _unreadOnly ? _palette.PillSelectedText : _palette.Text;
    }

    private void OnAllPill(object sender, RoutedEventArgs e)
    {
        _unreadOnly = false;
        _visibleCount = PageSize;
        UpdatePills();
        Refresh();
    }

    private void OnUnreadPill(object sender, RoutedEventArgs e)
    {
        _unreadOnly = true;
        _visibleCount = PageSize;
        UpdatePills();
        Refresh();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _visibleCount = PageSize;
        Refresh();
    }

    private void OnShowMore(object sender, RoutedEventArgs e)
    {
        _visibleCount += PageSize;
        Refresh(keepScroll: true);
    }

    private void OnSignIn(object sender, RoutedEventArgs e)
    {
        Hide();
        SignInRequested?.Invoke();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void OnFooter(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenNotificationsPageRequested?.Invoke(UrlGuard.NotificationsUrl);
    }

    private void OnMenuButton(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Refresh Now", () => RefreshRequested?.Invoke()));
        menu.Items.Add(MenuItem("Mark all read", () => MarkAllReadRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Settings…", () => SettingsRequested?.Invoke()));
        menu.Items.Add(MenuItem(_poller.IsPaused ? "Resume polling" : "Pause polling",
            () => TogglePauseRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Exit FaceTray", () => ExitRequested?.Invoke()));
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        TrackMenu(menu);
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
