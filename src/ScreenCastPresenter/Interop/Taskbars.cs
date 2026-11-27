using System.Runtime.InteropServices;
using ScreenCastPresenter.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.Interop;

/// <summary>Where the taskbars are (FR-112): the work area Windows reports, and taskbars that hide themselves.</summary>
internal static partial class Taskbars
{
    private const uint AbmGetAutoHideBarEx = 0x0000000B; // ABM_GETAUTOHIDEBAREX, Windows 8 and later

    /// <summary>APPBARDATA (shellapi.h), as laid out on 64-bit Windows (x64 and Arm64, the two builds).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [LibraryImport("shell32.dll")]
    private static unsafe partial nuint SHAppBarMessage(uint message, AppBarData* data);

    /// <summary>The part of <paramref name="screen"/> clear of its taskbars, asked afresh (the taskbar may have moved or started hiding itself).</summary>
    public static CropRegion? ClearArea(DisplayMonitor screen)
    {
        DisplayMonitor now = DisplayHelpers.GetMonitors().FirstOrDefault(m => m.IsSameScreen(screen)) ?? screen;
        return TaskbarCrop.ClearOf(now.Bounds, now.WorkArea, AutoHiddenOn(now.Bounds));
    }

    /// <summary>Taskbars that hide themselves on the screen at <paramref name="monitor"/>: each edge and the bar's full thickness.</summary>
    public static unsafe List<(ScreenEdge Edge, int Thickness)> AutoHiddenOn(Rectangle monitor)
    {
        var found = new List<(ScreenEdge, int)>();
        foreach (ScreenEdge edge in Enum.GetValues<ScreenEdge>())
        {
            var data = new AppBarData
            {
                cbSize = (uint)sizeof(AppBarData),
                uEdge = (uint)edge, // ABE_LEFT 0, ABE_TOP 1, ABE_RIGHT 2, ABE_BOTTOM 3: the same order
                rc = new RECT(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom),
            };
            nuint bar = SHAppBarMessage(AbmGetAutoHideBarEx, &data);
            if (bar == 0 || !PInvoke.GetWindowRect((HWND)(nint)bar, out RECT r)) continue;
            int thickness = TaskbarCrop.Thickness(edge, Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom));
            if (thickness > 0) found.Add((edge, thickness));
        }
        return found;
    }
}
