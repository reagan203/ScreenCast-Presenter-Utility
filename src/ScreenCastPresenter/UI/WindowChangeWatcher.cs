using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Tells SCP when any top-level window may have appeared on screen or moved: shown, uncloaked,
/// restored from minimised, brought to the front, moved or resized. Used while a whole screen is
/// shown, so a never-show app turning up there cuts the picture within milliseconds
/// (NeverShow). Out-of-context WinEvent hooks: nothing is injected into other apps, callbacks
/// arrive on the UI thread, and they exist only while <see cref="Start"/>ed. A window appearing is
/// reported at once; moves (many per second while dragging) at most every
/// <see cref="MoveInterval"/>. A slow poll backs the hooks up, in case Windows drops an event.
/// </summary>
internal sealed class WindowChangeWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016, EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectShow = 0x8002, EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectCloaked = 0x8017, EventObjectUncloaked = 0x8018;

    public const int MoveInterval = 60;
    public const int PollInterval = 1000;

    private readonly List<UnhookWinEventSafeHandle> _hooks = [];
    private readonly System.Windows.Forms.Timer _moved = new() { Interval = MoveInterval };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = PollInterval };
    private WINEVENTPROC? _callback; // must stay referenced while the hooks exist
    private bool _posted;

    public WindowChangeWatcher()
    {
        _moved.Tick += (_, _) =>
        {
            _moved.Stop();
            Raise();
        };
        _poll.Tick += (_, _) => Raise();
    }

    /// <summary>Something on screen may have changed. Raised on the UI thread, at most once per message-loop turn.</summary>
    public event EventHandler? Changed;

    public bool IsRunning => _hooks.Count > 0;

    public void Start()
    {
        if (IsRunning) return;
        _callback = OnEvent;
        foreach ((uint min, uint max) in new[]
        {
            (EventSystemForeground, EventSystemForeground),
            (EventSystemMinimizeStart, EventSystemMinimizeEnd),
            (EventObjectShow, EventObjectHide),
            (EventObjectLocationChange, EventObjectLocationChange),
            (EventObjectCloaked, EventObjectUncloaked),
        })
        {
            UnhookWinEventSafeHandle hook = PInvoke.SetWinEventHook(min, max, default, _callback, 0, 0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
            if (hook.IsInvalid) hook.Dispose();
            else _hooks.Add(hook);
        }
        _poll.Start();
    }

    public void Stop()
    {
        foreach (UnhookWinEventSafeHandle hook in _hooks) hook.Dispose();
        _hooks.Clear();
        _moved.Stop();
        _poll.Stop();
    }

    private void OnEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Whole windows only: not the caret, the cursor or a control inside a window.
        if (hwnd.IsNull || idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != (int)PInvoke.CHILDID_SELF) return;
        if (PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != hwnd) return;
        if (@event == EventObjectLocationChange)
        {
            if (!_moved.Enabled) _moved.Start();
            return;
        }
        Post(); // appeared, restored, uncloaked, brought forward: look now
    }

    private void Post()
    {
        if (_posted) return;
        _posted = true;
        (SynchronizationContext.Current ?? new SynchronizationContext()).Post(_ =>
        {
            _posted = false;
            Raise();
        }, null);
    }

    private void Raise()
    {
        if (IsRunning) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Stop();
        _moved.Dispose();
        _poll.Dispose();
    }
}
