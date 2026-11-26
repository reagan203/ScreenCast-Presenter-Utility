using System.Numerics;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Where the things laid over the picture go on the TV (FR-78 to FR-81, FR-84): pure arithmetic
/// on the canvas size, shared by the GPU drawing and its tests. Sizes follow the canvas height,
/// so they look the same on any TV and at any canvas resolution.
/// </summary>
internal static class Layouts
{
    /// <summary>The gap kept between things and the TV's edges, as a fraction of the height.</summary>
    public const float Margin = 0.04f;

    /// <summary>The QR card's side as a fraction of the height: big enough to scan from a few metres.</summary>
    public const float QrSide = 0.3f;

    /// <summary>
    /// The name banner (FR-79): a lower third on the left, as wide as its words (<paramref name="textWidth"/>,
    /// in canvas pixels) need, never more than 90% of the width.
    /// </summary>
    public static RectangleF Banner(SizeF canvas, bool twoLines, float textWidth)
    {
        float h = canvas.Height;
        float height = h * (twoLines ? 0.135f : 0.09f);
        float width = Math.Clamp(textWidth + h * 0.006f + h * 0.044f, h * 0.3f, canvas.Width * 0.9f);
        return new RectangleF(Math.Min(canvas.Width * 0.04f, canvas.Width * 0.05f), h * 0.93f - height, width, height);
    }

    /// <summary>
    /// Live captions (FR-84): a band of two lines, centred, along the bottom kept above whatever
    /// starts at <paramref name="above"/> (the name banner), or along the top (FR-89).
    /// </summary>
    /// <summary>
    /// A shortcut shown on the TV (FR-101): a chip as wide as its words, centred, just above
    /// <paramref name="above"/> (the banner or bottom captions), never off the top.
    /// </summary>
    public static RectangleF KeyChip(SizeF canvas, float textWidth, float above)
    {
        float h = canvas.Height, pad = h * 0.025f, height = h * 0.075f;
        float width = Math.Min(canvas.Width - 2 * h * Margin, textWidth + 2 * pad);
        float bottom = Math.Max(height + h * Margin, above - h * 0.03f);
        return new RectangleF((canvas.Width - width) / 2, bottom - height, width, height);
    }

    public static RectangleF LiveStrip(SizeF canvas, float above, CaptionLook? look = null)
    {
        look ??= CaptionLook.Default;
        float h = canvas.Height;
        float height = h * look.TextHeight * CaptionLines;
        float width = canvas.Width * 0.8f;
        if (look.Place == CaptionPlace.Top) return new RectangleF((canvas.Width - width) / 2, h * Margin, width, height);
        float bottom = Math.Min(h * 0.95f, above - h * 0.02f);
        return new RectangleF((canvas.Width - width) / 2, Math.Max(0, bottom - height), width, height);
    }

    /// <summary>The captions band's height in lines of text (two lines and padding).</summary>
    public const float CaptionLines = 3.1f;

    /// <summary>
    /// The QR card (FR-81) in <paramref name="corner"/>. A bottom card is kept above
    /// <paramref name="bottomLimit"/> (the banner or the captions), so nothing covers it.
    /// </summary>
    public static RectangleF QrCard(SizeF canvas, InsetCorner corner, float bottomLimit, float topLimit = 0)
    {
        float h = canvas.Height;
        float side = Math.Min(h * QrSide, canvas.Width * 0.45f), margin = h * Margin;
        bool right = corner is InsetCorner.TopRight or InsetCorner.BottomRight;
        bool bottom = corner is InsetCorner.BottomLeft or InsetCorner.BottomRight;
        float x = right ? canvas.Width - margin - side : margin;
        float labelRoom = h * 0.056f; // a bottom card's label goes above it
        float y = bottom ? Math.Min(h - margin, bottomLimit - margin) - side : Math.Max(margin, topLimit + margin);
        if (bottom && y - labelRoom < margin) y = margin + labelRoom;
        return new RectangleF(x, y, side, side);
    }

    /// <summary>
    /// A QR code's module size on the canvas: whole pixels so its squares stay crisp when the TV
    /// stretches the canvas, and four modules of white round it (the quiet zone readers need).
    /// </summary>
    public static float QrModule(float cardSide, int modules) => Math.Max(1, MathF.Floor(cardSide / (modules + 8)));

    /// <summary>An arrow head's two lines run from its tip to these two points.</summary>
    public readonly record struct ArrowHeadLines(Vector2 Tip, Vector2 Left, Vector2 Right);

    /// <summary>The half-angle between the shaft and each head line: about 28°.</summary>
    public const float ArrowHeadAngle = 0.5f;

    /// <summary>
    /// An arrow's head at <paramref name="tip"/>, pointing away from <paramref name="from"/>; null for
    /// a dot. A value, not a list: it's drawn every frame.
    /// </summary>
    public static ArrowHeadLines? ArrowHead(Vector2 from, Vector2 tip, float length)
    {
        Vector2 back = from - tip;
        float len = back.Length();
        if (len < 0.5f || !float.IsFinite(len)) return null;
        back /= len;
        length = Math.Min(length, len * 0.6f); // a short arrow keeps a small head
        Vector2 left = Vector2.Transform(back, Matrix3x2.CreateRotation(ArrowHeadAngle));
        Vector2 right = Vector2.Transform(back, Matrix3x2.CreateRotation(-ArrowHeadAngle));
        return new ArrowHeadLines(tip, tip + left * length, tip + right * length);
    }

    /// <summary>
    /// Your logo on the blank screen (FR-80): centred, at most 40% of the width and 30% of the
    /// height, never blown up past twice its own size; above the message when there is one.
    /// </summary>
    public static RectangleF Logo(SizeF canvas, SizeF image, bool withMessage)
    {
        if (image.Width <= 0 || image.Height <= 0 || canvas.Width <= 0 || canvas.Height <= 0) return RectangleF.Empty;
        float scale = Math.Min(Math.Min(canvas.Width * 0.4f / image.Width, canvas.Height * 0.3f / image.Height), 2f);
        float w = image.Width * scale, h = image.Height * scale;
        float centreY = withMessage ? canvas.Height * 0.4f : canvas.Height / 2;
        return new RectangleF((canvas.Width - w) / 2, centreY - h / 2, w, h);
    }

    /// <summary>Where the blank screen's message goes: the whole canvas, or under the logo.</summary>
    public static RectangleF Message(SizeF canvas, RectangleF logo) =>
        logo.IsEmpty ? new RectangleF(0, 0, canvas.Width, canvas.Height)
        : new RectangleF(0, logo.Bottom + canvas.Height * 0.04f, canvas.Width, Math.Max(1, canvas.Height * 0.96f - logo.Bottom - canvas.Height * 0.04f));
}
