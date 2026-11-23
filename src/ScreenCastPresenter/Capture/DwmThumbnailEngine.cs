using System.Drawing;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Default engine (FR-30). DWM itself draws the live source window inside the output window, so
/// no pixels ever pass through this process: zero copies, roughly zero CPU and memory.
/// Limitation: DWM can't draw a minimised window, so the output is black until it is restored
/// (M3 adds the WGC fallback for that).
/// </summary>
internal sealed class DwmThumbnailEngine : ICaptureEngine
{
    private nint _thumbnail;
    private HWND _source;
    private Size _outputSize;
    private byte _opacity;

    public string Name => "DWM thumbnail";

    public Task StartAsync(nint source, nint output, Size outputClientSize)
    {
        if (_thumbnail != 0) throw new InvalidOperationException("Engine already started.");

        _source = (HWND)source;
        _outputSize = outputClientSize;
        _opacity = 0;

        PInvoke.DwmRegisterThumbnail((HWND)output, _source, out _thumbnail).ThrowOnFailure();
        ApplyProperties().ThrowOnFailure();

        // DWM gives no "first frame drawn" signal (NFR-12). DwmFlush waits until the compositor
        // has picked up the properties above; the thumbnail is at opacity 0, so whatever DWM
        // shows first is invisible and the output can now be uncloaked safely.
        DwmHelpers.Flush();
        return Task.CompletedTask;
    }

    public void SetOpacity(double opacity)
    {
        _opacity = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        _ = ApplyProperties();
    }

    public void OnSourceChanged() => _ = ApplyProperties();

    public void OnOutputResized(Size outputClientSize)
    {
        _outputSize = outputClientSize;
        _ = ApplyProperties();
    }

    /// <summary>Pushes position, opacity and visibility to DWM. Returns the failure instead of throwing.</summary>
    private HRESULT ApplyProperties()
    {
        if (_thumbnail == 0) return default; // S_OK

        // Fit the source's client area (no title bar or borders) inside the output with letterboxing.
        Rectangle dest = Rectangle.Empty;
        if (!PInvoke.IsIconic(_source) && PInvoke.GetClientRect(_source, out RECT client))
            dest = AspectFit.Fit(new Size(client.right - client.left, client.bottom - client.top), _outputSize);

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_OPACITY |
                      PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { left = dest.Left, top = dest.Top, right = dest.Right, bottom = dest.Bottom },
            opacity = _opacity,
            fVisible = !dest.IsEmpty,
            fSourceClientAreaOnly = true,
        };
        return PInvoke.DwmUpdateThumbnailProperties(_thumbnail, in props);
    }

    public void Dispose()
    {
        if (_thumbnail == 0) return;
        _ = PInvoke.DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = 0;
    }
}
