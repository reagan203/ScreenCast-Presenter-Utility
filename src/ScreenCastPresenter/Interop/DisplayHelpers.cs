using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace ScreenCastPresenter.Interop;

/// <summary>One physical or virtual monitor. Coordinates are physical pixels (the app is per-monitor DPI v2 aware).</summary>
internal sealed record DisplayMonitor(string DeviceName, Rectangle Bounds, bool IsPrimary)
{
    /// <summary>Windows' own display number, taken from the device name ("\\.\DISPLAY2" → 2).</summary>
    public int Number
    {
        get
        {
            int i = DeviceName.Length;
            while (i > 0 && char.IsAsciiDigit(DeviceName[i - 1])) i--;
            return int.TryParse(DeviceName.AsSpan(i), out int n) ? n : 0;
        }
    }

    public string Label =>
        $"Display {Number} — {Bounds.Width} × {Bounds.Height}{(IsPrimary ? " (main screen)" : "")}";

    public override string ToString() => Label;
}

internal static class DisplayHelpers
{
    private const uint MonitorInfoPrimary = 1; // MONITORINFOF_PRIMARY

    public static unsafe List<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (hmon, _, _, _) =>
        {
            if (TryDescribe(hmon, out DisplayMonitor? m)) monitors.Add(m);
            return true;
        }, default);
        monitors.Sort((a, b) => a.Number.CompareTo(b.Number));
        return monitors;
    }

    /// <summary>The monitor that holds most of the given window.</summary>
    public static DisplayMonitor? FromWindow(nint hwnd)
    {
        HMONITOR hmon = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return TryDescribe(hmon, out DisplayMonitor? m) ? m : null;
    }

    private static unsafe bool TryDescribe(HMONITOR hmon, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DisplayMonitor? monitor)
    {
        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        // GetMonitorInfo fills the larger MONITORINFOEXW (with the device name) when cbSize says so.
        if (!PInvoke.GetMonitorInfo(hmon, (MONITORINFO*)&info))
        {
            monitor = null;
            return false;
        }

        RECT r = info.monitorInfo.rcMonitor;
        monitor = new DisplayMonitor(
            info.szDevice.ToString(),
            Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom),
            (info.monitorInfo.dwFlags & MonitorInfoPrimary) != 0);
        return true;
    }
}
