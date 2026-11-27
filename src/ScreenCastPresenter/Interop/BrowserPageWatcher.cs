using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// Asks a browser window where its page is (FR-133), every <see cref="BrowserPage.Every"/>, on its
/// own thread (a browser that hangs can't hang the app), and reports changes on the thread that
/// started it. One window at a time.
/// </summary>
internal sealed class BrowserPageWatcher : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Action<nint, CropRegion?> _changed;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _sync = new();
    private Thread? _thread;
    private nint _window;
    private CropRegion? _shown;
    private int _generation;
    private volatile bool _stop;

    /// <param name="changed">The window and its page's crop (null: no page found), on the starting thread.</param>
    public BrowserPageWatcher(Action<nint, CropRegion?> changed)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _changed = changed;
    }

    /// <summary>Watch <paramref name="window"/> (0 stops watching), starting from the page crop <paramref name="shown"/> now.</summary>
    public void Watch(nint window, CropRegion? shown = null)
    {
        lock (_sync)
        {
            if (window != _window)
            {
                _window = window;
                _shown = shown;
                _generation++;
            }
        }
        if (window != 0 && _thread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "SCP browser page" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
        _wake.Set();
    }

    /// <summary>Asks once, now, on the calling thread (the first answer before something is shown): the page's crop, and whether that's sure.</summary>
    public static (CropRegion? Crop, bool Sure) AskNow(nint window)
    {
        Rectangle client = WindowEnumerator.ClientBoundsOnScreen(window);
        if (ChromiumPage(window) is { } page) return BrowserPage.Read(client, page);
        using UiAutomation? uia = UiAutomation.Create();
        return uia is null ? (null, false) : BrowserPage.Read(client, uia.DocumentIn(window));
    }

    /// <summary>
    /// Chrome, Edge, Brave and the other Chromium browsers draw the page in a child window of its
    /// own: its place, found cheaply, without asking the browser through UI Automation (which on a
    /// busy page costs more). The biggest one visible is the page (docked developer tools are smaller).
    /// </summary>
    internal static Rectangle? ChromiumPage(nint window)
    {
        Rectangle? best = null;
        Windows.Win32.PInvoke.EnumChildWindows((Windows.Win32.Foundation.HWND)window, (child, _) =>
        {
            if (DesktopIcons.ClassOf(child) == "Chrome_RenderWidgetHostHWND" && Windows.Win32.PInvoke.IsWindowVisible(child)
                && Windows.Win32.PInvoke.GetWindowRect(child, out Windows.Win32.Foundation.RECT r))
            {
                var rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
                if (rect.Width > 0 && rect.Height > 0 && (best is not { } b || (long)rect.Width * rect.Height > (long)b.Width * b.Height)) best = rect;
            }
            return true;
        }, default);
        return best;
    }

    private void Run()
    {
        using UiAutomation? uia = UiAutomation.Create();
        int generation = -1;
        CropRegion? shown = null; // what the picture shows now (as last told, or as it started)
        int unsure = 0;
        while (!_stop)
        {
            nint window;
            lock (_sync)
            {
                window = _window;
                if (generation != _generation)
                {
                    // Another window (or the same one shown again): start from what it shows.
                    generation = _generation;
                    shown = _shown;
                    unsure = 0;
                }
            }
            if (window != 0 && uia is not null && WindowEnumerator.IsAlive(window) && !WindowEnumerator.IsMinimized(window))
            {
                (CropRegion? Crop, bool Sure) answer = BrowserPage.Read(WindowEnumerator.ClientBoundsOnScreen(window), ChromiumPage(window) ?? uia.DocumentIn(window));
                CropRegion? crop = BrowserPage.Steady(shown, answer, ref unsure);
                if (!BrowserPage.Same(crop, shown))
                {
                    shown = crop;
                    _ui.Post(_ => { if (!_stop) _changed(window, crop); }, null);
                }
            }
            _wake.WaitOne(window == 0 ? Timeout.Infinite : (int)BrowserPage.Every.TotalMilliseconds);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
