namespace ScreenCastPresenter.Core;

/// <summary>An edge of a screen, where a taskbar (or another app bar) can be docked.</summary>
internal enum ScreenEdge { Left, Top, Right, Bottom }

/// <summary>
/// Leave out the taskbar (FR-112): a whole screen shown to the audience stops short of its taskbar,
/// which holds the presenter's app icons, unread badges, the clock and notifications. What's left
/// out is everything Windows reserves at the screen's edges (its work area: the taskbar and any
/// other docked bar), plus the full thickness of a taskbar that hides itself, which pops up over
/// the screen when the pointer reaches the edge. Kept as a crop, so zoom, marks, hidden parts,
/// screenshots and recordings all stop at it too.
/// </summary>
internal static class TaskbarCrop
{
    /// <summary>At most this much of a screen's width or height is left out at one edge: a bar can't swallow the screen.</summary>
    public const double MaxShare = 0.4;

    /// <summary>
    /// The part of <paramref name="monitor"/> clear of its taskbars, as a crop of the screen; null
    /// when nothing is reserved (no taskbar on this screen, or one that's switched off).
    /// </summary>
    /// <param name="workArea">The screen's work area (Windows leaves its taskbar and docked bars out of it).</param>
    /// <param name="autoHidden">Taskbars that hide themselves on this screen: their edge and full thickness in pixels.</param>
    public static CropRegion? ClearOf(Rectangle monitor, Rectangle workArea, IReadOnlyList<(ScreenEdge Edge, int Thickness)>? autoHidden = null)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0) return null;
        Rectangle work = Rectangle.Intersect(monitor, workArea);
        if (work.Width <= 0 || work.Height <= 0) work = monitor; // nonsense from Windows: nothing is known to be reserved
        int left = work.Left - monitor.Left, top = work.Top - monitor.Top, right = monitor.Right - work.Right, bottom = monitor.Bottom - work.Bottom;
        foreach ((ScreenEdge edge, int thickness) in autoHidden ?? [])
        {
            int t = Math.Max(0, thickness);
            switch (edge)
            {
                case ScreenEdge.Left: left = Math.Max(left, t); break;
                case ScreenEdge.Top: top = Math.Max(top, t); break;
                case ScreenEdge.Right: right = Math.Max(right, t); break;
                case ScreenEdge.Bottom: bottom = Math.Max(bottom, t); break;
            }
        }
        int maxX = (int)(monitor.Width * MaxShare), maxY = (int)(monitor.Height * MaxShare);
        left = Math.Clamp(left, 0, maxX);
        right = Math.Clamp(right, 0, maxX);
        top = Math.Clamp(top, 0, maxY);
        bottom = Math.Clamp(bottom, 0, maxY);
        if (left == 0 && top == 0 && right == 0 && bottom == 0) return null;
        return CropRegion.Create(
            (double)left / monitor.Width,
            (double)top / monitor.Height,
            (double)(monitor.Width - left - right) / monitor.Width,
            (double)(monitor.Height - top - bottom) / monitor.Height);
    }

    /// <summary>
    /// What to show of a screen: the part the presenter chose (null: all of it) kept inside what's
    /// clear of the taskbar. <c>Empty</c> when the chosen part is all taskbar: then nothing of it may be shown.
    /// </summary>
    public static (CropRegion? Crop, bool Empty) Inside(CropRegion? chosen, CropRegion? clear)
    {
        if (clear is null) return (chosen, false);
        if (chosen is null) return (clear, false);
        double left = Math.Max(chosen.X, clear.X), top = Math.Max(chosen.Y, clear.Y);
        double right = Math.Min(chosen.X + chosen.Width, clear.X + clear.Width), bottom = Math.Min(chosen.Y + chosen.Height, clear.Y + clear.Height);
        if (right - left <= 0.0001 || bottom - top <= 0.0001) return (null, true);
        // A sliver that overlaps the taskbar's edge is too small for a crop: show nothing rather than more.
        return CropRegion.Create(left, top, right - left, bottom - top) is { } inside ? (inside, false) : (null, true);
    }

    /// <summary>The thickness of a bar along <paramref name="edge"/>: the short side of its window, as it is when shown.</summary>
    public static int Thickness(ScreenEdge edge, Rectangle bar) => edge is ScreenEdge.Left or ScreenEdge.Right ? Math.Max(0, bar.Width) : Math.Max(0, bar.Height);
}
