namespace ScreenCastPresenter.Core;

/// <summary>Where a PowerPoint deck is, and its notes for that slide.</summary>
internal sealed record SlideInfo(int Slide, int Slides, string Notes, bool SlideShow);

/// <summary>
/// PowerPoint's own speaker notes (FR-122): while a PowerPoint window is shown, the notes for the
/// slide on the TV join the presenter's notes window, the paired phone and the speaker's screen,
/// following each slide, so Presenter View (which needs a screen of its own) isn't needed. Read from
/// PowerPoint on this PC; they never go to the TV.
/// </summary>
internal static class SlideNotes
{
    /// <summary>PowerPoint's program name.</summary>
    public const string PowerPointApp = "POWERPNT";

    /// <summary>How often PowerPoint is asked where it is, while it's shown and the notes are seen somewhere.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(700);

    public static bool IsPowerPoint(string? appName) => string.Equals(appName, PowerPointApp, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Slide 3 of 12".</summary>
    public static string Title(SlideInfo slide) => slide.Slides > 0 ? $"Slide {slide.Slide} of {slide.Slides}" : $"Slide {slide.Slide}";

    /// <summary>The presenter's own notes and the slide's, together: their own first; either alone as it is.</summary>
    public static string Combine(string own, SlideInfo? slide)
    {
        string mine = own.Trim();
        string theirs = slide is null ? "" : Tidy(slide.Notes);
        if (theirs.Length == 0) return mine;
        string fromSlide = $"{Title(slide!)}: {theirs}";
        string all = mine.Length == 0 ? fromSlide : mine + Environment.NewLine + Environment.NewLine + fromSlide;
        return all.Length <= NotesCipher.MaxLength ? all : all[..NotesCipher.MaxLength];
    }

    /// <summary>PowerPoint's text tidied: its paragraph marks as line breaks, control characters gone, trimmed.</summary>
    public static string Tidy(string? text) =>
        new string([.. (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\v', '\n').Select(c => c == '\n' || !char.IsControl(c) ? c : ' ')]).Trim();
}
