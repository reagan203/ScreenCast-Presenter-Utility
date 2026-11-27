namespace ScreenCastPresenter.Core;

/// <summary>
/// Whether the mouse pointer is drawn into the picture on the TV (FR-24). It is when "Mouse pointer
/// on the TV" is on, except while the laser stands in for it (FR-71), and except while the
/// presentation covers the very screen the shown window is on (no TV connected, so it's shown full
/// screen on the presenter's own screen). There the presenter's real pointer is already on top of
/// the picture, so a copy of it inside the picture would be a second pointer, out of step with the
/// first (the picture is the window scaled to fit).
/// </summary>
internal static class PictureCursor
{
    /// <param name="setting">"Mouse pointer on the TV" is on.</param>
    /// <param name="laser">The laser pointer is on.</param>
    /// <param name="outputScreen">The screen the presentation covers (full screen), or null when it's in a window or not shown.</param>
    /// <param name="windowScreen">The screen the shown window is on, or null when what's shown isn't a window.</param>
    public static bool Wanted(bool setting, bool laser, string? outputScreen, string? windowScreen) =>
        setting && !laser && !CoversOwnScreen(outputScreen, windowScreen);

    /// <summary>The presentation covers the screen the shown window is on: the presenter's own pointer is on it already.</summary>
    public static bool CoversOwnScreen(string? outputScreen, string? windowScreen) =>
        outputScreen is not null && windowScreen is not null && string.Equals(outputScreen, windowScreen, StringComparison.OrdinalIgnoreCase);
}
