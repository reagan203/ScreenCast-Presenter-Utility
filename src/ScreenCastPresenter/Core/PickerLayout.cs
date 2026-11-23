namespace ScreenCastPresenter.Core;

/// <summary>Grid maths for the picker, kept pure so scrolling and clipping are unit-tested.</summary>
internal static class PickerLayout
{
    /// <summary>How many cards of at least <paramref name="minCardWidth"/> fit across.</summary>
    public static int Columns(int width, int minCardWidth, int gap) =>
        Math.Max(1, (width + gap) / Math.Max(1, minCardWidth + gap));

    /// <summary>Card rectangles in content coordinates (before scrolling).</summary>
    public static List<Rectangle> Grid(int count, int width, int columns, int gap, int cardHeight)
    {
        var cards = new List<Rectangle>(count);
        if (count <= 0 || columns <= 0 || width <= 0) return cards;
        int cardWidth = Math.Max(1, (width - gap * (columns - 1)) / columns);
        for (int i = 0; i < count; i++)
        {
            int row = i / columns, col = i % columns;
            cards.Add(new Rectangle(col * (cardWidth + gap), row * (cardHeight + gap), cardWidth, cardHeight));
        }
        return cards;
    }

    /// <summary>Total content height for <paramref name="count"/> cards.</summary>
    public static int ContentHeight(int count, int columns, int gap, int cardHeight)
    {
        if (count <= 0) return 0;
        int rows = (count + columns - 1) / columns;
        return rows * cardHeight + (rows - 1) * gap;
    }

    /// <summary>
    /// A DWM thumbnail is always drawn on top of the picker, so a card scrolled half out of the
    /// grid would paint over the search box. This crops it: the visible part of the destination,
    /// and the matching part of the source window (in source client pixels). Null if nothing shows.
    /// </summary>
    public static (Rectangle Destination, Rectangle Source)? Clip(Rectangle destination, Rectangle viewport, Size sourceSize)
    {
        if (destination.Width <= 0 || destination.Height <= 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0) return null;
        Rectangle visible = Rectangle.Intersect(destination, viewport);
        if (visible.Width <= 0 || visible.Height <= 0) return null;
        if (visible == destination) return (destination, new Rectangle(Point.Empty, sourceSize));

        double sx = (double)sourceSize.Width / destination.Width;
        double sy = (double)sourceSize.Height / destination.Height;
        int left = (int)Math.Round((visible.Left - destination.Left) * sx);
        int top = (int)Math.Round((visible.Top - destination.Top) * sy);
        int right = (int)Math.Round((visible.Right - destination.Left) * sx);
        int bottom = (int)Math.Round((visible.Bottom - destination.Top) * sy);
        var source = Rectangle.FromLTRB(left, top, Math.Max(right, left + 1), Math.Max(bottom, top + 1));
        return (visible, source);
    }

    /// <summary>New selection after an arrow/Home/End/Page key; clamps at the edges.</summary>
    public static int Move(int index, int count, int columns, int rowsPerPage, Keys key)
    {
        if (count <= 0) return -1;
        if (index < 0) return 0;
        int next = key switch
        {
            Keys.Left => index - 1,
            Keys.Right => index + 1,
            Keys.Up => index - columns,
            Keys.Down => index + columns,
            Keys.PageUp => index - columns * Math.Max(1, rowsPerPage),
            Keys.PageDown => index + columns * Math.Max(1, rowsPerPage),
            Keys.Home => 0,
            Keys.End => count - 1,
            _ => index,
        };
        // Down from the last full row lands on the last card rather than doing nothing.
        if (key is Keys.Down or Keys.PageDown && next >= count) next = count - 1;
        if (key == Keys.Up && next < 0) next = index;                 // top row: stay put
        if (key == Keys.PageUp && next < 0) next = index % columns;    // same column, first row
        return Math.Clamp(next, 0, count - 1);
    }

    /// <summary>Scroll offset that brings <paramref name="card"/> fully into a viewport of <paramref name="viewportHeight"/>.</summary>
    public static int ScrollToShow(Rectangle card, int scroll, int viewportHeight)
    {
        if (card.Top < scroll) return card.Top;
        if (card.Bottom > scroll + viewportHeight) return card.Bottom - viewportHeight;
        return scroll;
    }

    /// <summary>
    /// Search (FR-03): every word must appear in the title or app name, ignoring case and accents.
    /// Order: title starts with the query, then other title matches, then app-name matches; ties keep z-order.
    /// </summary>
    public static List<T> Filter<T>(IReadOnlyList<T> items, string query, Func<T, string> title, Func<T, string> app)
    {
        string[] words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [.. items];

        string q = string.Join(' ', words);
        var scored = new List<(T Item, int Score, int Order)>();
        for (int i = 0; i < items.Count; i++)
        {
            string t = Fold(title(items[i])), a = Fold(app(items[i]));
            if (!words.All(w => t.Contains(w, StringComparison.Ordinal) || a.Contains(w, StringComparison.Ordinal))) continue;
            int score = t.StartsWith(q, StringComparison.Ordinal) ? 0 : words.All(w => t.Contains(w, StringComparison.Ordinal)) ? 1 : 2;
            scored.Add((items[i], score, i));
        }
        return [.. scored.OrderBy(s => s.Score).ThenBy(s => s.Order).Select(s => s.Item)];
    }

    // The app runs with InvariantGlobalization (memory budget), where string.Normalize can't strip
    // accents, so Latin letters are folded with a small table instead.
    private static readonly Dictionary<char, string> AccentFolds = BuildAccentFolds();

    /// <summary>Lower-case without accents ("Présentation" → "presentation", "Straße" → "strasse").</summary>
    internal static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw);
            if (AccentFolds.TryGetValue(c, out string? plain)) sb.Append(plain);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static Dictionary<char, string> BuildAccentFolds()
    {
        var map = new Dictionary<char, string>();
        void Add(string accented, string plain)
        {
            foreach (char c in accented) map[c] = plain;
        }
        Add("àáâãäåāăą", "a"); Add("çćĉċč", "c"); Add("ďđ", "d"); Add("èéêëēĕėęě", "e");
        Add("ĝğġģ", "g"); Add("ĥħ", "h"); Add("ìíîïĩīĭįı", "i"); Add("ĵ", "j"); Add("ķ", "k");
        Add("ĺļľŀł", "l"); Add("ñńņňŉ", "n"); Add("òóôõöøōŏő", "o"); Add("ŕŗř", "r");
        Add("śŝşšș", "s"); Add("ţťŧț", "t"); Add("ùúûüũūŭůűų", "u"); Add("ŵ", "w"); Add("ýÿŷ", "y");
        Add("źżž", "z"); Add("ß", "ss"); Add("æ", "ae"); Add("œ", "oe"); Add("þ", "th");
        return map;
    }
}
