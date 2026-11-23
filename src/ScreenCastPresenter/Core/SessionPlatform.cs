using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;

namespace ScreenCastPresenter.Core;

/// <summary>The output window as the session controller sees it (lets tests use a fake).</summary>
internal interface IOutputSurface : IDisposable
{
    DisplayMonitor Monitor { get; }
    nint Handle { get; }
    Size ClientSize { get; }
    bool IsAlive { get; }
    event EventHandler? Resized;

    /// <summary>Shows the window while cloaked and paints it black.</summary>
    void ShowCloaked();

    void Reveal();
    void Conceal();

    /// <summary>Text on the idle screen (FR-27), drawn at <paramref name="brightness"/> 0..1 so it can fade.</summary>
    void SetIdleMessage(string? message, double brightness = 1);
}

/// <summary>Source window notifications as the session controller sees them.</summary>
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
    IOutputSurface CreateOutput(DisplayMonitor monitor, bool topMost);
    ICaptureEngine CreateEngine(SourceWindow source);
    ISourceWatcher Watch(nint hwnd);
    Task Fade(double from, double to, TimeSpan duration, Action<double> apply);
}

internal sealed class Win32SessionPlatform : ISessionPlatform
{
    public IOutputSurface CreateOutput(DisplayMonitor monitor, bool topMost) => new OutputWindow(monitor, topMost);

    // M3 adds the WGC engine and picks between the two here.
    public ICaptureEngine CreateEngine(SourceWindow source) => new DwmThumbnailEngine();

    public ISourceWatcher Watch(nint hwnd) => new WindowWatcher(hwnd);

    public Task Fade(double from, double to, TimeSpan duration, Action<double> apply) => Fader.RunAsync(from, to, duration, apply);
}
