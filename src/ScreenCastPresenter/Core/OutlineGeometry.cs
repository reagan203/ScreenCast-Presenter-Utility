namespace ScreenCastPresenter.Core;

/// <summary>
/// Where to draw the yellow outline (FR-17) around the presented window, as plain rectangles.
/// Pure geometry so every case (maximised, spanning screens, partly on the TV) is unit-tested.
/// </summary>
internal static class OutlineGeometry
{
    /// <param name="source">The window's visible bounds.</param>
    /// <param name="thickness">Line thickness in pixels.</param>
    /// <param name="allowed">Screens the outline may be drawn on (every screen except the output).</param>
    /// <returns>Up to four strips per allowed screen the window touches.</returns>
    /// <remarks>
    /// Each edge goes just outside the window. Where the window touches the edge of the screen
    /// (e.g. maximised) it goes just inside instead, so it stays visible. Where the window
    /// continues onto another screen, no edge is drawn along the cut, and nothing is ever drawn on
    /// a screen that isn't allowed — so the outline never appears on the TV.
    /// </remarks>
    public static List<Rectangle> Strips(Rectangle source, int thickness, IReadOnlyList<Rectangle> allowed)
    {
        var strips = new List<Rectangle>();
        if (source.Width <= 0 || source.Height <= 0 || thickness <= 0) return strips;

        foreach (Rectangle screen in allowed)
        {
            Rectangle part = Rectangle.Intersect(source, screen);
            if (part.Width <= 0 || part.Height <= 0) continue;

            // Which edges of this part are real window edges (not cuts made by the screen border)?
            bool hasLeft = part.Left == source.Left, hasRight = part.Right == source.Right;
            bool hasTop = part.Top == source.Top, hasBottom = part.Bottom == source.Bottom;

            // Outside if there's room on this screen, otherwise inside.
            int outerLeft = hasLeft ? Math.Max(part.Left - thickness, screen.Left) : part.Left;
            int outerRight = hasRight ? Math.Min(part.Right + thickness, screen.Right) : part.Right;
            int outerTop = hasTop ? Math.Max(part.Top - thickness, screen.Top) : part.Top;
            int outerBottom = hasBottom ? Math.Min(part.Bottom + thickness, screen.Bottom) : part.Bottom;

            int innerLeft = hasLeft ? outerLeft + thickness : part.Left;
            int innerRight = hasRight ? outerRight - thickness : part.Right;
            int innerTop = hasTop ? outerTop + thickness : part.Top;
            int innerBottom = hasBottom ? outerBottom - thickness : part.Bottom;

            if (innerLeft >= innerRight || innerTop >= innerBottom)
            {
                // Window smaller than two line widths: just fill it.
                strips.Add(Rectangle.FromLTRB(outerLeft, outerTop, outerRight, outerBottom));
                continue;
            }

            AddIfNotEmpty(strips, Rectangle.FromLTRB(outerLeft, outerTop, outerRight, innerTop));          // top
            AddIfNotEmpty(strips, Rectangle.FromLTRB(outerLeft, innerBottom, outerRight, outerBottom));    // bottom
            AddIfNotEmpty(strips, Rectangle.FromLTRB(outerLeft, innerTop, innerLeft, innerBottom));        // left
            AddIfNotEmpty(strips, Rectangle.FromLTRB(innerRight, innerTop, outerRight, innerBottom));      // right
        }
        return strips;
    }

    /// <summary>
    /// The "Showing on TV" tab: sits on top of the outline at the window's top-left, or just inside
    /// the top edge when there's no room above. Null when the window's top-left area isn't on an
    /// allowed screen.
    /// </summary>
    public static Rectangle? Tab(Rectangle source, Size tabSize, int thickness, int indent, IReadOnlyList<Rectangle> allowed)
    {
        if (source.Width <= 0 || source.Height <= 0) return null;
        foreach (Rectangle screen in allowed)
        {
            Rectangle part = Rectangle.Intersect(source, screen);
            if (part.Width < tabSize.Width + indent || part.Height <= 0 || part.Top != source.Top) continue;

            int left = Math.Min(part.Left + indent, part.Right - tabSize.Width);
            int above = part.Top - thickness - tabSize.Height;
            int top = above >= screen.Top ? above : part.Top + thickness;
            return new Rectangle(left, top, tabSize.Width, tabSize.Height);
        }
        return null;
    }

    private static void AddIfNotEmpty(List<Rectangle> strips, Rectangle r)
    {
        if (r.Width > 0 && r.Height > 0) strips.Add(r);
    }
}
