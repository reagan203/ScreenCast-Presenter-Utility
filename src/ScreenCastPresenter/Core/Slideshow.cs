namespace ScreenCastPresenter.Core;

/// <summary>
/// Favourites one after another (FR-69), for a lobby or waiting-room TV: each shows for a while,
/// switching through black. Favourites that can't be shown right now (the app isn't open, the
/// screen isn't connected) are skipped. Choosing anything else, or stopping, ends it; it waits
/// while the TV is blank, frozen or held black.
/// </summary>
internal static class Slideshow
{
    public static IReadOnlyList<int> Seconds { get; } = [10, 15, 30, 60, 120, 300];
    public const int DefaultSeconds = 15;

    public static int Normalize(int seconds) => Seconds.MinBy(s => Math.Abs(s - seconds));

    /// <summary>
    /// The favourite slot (1–9) to show after <paramref name="current"/>, going round, skipping
    /// empty slots and ones <paramref name="available"/> says can't be shown. Null when none can.
    /// From nothing (<paramref name="current"/> null) it starts at the first.
    /// </summary>
    public static int? Next(IReadOnlyList<Favourite?> favourites, int? current, Func<Favourite, bool> available)
    {
        int count = favourites.Count;
        if (count == 0) return null;
        int start = current is { } c && c >= 1 && c <= count ? c : 0; // slot numbers are 1-based
        for (int step = 1; step <= count; step++)
        {
            int slot = (start + step - 1) % count + 1;
            if (favourites[slot - 1] is { } f && available(f)) return slot;
        }
        return null;
    }

    /// <summary>"10 seconds", "1 minute", "5 minutes".</summary>
    public static string Words(int seconds) =>
        seconds < 60 ? $"{seconds} seconds" : seconds == 60 ? "1 minute" : $"{seconds / 60} minutes";
}

/// <summary>
/// Notices a TV or projector being plugged in (FR-70), so SCP can offer to show something on it.
/// Screens connected when SCP starts are known already. A screen counts as new when it wasn't
/// there before, or comes back after being away a while: Windows reuses names like "Screen 2",
/// so the desk monitor unplugged this morning and the meeting-room TV plugged in now share one.
/// A cable that flickers (or a screen waking up) isn't an arrival.
/// </summary>
internal sealed class ScreenArrivals
{
    public static readonly TimeSpan AwayLongEnough = TimeSpan.FromSeconds(30);

    private HashSet<string> _connected;
    private readonly Dictionary<string, DateTime> _goneAt = new(StringComparer.OrdinalIgnoreCase);

    public ScreenArrivals(IEnumerable<string> connectedAtStart) =>
        _connected = new HashSet<string>(connectedAtStart, StringComparer.OrdinalIgnoreCase);

    /// <summary>The screens that have just arrived, given those connected now.</summary>
    public List<string> Update(IEnumerable<string> connectedNow, DateTime now)
    {
        var current = new HashSet<string>(connectedNow, StringComparer.OrdinalIgnoreCase);
        foreach (string gone in _connected.Where(d => !current.Contains(d))) _goneAt[gone] = now;
        var arrived = new List<string>();
        foreach (string device in current.Where(d => !_connected.Contains(d)))
        {
            bool backTooSoon = _goneAt.TryGetValue(device, out DateTime left) && now - left < AwayLongEnough;
            if (!backTooSoon) arrived.Add(device);
            _goneAt.Remove(device);
        }
        _connected = current;
        return arrived;
    }

    /// <summary>A screen that is already being dealt with (made separate, chosen) shouldn't be offered as well.</summary>
    public void Know(string device) => _connected.Add(device);
}
