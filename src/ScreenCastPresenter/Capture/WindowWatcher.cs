using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Tells the session when the source window closes, moves/resizes, or is minimised/restored.
/// Uses out-of-context WinEvent hooks limited to the source's thread: callbacks arrive as messages
/// on our UI thread, nothing is injected into the other app, and idle cost is zero.
/// </summary>
internal sealed class WindowWatcher : IDisposable
{
    private readonly HWND _hwnd;
    private readonly WINEVENTPROC _callback; // must stay referenced while the hooks exist
    private readonly List<UnhookWinEventSafeHandle> _hooks = [];
    private readonly System.Windows.Forms.Timer _aliveCheck;
    private bool _closed;

    public event EventHandler? Closed;
    public event EventHandler? Changed;
    public event EventHandler? Minimized;
    public event EventHandler? Restored;

    public WindowWatcher(nint hwnd)
    {
        _hwnd = (HWND)hwnd;
        uint thread = PInvoke.GetWindowThreadProcessId(_hwnd, out uint process);
        if (thread == 0) throw new InvalidOperationException("The window no longer exists.");

        _callback = OnWinEvent;
        Hook(PInvoke.EVENT_OBJECT_DESTROY, PInvoke.EVENT_OBJECT_DESTROY, process, thread);
        Hook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, process, thread);
        Hook(PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND, process, thread);

        // Safety net: if the source process dies abruptly, a destroy event isn't guaranteed.
        _aliveCheck = new System.Windows.Forms.Timer { Interval = 1000 };
        _aliveCheck.Tick += (_, _) =>
        {
            if (!PInvoke.IsWindow(_hwnd)) RaiseClosed();
        };
        _aliveCheck.Start();
    }

    public bool IsMinimized => PInvoke.IsIconic(_hwnd);

    private void Hook(uint eventMin, uint eventMax, uint process, uint thread)
    {
        UnhookWinEventSafeHandle hook = PInvoke.SetWinEventHook(eventMin, eventMax, default, _callback,
            process, thread, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (hook.IsInvalid) hook.Dispose();
        else _hooks.Add(hook);
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Hooks fire for every object on that thread (carets, child controls...); keep only the window itself.
        if (hwnd != _hwnd || idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != (int)PInvoke.CHILDID_SELF)
            return;

        switch (@event)
        {
            case PInvoke.EVENT_OBJECT_DESTROY: RaiseClosed(); break;
            case PInvoke.EVENT_OBJECT_LOCATIONCHANGE: Changed?.Invoke(this, EventArgs.Empty); break;
            case PInvoke.EVENT_SYSTEM_MINIMIZESTART: Minimized?.Invoke(this, EventArgs.Empty); break;
            case PInvoke.EVENT_SYSTEM_MINIMIZEEND: Restored?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private void RaiseClosed()
    {
        if (_closed) return;
        _closed = true;
        _aliveCheck.Stop();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _aliveCheck.Dispose();
        foreach (UnhookWinEventSafeHandle hook in _hooks) hook.Dispose(); // calls UnhookWinEvent
        _hooks.Clear();
        Closed = Changed = Minimized = Restored = null;
    }
}
