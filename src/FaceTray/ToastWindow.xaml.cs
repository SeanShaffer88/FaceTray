using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FaceTray.Core;
using Point = System.Windows.Point;

namespace FaceTray;

/// <summary>
/// The new-notification pop-up (v0.2.0): a small Facebook-dark card at the
/// bottom-right of the work area with the sender's avatar, the text and
/// the relative time. Clicking it opens the notification in the browser;
/// it dismisses itself after about six seconds (hovering pauses the
/// countdown). Hand-rolled instead of a WinRT/toast-kit dependency — see
/// DECISIONS.md, 2026-10-24.
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan DismissAfter = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _timer;
    private NotificationItem? _item;

    /// <summary>Raised when the user clicks the toast body.</summary>
    public event Action<NotificationItem>? ToastClicked;

    public ToastWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = DismissAfter };
        _timer.Tick += (_, _) => Hide();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Stop();
                _timer.Start();
            }
        };
    }

    /// <summary>Shows (or replaces) the toast for a newly arrived notification.</summary>
    /// <param name="item">The newest new notification.</param>
    /// <param name="extraCount">How many MORE new notifications arrived in the same poll.</param>
    public void ShowNotification(NotificationItem item, int extraCount)
    {
        _item = item;

        BodyText.Inlines.Clear();
        if (!string.IsNullOrEmpty(item.ActorText) && item.Text.StartsWith(item.ActorText, StringComparison.Ordinal))
        {
            BodyText.Inlines.Add(new Run(item.ActorText) { FontWeight = FontWeights.Bold });
            BodyText.Inlines.Add(new Run(item.Text[item.ActorText.Length..]));
        }
        else
        {
            BodyText.Inlines.Add(new Run(item.Text));
        }

        var meta = string.IsNullOrEmpty(item.RelativeTimeText) ? "now" : item.RelativeTimeText;
        if (extraCount > 0)
            meta += $"  ·  +{extraCount} more new";
        MetaText.Text = meta;

        BuildAvatar(item);

        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;

        _timer.Stop();
        Show();
        _timer.Start();
    }

    /// <summary>
    /// The morning digest (v0.3.0): same card as a single-notification
    /// toast, but the meta line carries the overnight total. Clicking
    /// opens the newest item, exactly like a normal toast.
    /// </summary>
    public void ShowDigest(NotificationItem newest, int totalCount)
    {
        ShowNotification(newest, 0);
        var time = string.IsNullOrEmpty(newest.RelativeTimeText) ? "now" : newest.RelativeTimeText;
        MetaText.Text = totalCount <= 1
            ? $"1 new since your last digest  ·  {time}"
            : $"{totalCount} new since your last digest  ·  {time}";
    }

    private void BuildAvatar(NotificationItem item)
    {
        // Children[0] is the placeholder ellipse; drop any previous image.
        while (AvatarHost.Children.Count > 1)
            AvatarHost.Children.RemoveAt(AvatarHost.Children.Count - 1);

        if (item.PrimaryAvatarUrl is string url && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            try
            {
                var image = new System.Windows.Controls.Image
                {
                    Source = new BitmapImage(new Uri(url)),
                    Stretch = Stretch.UniformToFill,
                    Width = 40,
                    Height = 40,
                    Clip = new EllipseGeometry(new Point(20, 20), 20, 20),
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                AvatarHost.Children.Add(image);
            }
            catch
            {
                // A broken avatar just leaves the grey circle.
            }
        }
    }

    private void OnToastClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = _item;
        Hide();
        if (item is not null)
            ToastClicked?.Invoke(item);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        e.Handled = true;
    }
}
