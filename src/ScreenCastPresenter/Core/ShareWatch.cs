namespace ScreenCastPresenter.Core;

/// <summary>A window as the share watch sees it: its app, where it is, and the styles of a border drawn around a shared screen.</summary>
internal sealed record ShareFacts(nint Handle, string AppName, Rectangle Bounds, bool Layered, bool ClickThrough, bool Topmost);

/// <summary>A screen the share watch knows: its device name, its name ("Screen 1"), where it is, and whether sharing it is safe.</summary>
/// <param name="Safe">Only the chosen source is on it: the Zoom screen, or a TV showing ScreenCast Presenter full screen.</param>
internal sealed record WatchedScreen(string Device, string Name, Rectangle Bounds, bool Safe);

/// <summary>A meeting app sharing all of a screen that isn't safe to share.</summary>
internal sealed record WholeScreenShare(string App, string Device, string ScreenName);

/// <summary>
/// "You're sharing your whole screen" (FR-134). Meeting apps mark a shared screen with a border
/// round its edge: a see-through, click-through window on top of everything, as big as the
/// screen (Zoom's green border, Teams' red one). When such a border is round a screen that isn't
/// the Zoom screen (or a TV showing only the chosen source), the meeting sees everything on that
/// screen, not just what's shown, so the presenter is told. It's a guess from the outside: it can
/// miss a meeting app that draws no border (a browser), and a newer app version may draw it
/// differently. Nothing is ever changed in the meeting app.
/// </summary>
internal static class ShareWatch
{
    /// <summary>Program names of meeting apps (ignoring case) and the names people know them by.</summary>
    public static IReadOnlyDictionary<string, string> MeetingApps { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Zoom"] = "Zoom",
        ["ms-teams"] = "Teams",
        ["Teams"] = "Teams",
        ["CiscoCollabHost"] = "Webex",
        ["webexmta"] = "Webex",
        ["atmgr"] = "Webex",
        ["slack"] = "Slack",
        ["Discord"] = "Discord",
        ["g2mcomm"] = "GoTo Meeting",
        ["GoTo"] = "GoTo Meeting",
        ["RingCentral"] = "RingCentral",
    };

    /// <summary>A border's edges may be this many pixels inside or outside the screen's.</summary>
    public const int Slack = 24;

    /// <summary>The name people know a meeting app by, or null when <paramref name="appName"/> isn't one.</summary>
    public static string? MeetingName(string? appName) => appName is { Length: > 0 } && MeetingApps.TryGetValue(appName, out string? name) ? name : null;

    /// <summary>Every meeting app sharing all of a screen that isn't safe to share (one per app and screen).</summary>
    public static List<WholeScreenShare> Find(IEnumerable<ShareFacts> windows, IReadOnlyList<WatchedScreen> screens)
    {
        var found = new List<WholeScreenShare>();
        foreach (ShareFacts w in windows)
        {
            if (MeetingName(w.AppName) is not { } app || !LooksLikeBorder(w)) continue;
            foreach (WatchedScreen screen in screens)
            {
                if (screen.Safe || !Around(w.Bounds, screen.Bounds)) continue;
                if (!found.Any(f => f.App == app && f.Device == screen.Device)) found.Add(new WholeScreenShare(app, screen.Device, screen.Name));
            }
        }
        return found;
    }

    /// <summary>A border round a shared screen: see-through and click-through, kept on top.</summary>
    public static bool LooksLikeBorder(ShareFacts w) => w.Layered && w.ClickThrough && w.Topmost;

    /// <summary><paramref name="border"/> follows <paramref name="screen"/>'s edges, within <see cref="Slack"/> pixels on every side.</summary>
    public static bool Around(Rectangle border, Rectangle screen) =>
        screen.Width > 0 && screen.Height > 0
        && Math.Abs(border.Left - screen.Left) <= Slack && Math.Abs(border.Top - screen.Top) <= Slack
        && Math.Abs(border.Right - screen.Right) <= Slack && Math.Abs(border.Bottom - screen.Bottom) <= Slack;

    /// <summary>The shares that weren't there last time (so each is said once, while it lasts).</summary>
    public static List<WholeScreenShare> New(IReadOnlyCollection<WholeScreenShare> before, IEnumerable<WholeScreenShare> now) =>
        [.. now.Where(n => !before.Any(b => b.App == n.App && b.Device == n.Device))];
}
