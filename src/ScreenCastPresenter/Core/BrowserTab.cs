using System.Text.RegularExpressions;

namespace ScreenCastPresenter.Core;

/// <summary>
/// "Keep to the page you chose" for browsers. Windows can only show a whole browser window, and
/// a browser window shows whichever tab is active, so switching tabs (or going to another page)
/// would show the new page to the audience. The window's title says which page is showing, so
/// when it stops matching the page chosen, the picture cuts to black until it matches again.
/// </summary>
internal static partial class BrowserTab
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "chromium", "thorium",
        "librewolf", "waterfox", "floorp", "zen", "whale", "browser", "iexplore", "duckduckgo", "comet",
    };

    /// <summary>The app is a web browser (by its process name, e.g. "chrome", "msedge").</summary>
    public static bool IsBrowser(string? appName) =>
        appName is { Length: > 0 } && Browsers.Contains(Path.GetFileNameWithoutExtension(appName));

    /// <summary>
    /// The title as it identifies the page: without unread counters that come and go on the same
    /// page ("(3) Inbox", "• Chat", "* Draft") and without spacing differences.
    /// </summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string t = title.Trim();
        for (int i = 0; i < 3; i++) t = Counter().Replace(t, "").Trim();
        return Spaces().Replace(t, " ");
    }

    /// <summary>The window still shows the page that was chosen.</summary>
    public static bool SamePage(string? chosen, string? now) =>
        Normalize(chosen) is { Length: > 0 } a && string.Equals(a, Normalize(now), StringComparison.Ordinal);

    [GeneratedRegex(@"^(\(\d+\+?\)|\[\d+\+?\]|[•●*]+)\s*")]
    private static partial Regex Counter();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
