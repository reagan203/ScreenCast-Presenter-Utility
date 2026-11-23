using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Draws one source into the output window. Engines never show content at full brightness on
/// their own: they start at opacity 0 and the session controller fades them in and out.
/// All members are called on the UI thread; events are raised on the UI thread.
/// </summary>
internal interface ICaptureEngine : IDisposable
{
    /// <summary>Shown in Settings › Advanced and the palette (FR-32).</summary>
    string Name { get; }

    /// <summary>Can hold the last frame (freeze, minimised source). Only the WGC engine has frames.</summary>
    bool SupportsFreeze { get; }

    /// <summary>
    /// Starts drawing <paramref name="source"/> into <paramref name="output"/> at opacity 0.
    /// Completes once the first frame has reached the compositor, i.e. when the output may be
    /// uncloaked (NFR-12). Throws if the source can't be captured.
    /// </summary>
    Task StartAsync(SourceWindow source, nint output, Size outputClientSize);

    /// <summary>0 = black, 1 = full brightness. Never throws.</summary>
    void SetOpacity(double opacity);

    /// <summary>The source moved, resized, minimised or was restored. Never throws.</summary>
    void OnSourceChanged();

    /// <summary>The output window's client area changed size. Never throws.</summary>
    void OnOutputResized(Size outputClientSize);

    /// <summary>Show only part of the source (null = all). Never throws.</summary>
    void SetCrop(CropRegion? crop);

    /// <summary>Hold the current frame (true) or go live again. Ignored unless <see cref="SupportsFreeze"/>.</summary>
    void SetFrozen(bool frozen);

    /// <summary>
    /// Text for the idle screen while blanked (FR-27). Engines that cover the window with their
    /// own surface (WGC) draw it themselves; the DWM engine leaves it to the window.
    /// </summary>
    void SetIdleMessage(string? message, double brightness);

    /// <summary>The engine can't continue (GPU reset, capture refused, screen gone).</summary>
    event EventHandler<string>? Failed;
}
