namespace ScreenCastPresenter.Core;

/// <summary>
/// Follow me (FR-119): the TV shows whichever window the presenter switches to, but only windows
/// of the apps on a list they chose (PowerPoint, a browser, an editor), never a never-show app and
/// never anything else. Switching to anything not on the list keeps the TV as it is, so working
/// privately in email between slides never reaches the room.
/// </summary>
internal static class FollowMe
{
    public const int MaxApps = 20;

    /// <summary>How long a window must stay in front before the TV follows: Alt+Tab passing through other windows doesn't switch.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// Whether the TV should switch to <paramref name="active"/> (the window now in front; null when
    /// it can't be shown, like the desktop, the taskbar or a dialog box).
    /// </summary>
    public static bool ShouldFollow(string? activeApp, nint activeHandle, bool activeMinimised, nint shownHandle, IReadOnlyCollection<string> apps, IReadOnlyCollection<string> neverShow)
    {
        if (activeHandle == 0 || activeMinimised || activeHandle == shownHandle) return false;
        if (activeApp is not { Length: > 0 } || NeverShow.IsBlocked(neverShow, activeApp)) return false;
        return apps.Any(a => string.Equals(a, activeApp, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The list tidied: trimmed, no blanks or repeats (ignoring case), at most <see cref="MaxApps"/>, never a never-show app.</summary>
    public static List<string> Normalize(IEnumerable<string?>? apps, IReadOnlyCollection<string>? neverShow = null)
    {
        var result = new List<string>();
        foreach (string? app in apps ?? [])
        {
            string name = (app ?? "").Trim();
            if (name.Length == 0 || name.Length > NeverShow.MaxNameLength) continue;
            if (neverShow is not null && NeverShow.IsBlocked(neverShow, name)) continue;
            if (result.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(name);
            if (result.Count == MaxApps) break;
        }
        return result;
    }
}
