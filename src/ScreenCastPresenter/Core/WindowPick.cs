using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Core;

/// <summary>
/// A top-level window on screen, front to back, for "click a window to show it" (FR-05).
/// <see cref="Window"/> is what clicking it would show: the window, or the app window that owns
/// it (a dialog or palette counts as its app). Null for things that can't be shown: the
/// desktop, the taskbar, another app's overlay.
/// </summary>
internal sealed record PickTarget(SourceWindow? Window, Rectangle Bounds);

internal static class WindowPick
{
    /// <summary>
    /// What a click at <paramref name="point"/> would show: the front-most window there, if it
    /// can be shown. A window that can't be shown in front of one that can (the taskbar over a
    /// maximised window, a floating toolbar) is what the click lands on, so nothing is chosen.
    /// </summary>
    public static PickTarget? At(IReadOnlyList<PickTarget> frontToBack, Point point)
    {
        foreach (PickTarget target in frontToBack)
        {
            if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0 || !target.Bounds.Contains(point)) continue;
            return target.Window is null ? null : target;
        }
        return null;
    }

    /// <summary>Pointing at a window of a never-show app (NeverShow): it can't be chosen.</summary>
    public static string NeverShownHint(SourceWindow window) =>
        $"“{window.AppName}” is on your never-show list. Click another window, or press Esc to cancel";

    /// <summary>The instruction under the pointer.</summary>
    public static string Hint(PickTarget? target, string? escape = "Esc") =>
        target?.Window is { } w
            ? $"Click to show “{TrayMenuPlan.Shorten(w.Title.Length > 0 ? w.Title : w.AppName, 50)}”   ·   {escape} to cancel"
            : $"Point at the window you want to show, then click it   ·   {escape} to cancel";
}
