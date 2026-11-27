namespace ScreenCastPresenter.Core;

/// <summary>
/// A timer on the TV (FR-120), for an exercise or group work while something stays shown: "Exercise
/// 9:59" in a corner of the TV, counting down each second, then "Time's up" for a while. It sits in a
/// corner nothing else uses (not the QR code's, not the small picture's), above the name banner and
/// captions. It shows with the picture or the blank screen's message, never on plain black.
/// </summary>
internal static class ActivityTimer
{
    public static IReadOnlyList<int> Minutes { get; } = [1, 2, 3, 5, 10, 15, 20, 30, 45, 60];
    public const int DefaultMinutes = 5;
    public const int MaxMinutes = 90;
    public const int MaxLabel = 30;
    public const string DefaultLabel = "Time left";

    /// <summary>How long "Time's up" stays on the TV before the timer goes by itself.</summary>
    public static readonly TimeSpan UpFor = TimeSpan.FromSeconds(30);

    public static int Normalize(int minutes) => Math.Clamp(minutes, 1, MaxMinutes);

    /// <summary>The label tidied to one short line; the default when empty.</summary>
    public static string TidyLabel(string? label)
    {
        string tidy = InkStroke.TidyText(label ?? "", MaxLabel).Trim();
        return tidy.Length == 0 ? DefaultLabel : tidy;
    }

    /// <summary>"Exercise 9:59" while it runs; "Time's up" at the end.</summary>
    public static string Text(string label, TimeSpan left) => left > TimeSpan.Zero ? $"{TidyLabel(label)} {BlankCountdown.Clock(left)}" : "Time's up";

    /// <summary>The first corner nothing else uses: top left, top right, bottom left, then bottom right.</summary>
    public static InsetCorner Corner(InsetCorner? qr, InsetCorner? inset)
    {
        foreach (InsetCorner c in new[] { InsetCorner.TopLeft, InsetCorner.TopRight, InsetCorner.BottomLeft, InsetCorner.BottomRight })
            if (c != qr && c != inset) return c;
        return InsetCorner.TopLeft;
    }

    /// <summary>The chip on the TV: as wide as its words, in <paramref name="corner"/>, clear of what's above or below.</summary>
    /// <param name="bottomLimit">The top of the banner or captions (a bottom chip stays above it).</param>
    /// <param name="topLimit">The bottom of top captions (a top chip stays below it).</param>
    public static RectangleF Chip(SizeF canvas, InsetCorner corner, float textWidth, float bottomLimit, float topLimit = 0)
    {
        float h = canvas.Height, pad = h * 0.025f, height = h * 0.08f, margin = h * Layouts.Margin;
        float width = Math.Min(canvas.Width - 2 * margin, Math.Max(height * 2, textWidth + 2 * pad));
        bool right = corner is InsetCorner.TopRight or InsetCorner.BottomRight;
        bool bottom = corner is InsetCorner.BottomLeft or InsetCorner.BottomRight;
        float x = right ? canvas.Width - margin - width : margin;
        float y = bottom ? Math.Min(h - margin, bottomLimit - margin) - height : Math.Max(margin, topLimit + margin);
        return new RectangleF(x, Math.Clamp(y, 0, Math.Max(0, h - height)), width, height);
    }
}
