namespace ScreenCastPresenter.Core;

/// <summary>
/// The floating output window steps behind other windows while the presenter works in what it
/// shows, and comes back on top when they switch to something else; but only when it is a private
/// preview (hidden from capture, so no meeting can be sharing it). A window that a meeting could
/// share stays on top: some meeting apps share a window by copying its area of the screen, and
/// behind other windows they would send whatever covers it. The audience must only ever see the
/// chosen source (SRS 4.5).
/// </summary>
internal static class FloatingStepBack
{
    /// <param name="shareable">The window can be captured (it isn't a private preview): it never steps back.</param>
    /// <param name="activeRoot">The active window's top-level owner (0: none).</param>
    /// <param name="sourceRoot">The presented window's top-level owner (0 for a whole screen).</param>
    /// <param name="activeIsOutput">The active window is the output window itself.</param>
    /// <param name="activeOnShownScreen">A whole screen is shown and the active window is on it.</param>
    public static bool ShouldStepBack(bool shareable, nint activeRoot, nint sourceRoot, bool activeIsOutput, bool activeOnShownScreen)
    {
        if (shareable) return false;                             // privacy first: what a meeting shares is never covered
        if (activeIsOutput) return false;                        // the presenter went to it on purpose
        if (sourceRoot != 0) return activeRoot != 0 && activeRoot == sourceRoot; // in the presented window or one of its dialogs
        return activeOnShownScreen;                             // a whole screen: anywhere on it
    }
}
