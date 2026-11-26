using System.Runtime.InteropServices;
using ScreenCastPresenter.Core;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// DDC/CI to a screen (FR-107): power and input, through Windows' monitor configuration API. Each
/// call talks over the cable and can take tens of milliseconds (asking what a screen supports, a
/// second or two), so callers run these off the UI thread. A screen that doesn't answer is simply
/// "not supported".
/// </summary>
internal static unsafe class MonitorDdc
{
    /// <summary>Runs <paramref name="act"/> on each physical monitor behind <paramref name="screen"/>; true when one of them did it.</summary>
    private static bool OnPhysical(DisplayMonitor screen, Func<HANDLE, bool> act)
    {
        var centre = new Point(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2);
        HMONITOR monitor = PInvoke.MonitorFromPoint(centre, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        if (monitor.IsNull) return false;
        if (!PInvoke.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0 || count > 8) return false;
        var physical = new PHYSICAL_MONITOR[count];
        if (!PInvoke.GetPhysicalMonitorsFromHMONITOR(monitor, physical)) return false;
        try
        {
            bool any = false;
            foreach (PHYSICAL_MONITOR p in physical) any |= act(p.hPhysicalMonitor);
            return any;
        }
        finally
        {
            PInvoke.DestroyPhysicalMonitors(physical);
        }
    }

    /// <summary>Turns the screen on or off (standby). False when it didn't take it.</summary>
    public static bool SetPower(DisplayMonitor screen, bool on) =>
        OnPhysical(screen, h => PInvoke.SetVCPFeature(h, TvControl.PowerMode, on ? TvControl.PowerOn : TvControl.PowerStandby) != 0);

    /// <summary>Switches the screen to input <paramref name="code"/> (MCCS 0x60). False when it didn't take it.</summary>
    public static bool SetInput(DisplayMonitor screen, uint code) =>
        OnPhysical(screen, h => PInvoke.SetVCPFeature(h, TvControl.InputSource, code) != 0);

    /// <summary>The screen's current value for <paramref name="code"/>, or null.</summary>
    public static uint? Read(DisplayMonitor screen, byte code)
    {
        uint? value = null;
        OnPhysical(screen, h =>
        {
            MC_VCP_CODE_TYPE type;
            uint current, maximum;
            if (PInvoke.GetVCPFeatureAndVCPFeatureReply(h, code, &type, &current, &maximum) == 0) return false;
            value = current;
            return true;
        });
        return value;
    }

    /// <summary>What the screen says it supports (its capabilities string), or null when it doesn't answer.</summary>
    public static string? Capabilities(DisplayMonitor screen)
    {
        string? caps = null;
        OnPhysical(screen, h =>
        {
            uint length;
            if (PInvoke.GetCapabilitiesStringLength(h, &length) == 0 || length is 0 or > 16_384) return false;
            byte[] buffer = new byte[length];
            fixed (byte* b = buffer)
            {
                if (PInvoke.CapabilitiesRequestAndCapabilitiesReply(h, new PSTR(b), length) == 0) return false;
            }
            caps = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            return true;
        });
        return caps;
    }
}
