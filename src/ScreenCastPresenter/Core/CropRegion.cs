namespace ScreenCastPresenter.Core;

/// <summary>
/// The part of a source to show (FR-04), as fractions (0–1) of its client area, so a saved crop
/// still fits after the window is resized. Stored per app in settings.
/// </summary>
internal sealed record CropRegion(double X, double Y, double Width, double Height)
{
    /// <summary>Smallest crop: 2% of either side, so a stray click can't make a 1-pixel source.</summary>
    public const double MinSize = 0.02;

    public static CropRegion Full { get; } = new(0, 0, 1, 1);

    public bool IsFull => X <= 0.0001 && Y <= 0.0001 && Width >= 0.9999 && Height >= 0.9999;

    /// <summary>Clamped to the source and at least <see cref="MinSize"/>; null if the input is nonsense.</summary>
    public static CropRegion? Create(double x, double y, double width, double height) => Create(x, y, width, height, MinSize);

    /// <summary>Clamped to the source and at least <paramref name="minSize"/> a side (small boxes over a word use a smaller minimum).</summary>
    public static CropRegion? Create(double x, double y, double width, double height, double minSize)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)) return null;
        double left = Math.Clamp(Math.Min(x, x + width), 0, 1);
        double top = Math.Clamp(Math.Min(y, y + height), 0, 1);
        double right = Math.Clamp(Math.Max(x, x + width), 0, 1);
        double bottom = Math.Clamp(Math.Max(y, y + height), 0, 1);
        if (right - left < minSize || bottom - top < minSize) return null;
        return new CropRegion(left, top, right - left, bottom - top);
    }

    /// <summary>A crop from a rectangle drawn on screen over the source's client area (both in screen pixels).</summary>
    public static CropRegion? FromScreen(Rectangle selection, Rectangle client)
    {
        if (client.Width <= 0 || client.Height <= 0) return null;
        return Create(
            (double)(selection.Left - client.Left) / client.Width,
            (double)(selection.Top - client.Top) / client.Height,
            (double)selection.Width / client.Width,
            (double)selection.Height / client.Height);
    }

    /// <summary>The crop in pixels of a source of <paramref name="size"/>; always at least 1×1 and inside it.</summary>
    public Rectangle ToPixels(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return Rectangle.Empty;
        int left = (int)Math.Round(X * size.Width), top = (int)Math.Round(Y * size.Height);
        int right = (int)Math.Round((X + Width) * size.Width), bottom = (int)Math.Round((Y + Height) * size.Height);
        left = Math.Clamp(left, 0, size.Width - 1);
        top = Math.Clamp(top, 0, size.Height - 1);
        right = Math.Clamp(right, left + 1, size.Width);
        bottom = Math.Clamp(bottom, top + 1, size.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>The crop on screen, given where the source's client area is.</summary>
    public Rectangle ToScreen(Rectangle client)
    {
        Rectangle r = ToPixels(client.Size);
        r.Offset(client.Location);
        return r;
    }
}
