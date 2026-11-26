namespace ScreenCastPresenter.Core;

/// <summary>How split screen arranges its pictures (FR-104).</summary>
internal enum SplitLayout
{
    /// <summary>Side by side, in columns.</summary>
    SideBySide,

    /// <summary>One above the other, in rows (wide pictures, like spreadsheets).</summary>
    Stacked,

    /// <summary>Two by two.</summary>
    Grid,

    /// <summary>The main picture large on the left, the others small down the right.</summary>
    Focus,
}

/// <summary>
/// Split screen (FR-104): the main picture and up to three more (windows, screens, cameras) on the
/// TV at once, to compare two versions or show a document beside a camera. Pure layout rules.
/// </summary>
internal static class SplitScreen
{
    /// <summary>Pictures besides the main one.</summary>
    public const int MaxOthers = 3;

    /// <summary>The gap between pictures, as a part of the TV's height.</summary>
    public const float Gap = 0.01f;

    /// <summary>In <see cref="SplitLayout.Focus"/>, the main picture's share of the width.</summary>
    public const float FocusShare = 2f / 3;

    public static IReadOnlyList<SplitLayout> All { get; } = [SplitLayout.SideBySide, SplitLayout.Stacked, SplitLayout.Grid, SplitLayout.Focus];

    public static SplitLayout Parse(string? text) =>
        Enum.TryParse(text?.Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out SplitLayout l) && Enum.IsDefined(l) ? l : SplitLayout.SideBySide;

    public static SplitLayout Next(SplitLayout layout) => All[(All.ToList().IndexOf(layout) + 1) % All.Count];

    public static string Words(SplitLayout layout) => layout switch
    {
        SplitLayout.Stacked => "one above the other",
        SplitLayout.Grid => "in a grid",
        SplitLayout.Focus => "large and small",
        _ => "side by side",
    };

    /// <summary>
    /// Where each of <paramref name="pictures"/> pictures goes on a canvas (the main one first),
    /// with a gap between them and round the edge. 1 picture fills the canvas.
    /// </summary>
    public static List<RectangleF> Cells(SizeF canvas, SplitLayout layout, int pictures)
    {
        pictures = Math.Clamp(pictures, 1, MaxOthers + 1);
        var cells = new List<RectangleF>(pictures);
        if (canvas.Width <= 0 || canvas.Height <= 0) return cells;
        if (pictures == 1)
        {
            cells.Add(new RectangleF(0, 0, canvas.Width, canvas.Height));
            return cells;
        }
        float gap = Math.Max(2f, canvas.Height * Gap);
        var inner = new RectangleF(gap, gap, canvas.Width - 2 * gap, canvas.Height - 2 * gap);
        switch (layout)
        {
            case SplitLayout.Stacked:
                Rows(inner, pictures, gap, cells);
                break;
            case SplitLayout.Grid when pictures > 2:
            {
                float h = (inner.Height - gap) / 2, w = (inner.Width - gap) / 2;
                cells.Add(new RectangleF(inner.X, inner.Y, w, h));
                cells.Add(new RectangleF(inner.X + w + gap, inner.Y, w, h));
                if (pictures == 3) cells.Add(new RectangleF(inner.X + (inner.Width - w) / 2, inner.Y + h + gap, w, h)); // the third centred below
                else
                {
                    cells.Add(new RectangleF(inner.X, inner.Y + h + gap, w, h));
                    cells.Add(new RectangleF(inner.X + w + gap, inner.Y + h + gap, w, h));
                }
                break;
            }
            case SplitLayout.Focus:
            {
                float main = (inner.Width - gap) * FocusShare;
                cells.Add(new RectangleF(inner.X, inner.Y, main, inner.Height));
                var side = new RectangleF(inner.X + main + gap, inner.Y, inner.Width - main - gap, inner.Height);
                var rest = new List<RectangleF>();
                Rows(side, pictures - 1, gap, rest);
                cells.AddRange(rest);
                break;
            }
            default: // side by side, and a grid of two
            {
                float w = (inner.Width - gap * (pictures - 1)) / pictures;
                for (int i = 0; i < pictures; i++) cells.Add(new RectangleF(inner.X + i * (w + gap), inner.Y, w, inner.Height));
                break;
            }
        }
        return cells;
    }

    private static void Rows(RectangleF area, int count, float gap, List<RectangleF> into)
    {
        float h = (area.Height - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++) into.Add(new RectangleF(area.X, area.Y + i * (h + gap), area.Width, h));
    }

    /// <summary>A picture of <paramref name="content"/> fitted into <paramref name="cell"/>, centred, its shape kept.</summary>
    public static RectangleF Fit(SizeF content, RectangleF cell)
    {
        if (content.Width <= 0 || content.Height <= 0 || cell.Width <= 0 || cell.Height <= 0) return RectangleF.Empty;
        float scale = Math.Min(cell.Width / content.Width, cell.Height / content.Height);
        float w = content.Width * scale, h = content.Height * scale;
        return new RectangleF(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h);
    }

    /// <summary>The best layout for pictures this shape: stacked for very wide ones, side by side otherwise, a grid for four.</summary>
    public static SplitLayout Suggest(IReadOnlyList<SizeF> sizes)
    {
        if (sizes.Count >= 4) return SplitLayout.Grid;
        bool allWide = sizes.Count > 0 && sizes.All(s => s.Height > 0 && s.Width / s.Height > 2.2f);
        return allWide ? SplitLayout.Stacked : SplitLayout.SideBySide;
    }
}
