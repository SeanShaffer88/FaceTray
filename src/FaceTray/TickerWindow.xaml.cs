using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FaceTray.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;

namespace FaceTray;

/// <summary>
/// The notification ticker, styled to match Facebook's own notifications
/// menu (SEAN's direction, 2026-10-23): #242526 panel, “Notifications”
/// header with a ··· menu, All/Unread pills, Today/Earlier sections,
/// 56px circular avatars with a coloured type badge, blue unread dot,
/// and the “See previous notifications” footer pill.
///
/// Rows are built in code (not a ListBox template) so the multi-part row
/// layout — avatar + overlapping badge + two-line text + unread dot — is
/// exact, and each row is a focusable element with an accessible name.
/// Limitation: group notifications show only the first avatar (Facebook
/// stacks several); recorded in KNOWN_LIMITATIONS.md.
/// </summary>
public partial class TickerWindow : Window
{
    private static readonly Brush PanelText = Freeze(Color.FromRgb(0xE4, 0xE6, 0xEB));
    private static readonly Brush MutedText = Freeze(Color.FromRgb(0xB0, 0xB3, 0xB8));
    private static readonly Brush ReadText = Freeze(Color.FromRgb(0xC9, 0xCC, 0xD1));
    private static readonly Brush HoverBg = Freeze(Color.FromRgb(0x3A, 0x3B, 0x3C));
    private static readonly Brush PillSelectedBg = Freeze(Color.FromRgb(0x3A, 0x3B, 0x3C));
    private static readonly Brush UnreadDot = Freeze(Color.FromRgb(0x2D, 0x88, 0xFF));
    private static readonly Brush PanelBorder = Freeze(Color.FromRgb(0x24, 0x25, 0x26));

    private readonly PollerService _poller;
    private readonly DiagnosticLog? _log;
    private bool _unreadOnly;
    private bool _menuOpen;

    // Wired by App.
    public Action? SignInRequested;
    public Action? RefreshRequested;
    public Action? MarkAllReadRequested;
    public Action? SettingsRequested;
    public Action? TogglePauseRequested;
    public Action? ExitRequested;
    public Action<string>? OpenNotificationsPageRequested;
    public Action<NotificationItem>? NotificationClicked;

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
        UpdatePills();
    }

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

    /// <summary>Shows the popup anchored at the bottom-right of the work area, above the taskbar.</summary>
    public void ShowTicker()
    {
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

    /// <summary>Rebuilds the whole popup from the poller's current state.</summary>
    public void Refresh()
    {
        var items = _unreadOnly
            ? _poller.Items.Where(i => i.IsUnread).ToList()
            : _poller.Items.ToList();

        SignInPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        CaughtUpPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ListScroll.Visibility = Visibility.Collapsed;
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

        if (items.Count == 0)
        {
            CaughtUpPanel.Text = _unreadOnly && _poller.Items.Count > 0
                ? "No unread notifications."
                : "You're all caught up.";
            CaughtUpPanel.Visibility = Visibility.Visible;
            return;
        }

        ItemsPanel.Children.Clear();
        foreach (var group in NotificationGrouping.GroupForDisplay(items, DateTimeOffset.Now))
        {
            ItemsPanel.Children.Add(BuildSectionHeader(group.Title));
            foreach (var item in group.Items)
                ItemsPanel.Children.Add(BuildRow(item));
        }
        ListScroll.Visibility = Visibility.Visible;
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
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = PanelText,
        };
        grid.Children.Add(text);

        if (title == NotificationGrouping.EarlierTitle)
        {
            var seeAll = new TextBlock
            {
                Text = "See all",
                FontSize = 14,
                Foreground = UnreadDot,
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
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Focusable = true,
            Cursor = Cursors.Hand,
        };
        AutomationProperties.SetName(row,
            $"{item.Text}{(string.IsNullOrEmpty(item.RelativeTimeText) ? "" : ", " + item.RelativeTimeText)}" +
            (item.IsUnread ? ", unread" : ""));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });

        // Avatar (56px circle) + type badge (28px circle, bottom-right).
        grid.Children.Add(BuildAvatar(item));

        // Text block: bold actor + regular remainder, time below.
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Foreground = item.IsUnread ? PanelText : ReadText,
            LineHeight = 19,
        };
        if (!string.IsNullOrEmpty(item.ActorText) && item.Text.StartsWith(item.ActorText, StringComparison.Ordinal))
        {
            body.Inlines.Add(new System.Windows.Documents.Run(item.ActorText)
            { FontWeight = FontWeights.Bold, Foreground = PanelText });
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
                FontSize = 13,
                Foreground = item.IsUnread ? UnreadDot : MutedText,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        if (item.IsUnread)
        {
            var dot = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = UnreadDot,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }

        row.Child = grid;
        row.MouseEnter += (_, _) => row.Background = HoverBg;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => NotificationClicked?.Invoke(item);
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                NotificationClicked?.Invoke(item);
                e.Handled = true;
            }
        };
        return row;
    }

    private UIElement BuildAvatar(NotificationItem item)
    {
        var container = new Grid
        {
            Width = 56,
            Height = 56,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        container.Children.Add(new Ellipse { Fill = HoverBg });

        if (item.PrimaryAvatarUrl is string avatarUrl && Uri.TryCreate(avatarUrl, UriKind.Absolute, out _))
        {
            try
            {
                var image = new Image
                {
                    Source = new BitmapImage(new Uri(avatarUrl)),
                    Stretch = Stretch.UniformToFill,
                    Width = 56,
                    Height = 56,
                    Clip = new EllipseGeometry(new Point(28, 28), 28, 28),
                };
                // Field diagnostics (v0.1.1): when an avatar is captured but
                // still renders grey, this log line proves the URL reached
                // the shell and failed to load — as opposed to the script
                // capturing no URL at all (no line). Host+path only.
                image.ImageFailed += (_, e) => _log?.Warn(
                    $"Avatar image failed to load: {UrlGuard.RedactForLog(avatarUrl)}" +
                    (e.ErrorException is null ? "" : $" ({e.ErrorException.Message})"));
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                container.Children.Add(image);
            }
            catch
            {
                // A broken avatar URL just leaves the grey circle.
            }
        }

        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = BadgeBrushFor(item.Type),
            BorderBrush = PanelBorder,
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

    private void UpdatePills()
    {
        AllPill.Background = _unreadOnly ? Brushes.Transparent : PillSelectedBg;
        AllPill.Foreground = PanelText;
        UnreadPill.Background = _unreadOnly ? PillSelectedBg : Brushes.Transparent;
        UnreadPill.Foreground = PanelText;
    }

    private void OnAllPill(object sender, RoutedEventArgs e)
    {
        _unreadOnly = false;
        UpdatePills();
        Refresh();
    }

    private void OnUnreadPill(object sender, RoutedEventArgs e)
    {
        _unreadOnly = true;
        UpdatePills();
        Refresh();
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
        _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!IsActive)
                Hide();
        };
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
