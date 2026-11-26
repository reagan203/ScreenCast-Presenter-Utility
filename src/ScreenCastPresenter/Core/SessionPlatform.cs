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

    /// <summary>Your logo above the black screen's message (FR-80), painted while nothing draws over the window; null for none.</summary>
    void SetLogo(string? path) { }

    /// <summary>The user closed the floating window.</summary>
    event EventHandler? CloseRequested;

    /// <summary>The floating window was minimised or restored.</summary>
    event EventHandler? MinimizedChanged;

    /// <summary>Double-click on the output: take the presenter to what it shows.</summary>
    event EventHandler? DoubleClicked;

    /// <summary>
    /// The size of what is shown (null: nothing, or unknown). A floating window takes its shape,
    /// so there are no black bars; a full-screen output ignores it.
    /// </summary>
    void SetContentAspect(Size? content);

    /// <summary>
    /// A floating window steps behind other windows (true) or comes back to the front (false),
    /// without being hidden or minimised, so a meeting sharing it keeps sharing it.
    /// </summary>
    void StepBack(bool back);

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

    /// <summary>The size of what <paramref name="source"/> shows: the window's client area or the screen, cropped. Null if unknown.</summary>
    Size? ContentSize(SourceWindow source);
    Task Fade(double from, double to, TimeSpan duration, Action<double> apply);
}

/// <summary>
/// How something is shown. The light way (a DWM thumbnail) costs almost nothing but can't draw the
/// mouse pointer, show a whole screen, or freeze; Windows Graphics Capture (in the helper process)
/// can. So it's used for screens, freezing, the Advanced choice, and whenever the pointer should be
/// on the TV: a switch that says "pointer on" must really show it.
/// </summary>
internal static class CaptureChoice
{
    public static bool DrawsPointerAndScreens(bool isScreen, bool startFrozen, bool preferWgc, bool showPointer) =>
        isScreen || startFrozen || preferWgc || showPointer;
}

/// <param name="marksWanted">Marks on the TV are on (click rings, laser, drawing…): only the helper's engine can draw them.</param>
internal sealed class Win32SessionPlatform(Func<AppSettings> settings, Func<bool>? marksWanted = null) : ISessionPlatform
{
    public IOutputSurface CreateOutput(OutputPlacement placement) => new OutputWindow(placement);

    public ICaptureEngine CreateEngine(SourceWindow source, bool startFrozen = false)
    {
        AppSettings s = settings();
        // A camera has no window for the light way to show: always the helper (FR-68). So does
        // anything with parts hidden from the audience: the light way can't cover them (FR-74).
        return CaptureChoice.DrawsPointerAndScreens(!source.IsWindow, startFrozen, s.PreferWgc, s.ShowCursor || marksWanted?.Invoke() == true || HidesParts(s, source) || s.AutoHideKinds != SensitiveKinds.None)
            ? new RemoteWgcEngine(s.ShowCursor, startFrozen) { StartAutoHide = s.AutoHideKinds, StartAutoStyle = s.HiddenPartStyle, StartAutoWords = s.AutoHideWords }
            : new DwmThumbnailEngine();
    }

    private static bool HidesParts(AppSettings s, SourceWindow source) => Redactions.For(s, source).Count > 0;

    public ICaptureEngine? CreateFallbackEngine(SourceWindow source, ICaptureEngine failed) => failed switch
    {
        // Never the light way for something with hidden parts: it would show them.
        RemoteWgcEngine when source.IsWindow && !HidesParts(settings(), source) && settings().AutoHideKinds == SensitiveKinds.None => new DwmThumbnailEngine(),
        DwmThumbnailEngine when Direct3D.IsCaptureSupported => new RemoteWgcEngine(settings().ShowCursor, false) { StartAutoHide = settings().AutoHideKinds, StartAutoStyle = settings().HiddenPartStyle, StartAutoWords = settings().AutoHideWords },
        _ => null,
    };

    /// <summary>A window is watched for closing and moving; a screen or a camera reports trouble through its engine.</summary>
    public ISourceWatcher Watch(SourceWindow source) => source.IsWindow ? new WindowWatcher(source.Handle) : new ScreenWatcher();

    public Size? ContentSize(SourceWindow source) => source.IsOffScreen ? null // its picture size isn't known here: the window keeps its shape
        : ContentSizeOf(source, WindowEnumerator.IsMinimized(source.Handle), WindowEnumerator.ClientSize(source.Handle));

    /// <summary>
    /// A minimised window measures as the small strip Windows shrinks it to (apps that draw their
    /// own title bar report it as client area), and would squash the floating window into a black
    /// sliver; so a minimised or implausibly small window has no shape, and the window keeps its
    /// last one until the source is restored.
    /// </summary>
    internal static Size? ContentSizeOf(SourceWindow source, bool minimized, Size client)
    {
        Size whole = source.Screen is { } screen ? screen.Bounds.Size : client;
        if (source.Screen is null && minimized) return null;
        if (whole.Width < MinimumContent.Width || whole.Height < MinimumContent.Height) return null; // closed, or not a real picture
        Size shown = source.Crop is { } crop ? crop.ToPixels(whole).Size : whole;
        return shown.Width > 0 && shown.Height > 0 ? shown : null;
    }

    /// <summary>Anything smaller isn't a picture worth reshaping the window for (a minimised title strip is 160 × 28).</summary>
    internal static readonly Size MinimumContent = new(80, 60);

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
