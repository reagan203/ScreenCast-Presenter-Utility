using System.Drawing;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace ScreenCastPresenter.Interop;

/// <summary>One physical or virtual monitor. Coordinates are physical pixels (the app is per-monitor DPI v2 aware).</summary>
internal sealed record DisplayMonitor(
    string DeviceName,
    Rectangle Bounds,
    Rectangle WorkArea,
    bool IsPrimary,
    double Scale,
    string FriendlyName = "",
    string Connection = "")
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

    /// <summary>
    /// A software screen from a virtual display driver (FR-12): nobody looks at it; it exists so
    /// Zoom can share it. Indirect-display drivers report themselves as virtual; some only say so
    /// in their name.
    /// </summary>
    public bool IsVirtual =>
        Connection == "Virtual"
        || FriendlyName.Contains("virtual", StringComparison.OrdinalIgnoreCase)
        || FriendlyName.StartsWith("IDD", StringComparison.OrdinalIgnoreCase);

    /// <summary>A TV-like screen: HDMI or wireless.</summary>
    public bool IsTv => Connection is "HDMI" or "Wireless";

    /// <summary>"TV · HDMI" for external screens, "Zoom screen" for a virtual one, "This screen" for the built-in one, else the monitor's own name.</summary>
    public string ShortName =>
        IsVirtual ? "Zoom screen"
        : Connection == "Built-in" ? "This screen"
        : IsTv ? $"TV · {Connection}"
        : FriendlyName.Length > 0 ? FriendlyName
        : $"Display {Number}";

    public string Label => $"Display {Number} · {ShortName} · {Bounds.Width} × {Bounds.Height}{(IsPrimary ? " (main)" : "")}";

    /// <summary>Same physical screen (device name), even if its resolution changed.</summary>
    public bool IsSameScreen(DisplayMonitor? other) => other is not null && other.DeviceName == DeviceName;

    public override string ToString() => Label;
}

internal static class DisplayHelpers
{
    private const uint MonitorInfoPrimary = 1; // MONITORINFOF_PRIMARY

    public static unsafe List<DisplayMonitor> GetMonitors()
    {
        Dictionary<string, (string Name, string Connection)> names = GetFriendlyNames();
        var monitors = new List<DisplayMonitor>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (hmon, _, _, _) =>
        {
            if (TryDescribe(hmon, names, out DisplayMonitor? m)) monitors.Add(m);
            return true;
        }, default);
        monitors.Sort((a, b) => a.Number.CompareTo(b.Number));
        return monitors;
    }

    /// <summary>The monitor that holds most of the given window.</summary>
    public static DisplayMonitor? FromWindow(nint hwnd)
    {
        HMONITOR hmon = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return TryDescribe(hmon, null, out DisplayMonitor? m) ? m : null;
    }

    /// <summary>The monitor under a screen point (e.g. the mouse cursor).</summary>
    public static DisplayMonitor? FromPoint(Point point)
    {
        HMONITOR hmon = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return TryDescribe(hmon, null, out DisplayMonitor? m) ? m : null;
    }

    /// <summary>
    /// True when any screen is duplicated onto another (Windows "Duplicate" mode). Then the TV
    /// shows the presenter's whole screen and SCP cannot keep anything private.
    /// </summary>
    public static bool IsAnyScreenDuplicated()
    {
        DISPLAYCONFIG_PATH_INFO[] paths = QueryActivePaths();
        var sources = new HashSet<(uint Low, int High, uint Id)>();
        foreach (DISPLAYCONFIG_PATH_INFO p in paths)
        {
            // Two active paths (targets) fed by the same source = one picture on two screens.
            if (!sources.Add((p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id)))
                return true;
        }
        return false;
    }

    private static unsafe bool TryDescribe(HMONITOR hmon, Dictionary<string, (string Name, string Connection)>? names,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DisplayMonitor? monitor)
    {
        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        // GetMonitorInfo fills the larger MONITORINFOEXW (with the device name) when cbSize says so.
        if (!PInvoke.GetMonitorInfo(hmon, (MONITORINFO*)&info))
        {
            monitor = null;
            return false;
        }

        double scale = 1.0;
        if (PInvoke.GetDpiForMonitor(hmon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded && dpiX > 0)
            scale = dpiX / 96.0;

        string device = info.szDevice.ToString();
        names ??= GetFriendlyNames();
        names.TryGetValue(device, out (string Name, string Connection) friendly);

        RECT r = info.monitorInfo.rcMonitor;
        RECT w = info.monitorInfo.rcWork;
        monitor = new DisplayMonitor(
            device,
            Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom),
            Rectangle.FromLTRB(w.left, w.top, w.right, w.bottom),
            (info.monitorInfo.dwFlags & MonitorInfoPrimary) != 0,
            scale,
            friendly.Name ?? "",
            friendly.Connection ?? "");
        return true;
    }

    /// <summary>GDI device name → (monitor's own name from EDID, connection type), via the display configuration API.</summary>
    private static unsafe Dictionary<string, (string Name, string Connection)> GetFriendlyNames()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (DISPLAYCONFIG_PATH_INFO path in QueryActivePaths())
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0) continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0) continue;

            string gdiName = source.viewGdiDeviceName.ToString();
            // First path wins: in Duplicate mode one GDI screen has several targets.
            result.TryAdd(gdiName, (target.monitorFriendlyDeviceName.ToString(), ConnectionName(target.outputTechnology)));
        }
        return result;
    }

    private static DISPLAYCONFIG_PATH_INFO[] QueryActivePaths()
    {
        const QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
        // The topology can change between the two calls (hot-plug), so retry a few times.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (PInvoke.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount) != WIN32_ERROR.ERROR_SUCCESS)
                return [];
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            WIN32_ERROR error = PInvoke.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes);
            if (error == WIN32_ERROR.ERROR_SUCCESS) return paths[..(int)pathCount];
            if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER) return [];
        }
        return [];
    }

    private static string ConnectionName(DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY tech) => tech switch
    {
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => "HDMI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => "DisplayPort",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL => "USB-C",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST => "Wireless",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL => "Virtual",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL or
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED or
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS or
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED => "Built-in",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI => "DVI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HD15 => "VGA",
        _ => "",
    };
}
