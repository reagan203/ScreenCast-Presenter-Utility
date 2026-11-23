using System.Runtime.InteropServices;

namespace StressHarness;

internal static partial class Native
{
    public const byte VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_ESCAPE = 0x1B;
    public const uint KEYEVENTF_KEYUP = 2;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "InternalGetWindowText", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int InternalGetWindowText(nint hwnd, [Out] char[] text, int max);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [LibraryImport("user32.dll")]
    public static partial uint GetGuiResources(nint process, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static partial nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

    public static string Title(nint hwnd)
    {
        var buffer = new char[256];
        int n = InternalGetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(n, 0));
    }

    public static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int v, 4) == 0 && v != 0;

    /// <summary>Presses a key chord the way a user would: modifiers down, key, then all up in reverse.</summary>
    public static void Chord(params byte[] keys)
    {
        foreach (byte k in keys) keybd_event(k, 0, 0, 0);
        Thread.Sleep(15);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, KEYEVENTF_KEYUP, 0);
    }

    public static void CtrlAlt(char key) => Chord(VK_CONTROL, VK_MENU, (byte)key);
    public static void CtrlAltShift(char key) => Chord(VK_CONTROL, VK_MENU, VK_SHIFT, (byte)key);

    public static List<nint> TopLevelWindows(int processId, bool visibleOnly)
    {
        var list = new List<nint>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == processId && (!visibleOnly || IsWindowVisible(h))) list.Add(h);
            return true;
        }, 0);
        return list;
    }
}
