using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// The desktop's icon list (FR-113): a SysListView32 inside SHELLDLL_DefView, which lives under
/// Progman or, once a wallpaper slideshow has run, under one of the WorkerW windows. Hiding it hides
/// the icons only: the wallpaper stays.
/// </summary>
internal sealed class DesktopIcons : Core.IDesktopIconPlatform
{
    public static DesktopIcons Instance { get; } = new();

    public nint FindList()
    {
        nint found = 0;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            string cls = ClassOf(hwnd);
            if (cls is not ("Progman" or "WorkerW")) return true;
            HWND view = PInvoke.FindWindowEx(hwnd, HWND.Null, "SHELLDLL_DefView", null);
            if (view.IsNull) return true;
            HWND list = PInvoke.FindWindowEx(view, HWND.Null, "SysListView32", null);
            if (list.IsNull) return true;
            found = list;
            return false;
        }, default);
        return found;
    }

    public bool IsVisible(nint list) => list != 0 && PInvoke.IsWindow((HWND)list) && PInvoke.IsWindowVisible((HWND)list);

    public void SetVisible(nint list, bool visible)
    {
        if (list == 0 || !PInvoke.IsWindow((HWND)list)) return;
        PInvoke.ShowWindow((HWND)list, visible ? SHOW_WINDOW_CMD.SW_SHOWNA : SHOW_WINDOW_CMD.SW_HIDE);
    }

    internal static unsafe string ClassOf(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[64];
        fixed (char* p = buffer)
        {
            int n = PInvoke.GetClassName(hwnd, p, buffer.Length);
            return n > 0 ? new string(buffer[..n]) : "";
        }
    }
}
