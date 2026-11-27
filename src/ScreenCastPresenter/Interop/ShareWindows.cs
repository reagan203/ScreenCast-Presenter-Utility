using ScreenCastPresenter.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Interop;

/// <summary>The windows of meeting apps, described for the share watch (FR-134).</summary>
internal static class ShareWindows
{
    /// <summary>Visible windows of meeting apps with their styles; nothing else is looked at closely.</summary>
    public static List<ShareFacts> Describe()
    {
        var facts = new List<ShareFacts>();
        foreach (VisibleWindow w in WindowEnumerator.GetVisibleWindows([]))
        {
            if (ShareWatch.MeetingName(w.AppName) is null) continue;
            var ex = (WINDOW_EX_STYLE)PInvoke.GetWindowLong((HWND)w.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            facts.Add(new ShareFacts(w.Handle, w.AppName, w.Bounds,
                ex.HasFlag(WINDOW_EX_STYLE.WS_EX_LAYERED), ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TRANSPARENT), ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST)));
        }
        return facts;
    }
}
