using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Interop;

/// <summary>A window the user can choose to present.</summary>
internal sealed record SourceWindow(nint Handle, string Title, string AppName, bool IsMinimized)
{
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

            if (!appNames.TryGetValue(c.ProcessId, out string? app))
            {
                app = GetAppName(c.ProcessId);
                appNames[c.ProcessId] = app;
            }
            result.Add(new SourceWindow(c.Handle, c.Title, app, c.IsMinimized));
        }
        return result;
    }

    public static bool IsAlive(nint hwnd) => PInvoke.IsWindow((HWND)hwnd);

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

    private static string GetTitle(HWND hwnd)
    {
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        Span<char> buffer = length < 512 ? stackalloc char[length + 1] : new char[length + 1];
        int copied = PInvoke.GetWindowText(hwnd, buffer);
        return new string(buffer[..copied]);
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
