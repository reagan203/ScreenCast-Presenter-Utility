namespace ScreenCastPresenter.Core;

/// <summary>
/// Show only the web page (FR-133): a browser window shown shows only its page. Its tab strip (the
/// titles of every other tab), address bar, bookmarks, profile name and extensions are left out,
/// none of which the presenter chose to show. The page's area comes from the browser itself (UI
/// Automation's document), is followed as the window changes, and is kept as a crop, so zoom,
/// marks and hidden parts all stay inside it. A crop the presenter chose wins over it.
/// </summary>
internal static class BrowserPage
{
    /// <summary>A page smaller than this share of the window isn't believed (a side panel, a pop-up).</summary>
    public const double MinShare = 0.25;

    /// <summary>A page this close to the whole window is the whole window (full screen, F11): nothing to leave out.</summary>
    public const double WholeShare = 0.985;

    /// <summary>How often the page's area is asked for while a browser is shown.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

    /// <summary>Unsure answers in a row (2 s at <see cref="Every"/>) before the picture stops waiting for the page and shows the whole window.</summary>
    public const int Patience = 4;

    /// <summary>
    /// The crop of the window's client area (<paramref name="client"/>, on screen) that is its page
    /// (<paramref name="document"/>, on screen); null when there's nothing to leave out or the
    /// document can't be believed.
    /// </summary>
    public static CropRegion? Crop(Rectangle client, Rectangle? document) => Read(client, document).Crop;

    /// <summary>
    /// What the browser's answer says: the page's crop (null: the whole window) and whether that's
    /// sure. A page filling the window (full screen) is sure: nothing to leave out. No page at all,
    /// or one under <see cref="MinShare"/> of the window, is unsure: most often it's a page between
    /// two loads (the browser swaps the window it draws the page in), gone again a moment later.
    /// </summary>
    public static (CropRegion? Crop, bool Sure) Read(Rectangle client, Rectangle? document)
    {
        if (document is not { } doc || client.Width <= 0 || client.Height <= 0) return (null, false);
        Rectangle page = Rectangle.Intersect(client, doc);
        if (page.Width <= 0 || page.Height <= 0) return (null, false);
        double share = (double)page.Width * page.Height / ((double)client.Width * client.Height);
        if (share >= WholeShare) return (null, true);
        if (share < MinShare) return (null, false);
        return (CropRegion.FromScreen(page, client), true);
    }

    /// <summary>
    /// The crop to show after an answer, given the one <paramref name="shown"/>. A sure answer is
    /// taken as it is. An unsure one keeps the page that's shown for up to <see cref="Patience"/>
    /// answers in a row, so a link clicked doesn't make the picture jump to the whole window (tabs and
    /// address bar included) and back; after that the whole window shows, as when it can't tell.
    /// </summary>
    public static CropRegion? Steady(CropRegion? shown, (CropRegion? Crop, bool Sure) answer, ref int unsureInARow)
    {
        if (answer.Sure)
        {
            unsureInARow = 0;
            return answer.Crop;
        }
        unsureInARow++;
        return shown is not null && unsureInARow <= Patience ? shown : answer.Crop;
    }

    /// <summary>Two crops close enough (within a couple of pixels on a big window) not to move the picture for.</summary>
    public static bool Same(CropRegion? a, CropRegion? b) =>
        a is null ? b is null : b is not null && Math.Abs(a.X - b.X) < 0.002 && Math.Abs(a.Y - b.Y) < 0.002 && Math.Abs(a.Width - b.Width) < 0.002 && Math.Abs(a.Height - b.Height) < 0.002;
}
