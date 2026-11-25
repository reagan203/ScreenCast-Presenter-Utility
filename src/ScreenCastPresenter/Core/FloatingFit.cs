namespace ScreenCastPresenter.Core;

/// <summary>
/// The floating output window takes the shape of what it shows, so there are no black bars: when
/// the content changes, its height follows its width (or its width follows its height when that
/// would be taller than the screen), and dragging an edge or corner keeps the shape. Pure, so the
/// sizes can be tested without windows.
/// </summary>
internal static class FloatingFit
{
    /// <summary>WM_SIZING's edge codes (WMSZ_*).</summary>
    public const int Left = 1, Right = 2, Top = 3, TopLeft = 4, TopRight = 5, Bottom = 6, BottomLeft = 7, BottomRight = 8;

    /// <summary>Same shape within 1% (a pixel of rounding is never worth a resize).</summary>
    public static bool SameShape(Size a, Size b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 &&
        Math.Abs((double)a.Width / a.Height - (double)b.Width / b.Height) <= 0.01 * ((double)b.Width / b.Height);

    /// <summary>
    /// The client size for <paramref name="content"/>: the current width kept, the height
    /// following; never bigger than <paramref name="max"/> (the screen), and as close to
    /// <paramref name="min"/> as the shape allows. Unknown content keeps the current size.
    /// </summary>
    public static Size Client(Size current, Size content, Size min, Size max)
    {
        if (content.Width <= 0 || content.Height <= 0 || current.Width <= 0 || max.Width <= 0 || max.Height <= 0) return current;
        double aspect = (double)content.Width / content.Height;
        double w = current.Width, h = w / aspect;
        if (h > max.Height) (h, w) = (max.Height, max.Height * aspect);
        if (w > max.Width) (w, h) = (max.Width, max.Width / aspect);
        if (w < min.Width) (w, h) = (min.Width, min.Width / aspect);
        if (h < min.Height) (h, w) = (min.Height, min.Height * aspect);
        // A very wide or very tall picture can't meet both limits: the screen wins.
        if (w > max.Width) (w, h) = (max.Width, max.Width / aspect);
        if (h > max.Height) (h, w) = (max.Height, max.Height * aspect);
        return new Size(Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    /// <summary>
    /// WM_SIZING: the window rectangle being dragged, adjusted so the client keeps the content's
    /// shape. The edge being dragged decides which side follows; the opposite edge or corner stays put.
    /// </summary>
    public static Rectangle Sizing(Rectangle window, int edge, Size nonClient, Size content, Size minClient)
    {
        if (content.Width <= 0 || content.Height <= 0) return window;
        double aspect = (double)content.Width / content.Height;
        double cw = Math.Max(1, window.Width - nonClient.Width), ch = Math.Max(1, window.Height - nonClient.Height);
        if (edge is Top or Bottom) cw = ch * aspect; // dragging the top or bottom: the width follows
        else ch = cw / aspect;                       // a side or a corner: the height follows
        if (cw < minClient.Width) (cw, ch) = (minClient.Width, minClient.Width / aspect);
        if (ch < minClient.Height) (ch, cw) = (minClient.Height, minClient.Height * aspect);

        int width = (int)Math.Round(cw) + nonClient.Width, height = (int)Math.Round(ch) + nonClient.Height;
        int x = edge is Left or TopLeft or BottomLeft ? window.Right - width : window.Left;
        int y = edge is Top or TopLeft or TopRight ? window.Bottom - height : window.Top;
        return new Rectangle(x, y, width, height);
    }

    /// <summary>Moves (never resizes) <paramref name="window"/> back inside <paramref name="area"/> as far as it fits.</summary>
    public static Rectangle KeepOn(Rectangle window, Rectangle area)
    {
        int x = Math.Max(area.Left, Math.Min(window.X, area.Right - window.Width));
        int y = Math.Max(area.Top, Math.Min(window.Y, area.Bottom - window.Height));
        return window with { X = x, Y = y };
    }
}
