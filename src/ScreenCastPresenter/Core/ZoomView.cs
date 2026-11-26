namespace ScreenCastPresenter.Core;

/// <summary>
/// Zooming in on the TV around the pointer (FR-64), like ZoomIt: the view is a moving crop, so it
/// never shows anything outside what was chosen (and its crop). Pure maths, unit-tested.
/// </summary>
internal static class ZoomView
{
    public static IReadOnlyList<double> Levels { get; } = [1.5, 2, 3, 4];
    public const double DefaultLevel = 2;

    /// <summary>How far the view moves towards the pointer each frame: smooth, but never far behind.</summary>
    public const float Follow = 0.3f;

    /// <summary>The nearest offered level (a hand-edited settings file may hold anything).</summary>
    public static double Normalize(double level) =>
        double.IsFinite(level) ? Levels.MinBy(l => Math.Abs(l - level)) : DefaultLevel;

    /// <summary>The next level up, back to the first after the last.</summary>
    public static double Next(double level)
    {
        double current = Normalize(level);
        int i = Levels.ToList().IndexOf(current);
        return Levels[(i + 1) % Levels.Count];
    }

    /// <summary>
    /// The view (fractions of the whole content) <paramref name="level"/> times closer than
    /// <paramref name="crop"/> (or all of it), centred on <paramref name="centre"/> as far as it
    /// fits: at an edge the view stops, it never shows past the crop. Never smaller than the
    /// smallest crop allowed.
    /// </summary>
    public static CropRegion View(PointF centre, double level, CropRegion? crop)
    {
        CropRegion area = crop ?? CropRegion.Full;
        double zoom = double.IsFinite(level) && level >= 1 ? level : 1;
        double w = Math.Min(area.Width, Math.Max(area.Width / zoom, CropRegion.MinSize));
        double h = Math.Min(area.Height, Math.Max(area.Height / zoom, CropRegion.MinSize));
        double cx = float.IsFinite(centre.X) ? centre.X : area.X + area.Width / 2;
        double cy = float.IsFinite(centre.Y) ? centre.Y : area.Y + area.Height / 2;
        double x = Math.Clamp(cx - w / 2, area.X, area.X + area.Width - w);
        double y = Math.Clamp(cy - h / 2, area.Y, area.Y + area.Height - h);
        return new CropRegion(x, y, w, h);
    }

    /// <summary>The centre of the crop (or of everything): where a zoom starts when the pointer isn't over what's shown.</summary>
    public static PointF Middle(CropRegion? crop)
    {
        CropRegion area = crop ?? CropRegion.Full;
        return new PointF((float)(area.X + area.Width / 2), (float)(area.Y + area.Height / 2));
    }

    /// <summary>One step of following: part of the way to <paramref name="target"/>, arriving exactly once close enough.</summary>
    public static PointF Ease(PointF current, PointF target, float amount = Follow)
    {
        float dx = target.X - current.X, dy = target.Y - current.Y;
        if (Math.Abs(dx) < 0.0005f && Math.Abs(dy) < 0.0005f) return target;
        return new PointF(current.X + dx * amount, current.Y + dy * amount);
    }
}

/// <summary>Mouse buttons as the click highlights see them (FR-65).</summary>
internal static class PointerInput
{
    /// <summary>
    /// Windows reports the physical buttons; with "switch primary and secondary buttons" on, the
    /// physical right button is the one that clicks. Returns the primary and secondary button.
    /// </summary>
    public static (bool Primary, bool Secondary) Buttons(bool leftDown, bool rightDown, bool swapped) =>
        swapped ? (rightDown, leftDown) : (leftDown, rightDown);

    /// <summary>A click starts when a button goes down: ringed once, however long it is held.</summary>
    public static (bool Primary, bool Secondary) Pressed((bool Primary, bool Secondary) before, (bool Primary, bool Secondary) now) =>
        (now.Primary && !before.Primary, now.Secondary && !before.Secondary);

    /// <summary>Where the pointer is, as a fraction of <paramref name="area"/> (the content on the presenter's screen).</summary>
    public static PointF Fraction(Point pointer, Rectangle area) =>
        area.Width <= 0 || area.Height <= 0 ? new PointF(float.NaN, float.NaN)
            : new PointF((pointer.X - area.X) / (float)area.Width, (pointer.Y - area.Y) / (float)area.Height);
}
