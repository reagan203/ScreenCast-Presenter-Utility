using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Default engine for windows (FR-30). DWM itself draws the live source window inside the output
/// window, so no pixels ever pass through this process: zero copies, roughly zero CPU and memory.
/// Crops use DWM's own source rectangle. Limitations: it can't draw a minimised window, can't
/// show the mouse pointer, has no frames to freeze, and can't capture whole screens — the WGC
/// engine covers those.
/// </summary>
internal sealed class DwmThumbnailEngine : ICaptureEngine
{
    private nint _thumbnail;
    private HWND _source;
    private Size _outputSize;
    private byte _opacity;
    private CropRegion? _crop;

    public string Name => "DWM thumbnail";
    public bool SupportsFreeze => false;

    public event EventHandler<string>? Failed { add { } remove { } } // DWM never fails on its own; the watcher reports closed windows

    public Task StartAsync(SourceWindow source, nint output, Size outputClientSize)
    {
        if (_thumbnail != 0) throw new InvalidOperationException("Engine already started.");
        if (source.IsScreen) throw new NotSupportedException("DWM thumbnails can't show a whole screen.");

        _source = (HWND)source.Handle;
        _outputSize = outputClientSize;
        _crop = source.Crop;
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

    public void SetCrop(CropRegion? crop)
    {
        _crop = crop;
        _ = ApplyProperties();
    }

    public void SetFrozen(bool frozen) { }

    public void SetIdleMessage(string? message, double brightness) { } // the output window paints it

    /// <summary>Pushes position, crop, opacity and visibility to DWM. Returns the failure instead of throwing.</summary>
    private HRESULT ApplyProperties()
    {
        if (_thumbnail == 0) return default; // S_OK

        // Fit the source's client area (or the cropped part of it) inside the output with letterboxing.
        Rectangle dest = Rectangle.Empty;
        Rectangle source = Rectangle.Empty;
        if (!PInvoke.IsIconic(_source) && PInvoke.GetClientRect(_source, out RECT client))
        {
            var clientSize = new Size(client.right - client.left, client.bottom - client.top);
            source = _crop is { } crop ? crop.ToPixels(clientSize) : new Rectangle(Point.Empty, clientSize);
            dest = AspectFit.Fit(source.Size, _outputSize);
        }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_RECTSOURCE | PInvoke.DWM_TNP_OPACITY |
                      PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { left = dest.Left, top = dest.Top, right = dest.Right, bottom = dest.Bottom },
            rcSource = new RECT { left = source.Left, top = source.Top, right = source.Right, bottom = source.Bottom },
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
