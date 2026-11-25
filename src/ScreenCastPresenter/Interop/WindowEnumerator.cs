using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Interop;

/// <summary>A window the user can choose to present.</summary>
/// <summary>
/// Something the user can present: a window (<see cref="Handle"/>) or a whole screen
/// (<see cref="Screen"/>, FR-02), optionally cropped (<see cref="Crop"/>, FR-04).
/// </summary>
internal sealed record SourceWindow(nint Handle, string Title, string AppName, bool IsMinimized)
{
    /// <summary>Set for a whole-screen source; then <see cref="Handle"/> is 0.</summary>
    public DisplayMonitor? Screen { get; init; }

    /// <summary>The part of the source to show; null shows all of it.</summary>
    public Core.CropRegion? Crop { get; init; }

    public bool IsScreen => Screen is not null;

    /// <summary>Same window or same screen, whatever the crop or title.</summary>
    public bool IsSameSource(SourceWindow? other) =>
        other is not null && (IsScreen ? other.Screen?.DeviceName == Screen!.DeviceName && other.IsScreen : !other.IsScreen && other.Handle == Handle);

    public static SourceWindow ForScreen(DisplayMonitor screen) =>
        new(0, $"{screen.Name} · {screen.ShortName}", "Screen", false) { Screen = screen };

    public override string ToString() => Title;
}

/// <summary>Raw facts about a top-level window, gathered once so the filter can be a pure (testable) function.</summary>
internal readonly record struct WindowCandidate(
    nint Handle,
    string Title,
    uint ProcessId,
    WINDOW_EX_STYLE ExStyle,
    bool IsVisible,
    bool IsCloaked,
    bool HasOwner,
    bool IsShellWindow,
    bool IsMinimized,
    int Width,
    int Height);

internal static class WindowFilter
{
    /// <summary>
    /// Roughly the same rules Alt+Tab uses (FR-01): visible, not cloaked, has a title, is not a
    /// tool or owned popup window (unless it forces itself onto the taskbar with WS_EX_APPWINDOW),
    /// and is not one of SCP's own windows, which also keeps the output window out of the list (FR-14).
    /// </summary>
    public static bool IsPresentable(in WindowCandidate w, uint ownProcessId)
    {
        if (!w.IsVisible || w.IsCloaked || w.IsShellWindow) return false;
        if (w.ProcessId == ownProcessId) return false;
        if (string.IsNullOrWhiteSpace(w.Title)) return false;
        if (!w.IsMinimized && (w.Width <= 0 || w.Height <= 0)) return false;

        bool forcedOnTaskbar = w.ExStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_APPWINDOW);
        if (forcedOnTaskbar) return true;

        if (w.ExStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW)) return false;
        if (w.ExStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_NOACTIVATE)) return false;
        return !w.HasOwner;
    }
}

internal static class WindowEnumerator
{
    /// <summary>Presentable windows in z-order (most recently used first).</summary>
    public static List<SourceWindow> GetPresentableWindows()
    {
        var handles = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        }, default);

        uint ownPid = (uint)Environment.ProcessId;
        HWND shell = PInvoke.GetShellWindow();
        var appNames = new Dictionary<uint, string>();
        var result = new List<SourceWindow>();

        foreach (HWND hwnd in handles)
        {
            if (!PInvoke.IsWindowVisible(hwnd)) continue; // cheap early-out; most windows are hidden

            WindowCandidate c = Describe(hwnd, shell);
            if (!WindowFilter.IsPresentable(c, ownPid)) continue;
            result.Add(ToSource(c, appNames));
        }
        return result;
    }

    /// <summary>
    /// The top-level window the user is working in, if it can be presented (used by "Show the
    /// window I am in"). Returns null for SCP's own windows, the desktop and the taskbar.
    /// </summary>
    public static SourceWindow? GetActiveWindow()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull) return null;
        HWND root = PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        if (root.IsNull) root = foreground;

        WindowCandidate c = Describe(root, PInvoke.GetShellWindow());
        if (!PInvoke.IsWindowVisible(root)) return null;
        return WindowFilter.IsPresentable(c, (uint)Environment.ProcessId) ? ToSource(c, new()) : null;
    }

    /// <summary>Re-reads a window's title and state, or null if it no longer exists.</summary>
    public static SourceWindow? Refresh(SourceWindow window)
    {
        var hwnd = (HWND)window.Handle;
        if (!PInvoke.IsWindow(hwnd)) return null;
        return window with { Title = GetTitle(hwnd) is { Length: > 0 } t ? t : window.Title, IsMinimized = PInvoke.IsIconic(hwnd) };
    }

    public static bool IsAlive(nint hwnd) => PInvoke.IsWindow((HWND)hwnd);

    public static bool IsMinimized(nint hwnd) => PInvoke.IsIconic((HWND)hwnd);

    /// <summary>
    /// Brings a window to the front, restoring it if minimised. Works because it runs in response
    /// to the user clicking SCP (Windows only lets the app that got the last input do this).
    /// </summary>
    public static bool BringToFront(nint hwnd)
    {
        var h = (HWND)hwnd;
        if (!PInvoke.IsWindow(h)) return false;
        if (PInvoke.IsIconic(h)) PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        return PInvoke.SetForegroundWindow(h);
    }

    /// <summary>The process behind a window, looking through the UWP frame host to the real app; 0 if gone.</summary>
    public static uint ProcessOf(nint hwnd)
    {
        if (PInvoke.GetWindowThreadProcessId((HWND)hwnd, out uint pid) == 0) return 0;
        return AppProcessId((HWND)hwnd, pid);
    }

    /// <summary>The client area in screen coordinates (physical pixels); empty if the window is gone.</summary>
    public static Rectangle ClientBoundsOnScreen(nint hwnd)
    {
        var h = (HWND)hwnd;
        if (!PInvoke.GetClientRect(h, out RECT r)) return Rectangle.Empty;
        var origin = new Point(0, 0);
        if (!PInvoke.ClientToScreen(h, ref origin)) return Rectangle.Empty;
        return new Rectangle(origin.X, origin.Y, r.right - r.left, r.bottom - r.top);
    }

    /// <summary>Client-area size in physical pixels (what a client-only DWM thumbnail shows). No message is sent.</summary>
    public static Size ClientSize(nint hwnd) =>
        PInvoke.GetClientRect((HWND)hwnd, out RECT r) ? new Size(r.right - r.left, r.bottom - r.top) : Size.Empty;

    private static SourceWindow ToSource(in WindowCandidate c, Dictionary<uint, string> appNames)
    {
        uint pid = AppProcessId((HWND)c.Handle, c.ProcessId);
        if (!appNames.TryGetValue(pid, out string? app))
        {
            app = GetAppName(pid);
            appNames[pid] = app;
        }
        return new SourceWindow(c.Handle, c.Title, app, c.IsMinimized);
    }

    private static WindowCandidate Describe(HWND hwnd, HWND shell)
    {
        PInvoke.GetWindowThreadProcessId(hwnd, out uint pid);
        PInvoke.GetWindowRect(hwnd, out RECT rect);
        return new WindowCandidate(
            Handle: hwnd,
            Title: GetTitle(hwnd),
            ProcessId: pid,
            ExStyle: (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE),
            IsVisible: true,
            IsCloaked: DwmHelpers.IsCloaked(hwnd),
            HasOwner: !PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull,
            IsShellWindow: hwnd == shell,
            IsMinimized: PInvoke.IsIconic(hwnd),
            Width: rect.right - rect.left,
            Height: rect.bottom - rect.top);
    }

    /// <summary>
    /// InternalGetWindowText reads the stored caption without sending a message to the window,
    /// so a hung app can never freeze the picker (GetWindowText/GetWindowTextLength can block).
    /// </summary>
    private static string GetTitle(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[512];
        int copied = PInvoke.InternalGetWindowText(hwnd, buffer);
        return copied > 0 ? new string(buffer[..copied]) : string.Empty;
    }

    /// <summary>
    /// Store (UWP) apps live inside an ApplicationFrameHost window owned by another process; the
    /// real app is the process that owns the child CoreWindow. Use that for the app name.
    /// </summary>
    private static uint AppProcessId(HWND hwnd, uint framePid)
    {
        Span<char> cls = stackalloc char[64];
        int length = PInvoke.GetClassName(hwnd, cls);
        if (!cls[..Math.Max(length, 0)].SequenceEqual("ApplicationFrameWindow")) return framePid;

        uint appPid = framePid;
        PInvoke.EnumChildWindows(hwnd, (child, _) =>
        {
            PInvoke.GetWindowThreadProcessId(child, out uint pid);
            if (pid != framePid)
            {
                appPid = pid;
                return false; // stop
            }
            return true;
        }, default);
        return appPid;
    }

    /// <summary>Executable name without extension, e.g. "notepad". Avoids Process.GetProcessById, which snapshots every process.</summary>
    private static string GetAppName(uint pid)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid) return "App";

        Span<char> path = stackalloc char[1024];
        uint size = (uint)path.Length;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, path, ref size))
            return "App";
        return Path.GetFileNameWithoutExtension(path[..(int)size].ToString());
    }
}
