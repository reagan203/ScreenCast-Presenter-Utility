namespace ScreenCastPresenter.Core;

/// <summary>
/// How long a message stays at the top of the screen. A presenter glances up mid-sentence, so
/// it allows slow reading (about 2.5 words a second) plus a moment to notice it, and more time
/// when there are buttons to decide on. Hovering keeps it open regardless (PresentingBadge).
/// </summary>
internal static class MessageTiming
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Maximum = TimeSpan.FromSeconds(15);

    private const double WordsPerSecond = 2.5, NoticeSeconds = 2, PerButtonSeconds = 1.5;

    public static TimeSpan For(string? text, int buttons = 0)
    {
        int words = string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        double seconds = NoticeSeconds + words / WordsPerSecond + Math.Max(0, buttons) * PerButtonSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, Minimum.TotalSeconds, Maximum.TotalSeconds));
    }
}
