using System.Drawing;
using System.Runtime.InteropServices;
using FaceTray.Core;

namespace FaceTray;

/// <summary>
/// Renders the tray icon at runtime: a dark rounded square with a white
/// “f”, plus a red counter bubble with the white badge text when there are
/// unread notifications. Windows has no “badge a tray icon” API — swapping
/// in a dynamically drawn icon is how every app that does this does it.
/// Rendered at 32px; Windows scales for the tray and DPI.
/// </summary>
public static class TrayIconRenderer
{
    private const int Size = 32;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Caller owns the returned icon and must dispose it (after the tray no longer uses it).</summary>
    public static Icon Render(int unreadCount)
    {
        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Dark rounded square.
            using var bg = new SolidBrush(Color.FromArgb(24, 24, 27));
            FillRoundedRect(g, bg, new Rectangle(1, 1, Size - 2, Size - 2), 8);

            // White “f”.
            using var fFont = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            g.DrawString("f", fFont, white, 8.5f, 3f);

            // Red counter bubble with white badge text.
            var badge = BadgeText.For(unreadCount);
            if (badge is not null)
            {
                var bubble = badge.Length == 1
                    ? new Rectangle(Size - 15, 0, 15, 15)
                    : new Rectangle(Size - 19, 0, 19, 15);
                using var red = new SolidBrush(Color.FromArgb(229, 72, 77)); // FaceTray red #E5484D
                FillRoundedRect(g, red, bubble, 7);
                using var badgeFont = new Font("Segoe UI", badge.Length == 1 ? 9.5f : 8f, FontStyle.Bold, GraphicsUnit.Pixel);
                var textSize = g.MeasureString(badge, badgeFont);
                g.DrawString(badge, badgeFont, white,
                    bubble.Left + (bubble.Width - textSize.Width) / 2f,
                    bubble.Top + (bubble.Height - textSize.Height) / 2f - 0.5f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var fromHandle = Icon.FromHandle(handle);
            // Clone so the clone owns its own handle; ours is destroyed below.
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
