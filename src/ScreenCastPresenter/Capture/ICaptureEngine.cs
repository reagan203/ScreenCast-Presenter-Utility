using System.Drawing;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Draws one source into the output window. Engines never show content at full brightness on
/// their own: they start at opacity 0 and the session controller fades them in and out.
/// </summary>
internal interface ICaptureEngine : IDisposable
{
    /// <summary>Shown in Settings → Advanced (FR-32).</summary>
    string Name { get; }

    /// <summary>
    /// Starts drawing <paramref name="source"/> into <paramref name="output"/> at opacity 0.
    /// Completes once the first frame has reached the compositor, i.e. when the output may be
    /// uncloaked (NFR-12). Throws if the source can't be captured.
    /// </summary>
    Task StartAsync(nint source, nint output, Size outputClientSize);

    /// <summary>0 = black, 1 = full brightness. Never throws.</summary>
    void SetOpacity(double opacity);

    /// <summary>The source moved, resized, minimised or was restored. Never throws.</summary>
    void OnSourceChanged();

    /// <summary>The output window's client area changed size. Never throws.</summary>
    void OnOutputResized(Size outputClientSize);
}
