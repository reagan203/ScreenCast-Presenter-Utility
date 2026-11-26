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

/// <summary>
/// An engine that draws marks over the picture, inside the output (FR-65, FR-71): only the WGC
/// engine can (the DWM engine never touches pixels). Points are fractions of the content's whole
/// area. None of these throw.
/// </summary>
internal interface IEffectsEngine
{
    /// <summary>A click ring at <paramref name="at"/> (blue for a right click).</summary>
    void Click(PointF at, bool secondary);

    /// <summary>The ring around the pointer; null hides it.</summary>
    void Pointer(PointF? at);

    /// <summary>The laser dot; null hides it.</summary>
    void Laser(PointF? at);

    /// <summary>Draw the real mouse pointer (off while the laser stands in for it).</summary>
    void ShowCursor(bool on);

    void InkStart(InkStroke stroke);
    void InkAdd(int id, IReadOnlyList<PointF> points);
    void InkRemove(int id);
    void InkClear();

    /// <summary>A mark's points replaced: an arrow or box following the drag (FR-78).</summary>
    void InkSet(int id, IReadOnlyList<PointF> points);

    /// <summary>Parts of the shown window or screen covered for the audience (FR-74); an empty list shows everything.</summary>
    void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style);

    /// <summary>Everything dimmed but a circle around <paramref name="at"/> (FR-77); null turns it off.</summary>
    void Spotlight(PointF? at);

    /// <summary>A name banner along the bottom (FR-79); null takes it off.</summary>
    void Caption(string? name, string? second);

    /// <summary>The picture shown above the blank screen's message (FR-80), a PNG or JPEG file; null for none.</summary>
    void Logo(string? path);

    /// <summary>A QR code in a corner (FR-81); null takes it off.</summary>
    void Qr(string? text, InsetCorner corner);

    /// <summary>Live captions along the bottom (FR-84): the latest words; null takes them off.</summary>
    void LiveCaption(string? text);

    /// <summary>Live captions' size and place (FR-89).</summary>
    void CaptionStyle(CaptionLook look) { }

    /// <summary>Automatic hiding (FR-88): what to look for and cover; <see cref="SensitiveKinds.None"/> stops it.</summary>
    void AutoHide(SensitiveKinds kinds, RedactStyle style) { }

    /// <summary>The presenter's own words automatic hiding covers (when <see cref="SensitiveKinds.Custom"/> is on).</summary>
    void AutoHideWords(IReadOnlyList<string> words) { }

    /// <summary>Automatic hiding covered something new: what, in words ("2 email addresses"), on the UI thread.</summary>
    event EventHandler<string>? AutoHidden { add { } remove { } }

    /// <summary>The small picture in a corner (FR-72): another window, a screen or a camera; null takes it off.</summary>
    void Inset(SourceWindow? source, InsetCorner corner, int percent);

    /// <summary>The small picture couldn't start or went away (closed, unplugged): it's off, and this says why.</summary>
    event EventHandler<string>? InsetFailed;
}
