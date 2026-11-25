namespace ScreenCastPresenter.Core;

/// <summary>
/// "Text size" in Settings › Appearance: three steps, so every screen has been looked at in each.
/// Anything else (a hand-edited or newer settings file) snaps to the nearest step.
/// </summary>
internal static class TextSize
{
    public const float Normal = 1f, Large = 1.15f, Larger = 1.3f;

    public static IReadOnlyList<(float Scale, string Label)> Choices { get; } =
    [
        (Normal, "Normal"),
        (Large, "Large"),
        (Larger, "Larger"),
    ];

    /// <summary>The nearest allowed step; NaN, infinity and nonsense become Normal.</summary>
    public static float Clamp(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return Normal;
        float best = Normal;
        foreach ((float scale, _) in Choices)
        {
            if (Math.Abs(scale - value) < Math.Abs(best - value)) best = scale;
        }
        return best;
    }
}
