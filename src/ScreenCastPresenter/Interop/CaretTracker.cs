using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// Where the presenter is typing (FR-99): the text cursor in the window they're working in, every
/// <see cref="Every"/> on its own thread. Windows' own caret first (classic apps, Office, browsers
/// that support magnifiers); otherwise the focused text field from UI Automation when it's a small
/// one (a single line). Only where, never what is typed.
/// </summary>
internal sealed class CaretTracker : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(50);

    /// <summary>A focused element taller than this is a whole editor, not a place to zoom to.</summary>
    public const int MaxFieldHeight = 80;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly object _lock = new();
    private (Rectangle Caret, nint Window, long MovedAt)? _latest;

    public CaretTracker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SCP text cursor" };
        _thread.Start();
    }

    /// <summary>The text cursor (screen coordinates), the top-level window it's in, and when it last moved (Environment.TickCount64).</summary>
    public (Rectangle Caret, nint Window, long MovedAt)? Latest
    {
        get
        {
            lock (_lock) return _latest;
        }
    }

    private void Run()
    {
        using UiAutomation? uia = UiAutomation.Create();
        while (!_stop.IsSet)
        {
            nint root = WindowEnumerator.ForegroundRoot();
            Rectangle? caret = SystemCaret();
            if (caret is null && uia is not null && root != 0 && WindowEnumerator.ProcessOf(root) is var pid and not 0
                && uia.FocusedIn(pid) is { Height: > 0 and <= MaxFieldHeight } field)
                caret = new Rectangle(field.X + 4, field.Y, 2, field.Height); // the start of a small field
            lock (_lock)
            {
                if (caret is { } c && root != 0)
                {
                    if (_latest is not { } last || last.Caret != c || last.Window != root) _latest = (c, root, Environment.TickCount64);
                }
                else _latest = null;
            }
            if (_stop.Wait(Every)) break;
        }
    }

    /// <summary>Windows' own caret in the foreground thread, in screen coordinates; null when there's none.</summary>
    internal static unsafe Rectangle? SystemCaret()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull) return null;
        uint thread = PInvoke.GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (!PInvoke.GetGUIThreadInfo(thread, ref info) || info.hwndCaret.IsNull) return null;
        RECT r = info.rcCaret;
        if (r.bottom - r.top <= 0) return null;
        var origin = new Point(r.left, r.top);
        if (!PInvoke.ClientToScreen(info.hwndCaret, ref origin)) return null;
        return new Rectangle(origin.X, origin.Y, Math.Max(1, r.right - r.left), r.bottom - r.top);
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(1))) return; // a hung app: the background thread ends on its own
        _stop.Dispose();
    }
}
