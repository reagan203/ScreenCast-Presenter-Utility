using System.Globalization;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Blank with a countdown (FR-66): the TV shows the blank message and "Back in 4:59". When it ends
/// the TV says the break is over but stays blank: what is shown may have changed during the break,
/// so only the presenter brings it back.
/// </summary>
internal static class BlankCountdown
{
    public static IReadOnlyList<int> Minutes { get; } = [1, 2, 5, 10, 15, 20];
    public const int DefaultMinutes = 5;

    /// <summary>The nearest offered length (a hand-edited settings file may hold anything).</summary>
    public static int Normalize(int minutes) => Minutes.MinBy(m => Math.Abs(m - minutes));

    /// <summary>"4:59", "0:07", "1:00:00". Rounded up, so it never says 0:00 before the end.</summary>
    public static string Clock(TimeSpan left)
    {
        long seconds = left <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(left.TotalSeconds - 1e-9);
        return seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>What the TV shows: the blank message, and the time left on a second line.</summary>
    public static string Text(string message, TimeSpan left) =>
        left > TimeSpan.Zero ? $"{message}\nBack in {Clock(left)}" : $"{message}\nStarting again in a moment";
}

internal enum TalkAlert { None, TwoMinutesLeft, TimeUp }

/// <summary>
/// How long you've been presenting (FR-67), in the now-showing bar on your own screen, never on the
/// TV. It starts when showing starts, carries on through switches and blanks, and resets when
/// showing stops. With a talk length set, it warns once two minutes before the end and once at it.
/// </summary>
internal sealed class TalkTimer
{
    public static IReadOnlyList<int> Lengths { get; } = [0, 10, 15, 20, 30, 45, 60, 90];

    private bool _warnedSoon, _warnedUp;

    public DateTime? Started { get; private set; }

    public static int Normalize(int minutes) => Lengths.MinBy(m => Math.Abs(m - minutes));

    /// <summary>Starts counting, unless it already is.</summary>
    public void Start(DateTime now) => Started ??= now;

    /// <summary>From zero again (stopped showing, or "Restart the talk timer").</summary>
    public void Reset()
    {
        Started = null;
        _warnedSoon = _warnedUp = false;
    }

    public TimeSpan Elapsed(DateTime now) => Started is { } start && now > start ? now - start : TimeSpan.Zero;

    /// <summary>"12:04", or "12:04 of 20:00" with a talk length.</summary>
    public string Text(DateTime now, int lengthMinutes)
    {
        string elapsed = BlankCountdown.Clock(TimeSpan.FromSeconds(Math.Floor(Elapsed(now).TotalSeconds)));
        return lengthMinutes > 0 ? $"{elapsed} of {BlankCountdown.Clock(TimeSpan.FromMinutes(lengthMinutes))}" : elapsed;
    }

    /// <summary>A warning due now, each given once: two minutes left (talks of five minutes or more), then time's up.</summary>
    public TalkAlert Check(DateTime now, int lengthMinutes)
    {
        if (Started is null || lengthMinutes <= 0) return TalkAlert.None;
        TimeSpan length = TimeSpan.FromMinutes(lengthMinutes), elapsed = Elapsed(now);
        if (!_warnedUp && elapsed >= length)
        {
            _warnedUp = _warnedSoon = true;
            return TalkAlert.TimeUp;
        }
        if (!_warnedSoon && lengthMinutes >= 5 && elapsed >= length - TimeSpan.FromMinutes(2))
        {
            _warnedSoon = true;
            return TalkAlert.TwoMinutesLeft;
        }
        return TalkAlert.None;
    }
}
