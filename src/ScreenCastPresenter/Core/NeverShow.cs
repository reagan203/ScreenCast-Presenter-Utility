namespace ScreenCastPresenter.Core;

/// <summary>A window on screen as the never-show check sees it: its app and where it is.</summary>
internal sealed record VisibleWindow(nint Handle, string AppName, Rectangle Bounds);

/// <summary>
/// Apps that must never reach the audience (a password manager, banking, email). Their windows
/// can't be chosen, and a whole screen being shown turns black while one of them is open on the
/// part shown. Apps are matched by their program name ("KeePass" for KeePass.exe), ignoring case.
/// </summary>
internal static class NeverShow
{
    public const int MaxApps = 100;
    public const int MaxNameLength = 100;

    /// <summary>
    /// Common password managers, on the list from the start: the user can remove them. Password
    /// managers inside a browser (extensions) are part of the browser window and can't be told apart.
    /// </summary>
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "1Password", "Bitwarden", "KeePass", "KeePassXC", "LastPass", "Dashlane", "KeeperPasswordManager", "NordPass", "RoboForm", "Enpass", "ProtonPass",
    ];

    public static bool IsBlocked(IReadOnlyCollection<string> list, string? appName) =>
        appName is { Length: > 0 } && list.Any(a => string.Equals(a, appName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The first never-show window (front to back) with any part inside <paramref name="shown"/>,
    /// the area of the screen the audience sees. Being covered by other windows doesn't count as
    /// hidden: a window in front may be see-through, and "open on the screen" is a rule people can predict.
    /// </summary>
    public static VisibleWindow? FindOnScreen(IReadOnlyList<VisibleWindow> frontToBack, Rectangle shown, IReadOnlyCollection<string> list)
    {
        if (list.Count == 0 || shown.Width <= 0 || shown.Height <= 0) return null;
        foreach (VisibleWindow w in frontToBack)
        {
            if (w.Bounds.Width <= 0 || w.Bounds.Height <= 0 || !IsBlocked(list, w.AppName)) continue;
            if (w.Bounds.IntersectsWith(shown)) return w;
        }
        return null;
    }

    /// <summary>Tidies a list from a settings file: trimmed, no blanks, no repeats (ignoring case), bounded.</summary>
    public static List<string> Normalize(IEnumerable<string?>? apps)
    {
        var result = new List<string>();
        foreach (string? app in apps ?? [])
        {
            string name = (app ?? "").Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
            if (name.Length == 0 || name.Length > MaxNameLength) continue;
            if (result.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(name);
            if (result.Count == MaxApps) break;
        }
        return result;
    }
}
