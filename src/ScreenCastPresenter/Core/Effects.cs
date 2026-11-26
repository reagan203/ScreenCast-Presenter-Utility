namespace ScreenCastPresenter.Core;

/// <summary>What a drawing on the TV is made with.</summary>
internal enum InkTool
{
    /// <summary>A solid line.</summary>
    Pen,

    /// <summary>A wide, see-through line, like a highlighter pen.</summary>
    Highlighter,

    /// <summary>A straight arrow from where the drag started to where it ended (FR-78).</summary>
    Arrow,

    /// <summary>A rectangle outline between the drag's corners (FR-78).</summary>
    Box,

    /// <summary>Words placed where you click (FR-78); <see cref="InkStroke.Text"/> holds them.</summary>
    Text,
}

/// <summary>How a hidden part of a window looks on the TV (FR-74).</summary>
internal enum RedactStyle
{
    /// <summary>A solid box: nothing underneath can be made out. The default, and the safest.</summary>
    Solid,

    /// <summary>Heavily blurred, so the shape of the page stays but nothing can be read.</summary>
    Blurred,
}

/// <summary>
/// One line drawn on the TV (FR-71). Points are fractions (0–1) of what is shown's whole area (the
/// window's client area or the screen), so the drawing stays on the same spot of the content when
/// the window moves, the output resizes, or the view is cropped or zoomed. Width is a fraction of
/// that area's height, so it scales with the content too.
/// </summary>
internal sealed class InkStroke(int id, InkTool tool, int argb, float width)
{
    public const int MaxPoints = 4000;

    public const int MaxTextLength = 120;

    public int Id { get; } = id;
    public InkTool Tool { get; } = tool;
    public int Argb { get; } = argb;

    /// <summary>The line's width; for text, the letters' height. A fraction of the content's height.</summary>
    public float Width { get; } = width;
    public List<PointF> Points { get; } = [];

    /// <summary>The words of a <see cref="InkTool.Text"/> mark (one line, at most <see cref="MaxTextLength"/>); null otherwise.</summary>
    public string? Text
    {
        get => _text;
        set => _text = value is null ? null : TidyText(value);
    }

    private string? _text;

    /// <summary>One line, no control characters, not too long (at most <paramref name="max"/> characters, from the start).</summary>
    public static string TidyText(string text, int max = MaxTextLength)
    {
        string flat = new([.. text.Select(c => char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029 ? ' ' : c)]);
        flat = flat.Trim();
        return flat.Length > max ? flat[..max] : flat;
    }

    /// <summary>Arrows and boxes have two points, start and end: dragging moves the end.</summary>
    public bool IsShape => Tool is InkTool.Arrow or InkTool.Box;

    /// <summary>A shape's end moved: it keeps its start and takes the new end.</summary>
    public void SetEnd(PointF end)
    {
        if (!float.IsFinite(end.X) || !float.IsFinite(end.Y)) return;
        if (Points.Count == 0) Points.Add(end);
        else if (Points.Count == 1) Points.Add(end);
        else Points[^1] = end;
    }

    /// <summary>Adds a point unless the line already has <see cref="MaxPoints"/> (a very long scribble stops growing).</summary>
    public bool Add(PointF point)
    {
        if (Points.Count >= MaxPoints || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        if (Points.Count > 0 && Points[^1] == point) return false;
        Points.Add(point);
        return true;
    }
}

/// <summary>Colours and sizes of the marks drawn on the TV, chosen to be seen across a room.</summary>
internal static class EffectStyle
{
    /// <summary>Primary click: the outline yellow. Secondary (right) click: blue.</summary>
    public static readonly Color ClickPrimary = Color.FromArgb(0xFF, 0xC8, 0x3D);
    public static readonly Color ClickSecondary = Color.FromArgb(0x4C, 0x8D, 0xFF);
    public static readonly Color PointerRing = Color.FromArgb(0xFF, 0xC8, 0x3D);
    public static readonly Color Laser = Color.FromArgb(0xFF, 0x3B, 0x30);

    /// <summary>How long a click ring grows and fades, and how long the laser's trail lingers.</summary>
    public static readonly TimeSpan RingLife = TimeSpan.FromMilliseconds(550);
    public static readonly TimeSpan TrailLife = TimeSpan.FromMilliseconds(400);

    /// <summary>Sizes as fractions of the height of the picture on the TV.</summary>
    public const float RingRadius = 0.04f, RingStroke = 0.005f, PointerRadius = 0.022f, LaserRadius = 0.009f;

    /// <summary>The spotlight's lit circle (FR-77), as a fraction of the picture's height; outside it the picture is dimmed.</summary>
    public const float SpotlightRadius = 0.17f;

    /// <summary>How dark the spotlight makes everything outside its circle.</summary>
    public const float SpotlightDim = 0.72f;

    /// <summary>A hidden part's solid box (FR-74): a dark grey, so the audience can tell something is covered.</summary>
    public static readonly Color RedactFill = Color.FromArgb(0x23, 0x23, 0x27);

    /// <summary>
    /// The opaque colour a box of <paramref name="fill"/> has at <paramref name="opacity"/> over
    /// <paramref name="background"/>. Drawn fully opaque, so nothing under it can show through.
    /// </summary>
    public static Color SolidOver(Color background, Color fill, float opacity)
    {
        float t = Math.Clamp(float.IsFinite(opacity) ? opacity : 0, 0, 1);
        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * t);
        return Color.FromArgb(255, Mix(background.R, fill.R), Mix(background.G, fill.G), Mix(background.B, fill.B));
    }

    /// <summary>A click ring at <paramref name="age"/>: grows from a third of its size and fades out. Null once gone.</summary>
    public static (float Radius, float Alpha)? Ring(TimeSpan age)
    {
        if (age < TimeSpan.Zero || age >= RingLife) return null;
        float t = (float)(age / RingLife);
        float eased = 1 - (1 - t) * (1 - t); // fast start, gentle stop
        return (RingRadius * (0.33f + 0.67f * eased), 1 - t);
    }

    /// <summary>A laser trail point at <paramref name="age"/>: fades from full to nothing. Null once gone.</summary>
    public static float? TrailAlpha(TimeSpan age) => age < TimeSpan.Zero || age >= TrailLife ? null : 1 - (float)(age / TrailLife);

    /// <summary>
    /// Maps a point given as a fraction of the content's whole area to the canvas the TV shows:
    /// through the part of the frame that is shown (crop or zoom) into where that part is drawn.
    /// </summary>
    public static PointF Map(PointF fraction, RectangleF area, RectangleF shown, RectangleF dest)
    {
        if (shown.Width <= 0 || shown.Height <= 0) return new PointF(float.NaN, float.NaN);
        float x = area.X + fraction.X * area.Width, y = area.Y + fraction.Y * area.Height;
        return new PointF(dest.X + (x - shown.X) * dest.Width / shown.Width, dest.Y + (y - shown.Y) * dest.Height / shown.Height);
    }
}
