namespace ScreenCastPresenter.Core;

/// <summary>Which corner of the TV the small picture sits in (FR-72).</summary>
internal enum InsetCorner { BottomRight, BottomLeft, TopRight, TopLeft }

/// <summary>
/// The small picture in a corner of the TV (FR-72), e.g. you on a webcam over your slides. Its
/// width is a share of the TV's width; its height follows what it shows, never more than 60% of
/// the TV's height. A small margin keeps it off the very edge, where some TVs crop.
/// </summary>
internal static class InsetLayout
{
    public static IReadOnlyList<int> Sizes { get; } = [20, 28, 36];
    public const int DefaultSize = 28;
    public const float Margin = 0.03f, MaxHeight = 0.6f;

    public static int Normalize(int percent) => Sizes.MinBy(s => Math.Abs(s - percent));

    public static InsetCorner ParseCorner(string? text) => ParseCorner(text, InsetCorner.BottomRight);

    public static InsetCorner ParseCorner(string? text, InsetCorner fallback) =>
        Enum.TryParse(text, ignoreCase: true, out InsetCorner corner) && Enum.IsDefined(corner) ? corner : fallback;

    /// <summary>Where the small picture goes on a canvas of <paramref name="canvas"/>, for content of <paramref name="content"/>. Empty if either has no size.</summary>
    public static RectangleF Place(SizeF canvas, SizeF content, InsetCorner corner, int percent)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || content.Width <= 0 || content.Height <= 0) return RectangleF.Empty;
        float width = canvas.Width * Normalize(percent) / 100f;
        float height = width * content.Height / content.Width;
        if (height > canvas.Height * MaxHeight)
        {
            height = canvas.Height * MaxHeight;
            width = height * content.Width / content.Height;
        }
        float margin = canvas.Height * Margin;
        bool left = corner is InsetCorner.BottomLeft or InsetCorner.TopLeft;
        bool top = corner is InsetCorner.TopLeft or InsetCorner.TopRight;
        return new RectangleF(left ? margin : canvas.Width - margin - width, top ? margin : canvas.Height - margin - height, width, height);
    }

    /// <summary>"bottom right" for messages.</summary>
    public static string Words(InsetCorner corner) => corner switch
    {
        InsetCorner.BottomLeft => "bottom left",
        InsetCorner.TopRight => "top right",
        InsetCorner.TopLeft => "top left",
        _ => "bottom right",
    };
}
