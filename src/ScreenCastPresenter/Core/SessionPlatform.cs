using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;

namespace ScreenCastPresenter.Core;

/// <summary>The output window as the session controller sees it (lets tests use a fake).</summary>
internal interface IOutputSurface : IDisposable
{
    /// <summary>The screen it is on (a floating window: the screen it's on now).</summary>
    DisplayMonitor Monitor { get; }

    /// <summary>A floating window (FR-11) rather than fullscreen on a screen.</summary>
    bool IsFloating { get; }

    /// <summary>A floating window is minimised (Zoom then shares nothing).</summary>
    bool IsMinimized { get; }

    /// <summary>A floating window's normal position and size.</summary>
    Rectangle WindowBounds { get; }

    nint Handle { get; }
    Size ClientSize { get; }
    bool IsAlive { get; }
    event EventHandler? Resized;

    /// <summary>
    /// Private preview: hidden from screen capture. Used when a floating window shows the very
    /// screen it sits on, so sharing that screen in Teams/Zoom never shows the window (and never
    /// mirrors it endlessly). Normally off: Zoom must be able to share the output (FR-14).
    /// </summary>
    void SetPrivatePreview(bool on);

    /// <summary>The user closed the floating window.</summary>
    event EventHandler? CloseRequested;

    /// <summary>The floating window was minimised or restored.</summary>
    event EventHandler? MinimizedChanged;

    /// <summary>Shows the window while cloaked and paints it black.</summary>
    void ShowCloaked();

    void Reveal();
    void Conceal();

    /// <summary>Text on the idle screen (FR-27), drawn at <paramref name="brightness"/> 0..1 so it can fade.</summary>
    void SetIdleMessage(string? message, double brightness = 1);
}

/// <summary>Source notifications as the session controller sees them.</summary>
internal interface ISourceWatcher : IDisposable
{
    bool IsMinimized { get; }
    event EventHandler? Closed;
    event EventHandler? Changed;
    event EventHandler? Minimized;
    event EventHandler? Restored;
}

/// <summary>Everything the session controller needs from Windows; faked in unit tests.</summary>
internal interface ISessionPlatform
{
    IOutputSurface CreateOutput(OutputPlacement placement);

    /// <summary>The engine for a source (FR-32): screens and frozen starts need WGC; windows use DWM unless WGC is preferred.</summary>
    ICaptureEngine CreateEngine(SourceWindow source, bool startFrozen = false);

    /// <summary>The other engine, if it can show this source (a failing engine falls back, SRS 4.4).</summary>
    ICaptureEngine? CreateFallbackEngine(SourceWindow source, ICaptureEngine failed);

    ISourceWatcher Watch(SourceWindow source);
    Task Fade(double from, double to, TimeSpan duration, Action<double> apply);
}

internal sealed class Win32SessionPlatform(Func<AppSettings> settings) : ISessionPlatform
{
    public IOutputSurface CreateOutput(OutputPlacement placement) => new OutputWindow(placement);

    public ICaptureEngine CreateEngine(SourceWindow source, bool startFrozen = false)
    {
        bool wgc = source.IsScreen || startFrozen || settings().PreferWgc;
        return wgc ? new RemoteWgcEngine(settings().ShowCursor, startFrozen) : new DwmThumbnailEngine();
    }

    public ICaptureEngine? CreateFallbackEngine(SourceWindow source, ICaptureEngine failed) => failed switch
    {
        RemoteWgcEngine when !source.IsScreen => new DwmThumbnailEngine(),
        DwmThumbnailEngine when Direct3D.IsCaptureSupported => new RemoteWgcEngine(settings().ShowCursor, false),
        _ => null,
    };

    public ISourceWatcher Watch(SourceWindow source) => source.IsScreen ? new ScreenWatcher() : new WindowWatcher(source.Handle);

    public Task Fade(double from, double to, TimeSpan duration, Action<double> apply) => Fader.RunAsync(from, to, duration, apply);

    /// <summary>A screen can't be minimised or closed; unplugging is handled by the app's display-change logic.</summary>
    private sealed class ScreenWatcher : ISourceWatcher
    {
        public bool IsMinimized => false;
        public event EventHandler? Closed { add { } remove { } }
        public event EventHandler? Changed { add { } remove { } }
        public event EventHandler? Minimized { add { } remove { } }
        public event EventHandler? Restored { add { } remove { } }
        public void Dispose() { }
    }
}
