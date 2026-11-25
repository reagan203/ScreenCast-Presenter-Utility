using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Tells SCP when another app's window becomes the active one. An out-of-context WinEvent hook:
/// callbacks arrive as messages on the UI thread, nothing is injected into other apps, and it only
/// exists while it is needed (<see cref="Start"/> / <see cref="Stop"/>). SCP's own windows are
/// skipped.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;

    private UnhookWinEventSafeHandle? _hook;
    private WINEVENTPROC? _callback; // must stay referenced while the hook exists

    /// <summary>The newly active window.</summary>
    public event EventHandler<nint>? Changed;

    public bool IsRunning => _hook is not null;

    public void Start()
    {
        if (_hook is not null) return;
        _callback = (_, _, hwnd, _, _, _, _) => Changed?.Invoke(this, (nint)hwnd);
        UnhookWinEventSafeHandle hook = PInvoke.SetWinEventHook(EventSystemForeground, EventSystemForeground, default, _callback,
            0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (hook.IsInvalid) hook.Dispose();
        else _hook = hook;
    }

    public void Stop()
    {
        _hook?.Dispose();
        _hook = null;
    }

    public void Dispose() => Stop();
}
