namespace ScreenCastPresenter.Core;

/// <summary>The small picture's shape (FR-118).</summary>
internal enum InsetShape
{
    Rectangle,

    /// <summary>Rounded corners.</summary>
    Rounded,

    /// <summary>A circle, like a webcam bubble: the middle of the picture, cut round.</summary>
    Circle,
}

/// <summary>
/// The small picture in a circle or with rounded corners (FR-118), like a webcam bubble in a screen
/// recording. A circle shows the middle square of the picture (a face is usually in the middle);
/// the parts cut away are never shown, so this only ever shows less.
/// </summary>
internal static class InsetShapes
{
    /// <summary>How round rounded corners are: this much of the picture's shorter side.</summary>
    public const float CornerShare = 0.12f;

    public static InsetShape Parse(string? text) => Enum.TryParse(text, ignoreCase: true, out InsetShape s) && Enum.IsDefined(s) ? s : InsetShape.Rectangle;

    /// <summary>The part of the picture shown: all of it, or for a circle the square in its middle.</summary>
    public static Rectangle Shown(Rectangle part, InsetShape shape)
    {
        if (shape != InsetShape.Circle || part.Width <= 0 || part.Height <= 0) return part;
        int side = Math.Min(part.Width, part.Height);
        return new Rectangle(part.X + (part.Width - side) / 2, part.Y + (part.Height - side) / 2, side, side);
    }

    /// <summary>The corner radius for <paramref name="place"/> (zero for a rectangle; half the side for a circle).</summary>
    public static float Radius(RectangleF place, InsetShape shape) => shape switch
    {
        InsetShape.Circle => Math.Min(place.Width, place.Height) / 2,
        InsetShape.Rounded => Math.Min(place.Width, place.Height) * CornerShare,
        _ => 0,
    };

    public static string Words(InsetShape shape) => shape switch
    {
        InsetShape.Circle => "Circle",
        InsetShape.Rounded => "Rounded corners",
        _ => "Rectangle",
    };
}
