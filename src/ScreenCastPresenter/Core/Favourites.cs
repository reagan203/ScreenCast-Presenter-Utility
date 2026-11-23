using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Core;

/// <summary>Finds the open window that best matches a saved favourite (FR-23).</summary>
internal static class FavouriteMatcher
{
    private static readonly string[] TitleSeparators = [" - ", " — ", " – ", " | "];

    public static Favourite FromSource(SourceWindow source) =>
        new() { AppName = source.AppName, Title = source.Title, ScreenDevice = source.Screen?.DeviceName };

    /// <summary>
    /// Best match among <paramref name="windows"/> (z-order, most recent first):
    /// 1. same app and exact title;
    /// 2. same app and a similar title (titles change: "Deck.pptx - PowerPoint" → "Deck.pptx [Saved] - PowerPoint");
    /// 3. the most recently used window of the same app;
    /// otherwise null (the app isn't running).
    /// </summary>
    public static SourceWindow? FindBest(Favourite favourite, IReadOnlyList<SourceWindow> windows)
    {
        SourceWindow? sameAppMostRecent = null;
        SourceWindow? similar = null;

        foreach (SourceWindow w in windows)
        {
            if (!string.Equals(w.AppName, favourite.AppName, StringComparison.OrdinalIgnoreCase)) continue;
            if (w.Title == favourite.Title) return w;
            similar ??= IsSimilar(w.Title, favourite.Title) ? w : null;
            sameAppMostRecent ??= w;
        }
        return similar ?? sameAppMostRecent;
    }

    internal static bool IsSimilar(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;
        string headA = Head(a), headB = Head(b);
        return headA.Length >= 3 && string.Equals(headA, headB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The document part of a title without status markers apps add as you work:
    /// "● *Quarterly review.pptx [Saved] - PowerPoint" → "Quarterly review.pptx".
    /// </summary>
    internal static string Head(string title)
    {
        int cut = title.Length;
        foreach (string separator in TitleSeparators)
        {
            int i = title.IndexOf(separator, StringComparison.Ordinal);
            if (i > 0 && i < cut) cut = i;
        }
        string head = title[..cut];

        // Drop "[Saved]", "[Read-Only]", "[Compatibility Mode]" and similar.
        var sb = new System.Text.StringBuilder(head.Length);
        int depth = 0;
        foreach (char c in head)
        {
            if (c == '[') depth++;
            else if (c == ']' && depth > 0) depth--;
            else if (depth == 0) sb.Append(c);
        }
        // Unsaved-changes markers at either end: "*", "●", "•".
        return sb.ToString().Trim(' ', '\t', '*', '●', '•');
    }
}

/// <summary>Picks which monitor the output goes to.</summary>
internal static class OutputChooser
{
    /// <summary>
    /// The saved output screen if it's connected; otherwise the first screen that isn't the main
    /// one (normally the TV); otherwise the main screen.
    /// </summary>
    public static DisplayMonitor? Choose(IReadOnlyList<DisplayMonitor> monitors, string? preferredDevice)
    {
        if (monitors.Count == 0) return null;
        if (preferredDevice is not null && monitors.FirstOrDefault(m => m.DeviceName == preferredDevice) is { } preferred)
            return preferred;
        return monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors[0];
    }

    /// <summary>The next screen after <paramref name="current"/>, wrapping around.</summary>
    public static DisplayMonitor? Next(IReadOnlyList<DisplayMonitor> monitors, DisplayMonitor? current)
    {
        if (monitors.Count == 0) return null;
        int index = current is null ? -1 : monitors.ToList().FindIndex(m => m.IsSameScreen(current));
        return monitors[(index + 1) % monitors.Count];
    }

    /// <summary>
    /// Where SCP's own UI (badge, picker) goes: the screen with the mouse unless that's the
    /// output, then the main screen, then any non-output screen; the output screen only if it's
    /// the only one.
    /// </summary>
    public static DisplayMonitor? PresenterScreen(IReadOnlyList<DisplayMonitor> monitors, DisplayMonitor? output, DisplayMonitor? underMouse)
    {
        if (monitors.Count == 0) return null;
        bool IsOutput(DisplayMonitor m) => output is not null && m.IsSameScreen(output);
        if (underMouse is not null && !IsOutput(underMouse) && monitors.Any(m => m.IsSameScreen(underMouse)))
            return monitors.First(m => m.IsSameScreen(underMouse));
        return monitors.FirstOrDefault(m => m.IsPrimary && !IsOutput(m))
            ?? monitors.FirstOrDefault(m => !IsOutput(m))
            ?? monitors[0];
    }

    /// <summary>Stable order for "next/previous window": by app, then title, then handle.</summary>
    public static SourceWindow? Cycle(IReadOnlyList<SourceWindow> windows, SourceWindow? current, int direction)
    {
        if (windows.Count == 0) return null;
        var ordered = windows
            .OrderBy(w => w.AppName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Handle)
            .ToList();
        int index = current is null ? -1 : ordered.FindIndex(w => w.Handle == current.Handle);
        if (index < 0) return direction >= 0 ? ordered[0] : ordered[^1];
        int next = ((index + direction) % ordered.Count + ordered.Count) % ordered.Count;
        return ordered[next];
    }
}
