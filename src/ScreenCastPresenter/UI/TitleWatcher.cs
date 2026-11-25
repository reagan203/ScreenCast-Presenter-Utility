using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Tells SCP the moment one window's title changes (a browser changes it as soon as another tab
/// or page is shown). An out-of-context WinEvent hook limited to that window's thread: the
/// callback arrives as a message on the UI thread within milliseconds, nothing is injected into
/// the browser, and nothing runs while no browser is being shown.
/// </summary>
internal sealed class TitleWatcher : IDisposable
{
    private const uint EventObjectNameChange = 0x800C;

    private UnhookWinEventSafeHandle? _hook;
    private WINEVENTPROC? _callback; // must stay referenced while the hook exists
    private HWND _window;

    public event EventHandler? TitleChanged;

    public nint Window => _window;

    public void Watch(nint window)
    {
        if (_window == (HWND)window && _hook is not null) return;
        Stop();
        _window = (HWND)window;
        uint thread = PInvoke.GetWindowThreadProcessId(_window, out uint process);
        if (thread == 0) return;
        _callback = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (hwnd == _window && idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW && idChild == (int)PInvoke.CHILDID_SELF)
                TitleChanged?.Invoke(this, EventArgs.Empty);
        };
        UnhookWinEventSafeHandle hook = PInvoke.SetWinEventHook(EventObjectNameChange, EventObjectNameChange, default, _callback,
            process, thread, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (hook.IsInvalid) hook.Dispose();
        else _hook = hook;
    }

    public void Stop()
    {
        _hook?.Dispose();
        _hook = null;
        _window = default;
    }

    public void Dispose() => Stop();
}
