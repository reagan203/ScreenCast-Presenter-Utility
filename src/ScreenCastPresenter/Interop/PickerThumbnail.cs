using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// One live DWM thumbnail in the picker. Created only while the picker is open and only for cards
/// that are on screen; released when the card scrolls away or the picker closes (SRS 4.1).
/// </summary>
internal sealed class PickerThumbnail : IDisposable
{
    private nint _id;

    private PickerThumbnail(nint id) => _id = id;

    /// <summary>Null if DWM refuses (the window just closed, or it's a protected window).</summary>
    public static PickerThumbnail? TryCreate(nint destination, nint source) =>
        PInvoke.DwmRegisterThumbnail((HWND)destination, (HWND)source, out nint id).Succeeded ? new PickerThumbnail(id) : null;

    /// <param name="destination">Where to draw, in the picker window's client pixels.</param>
    /// <param name="source">Which part of the source window's client area to show.</param>
    public void Update(Rectangle destination, Rectangle source)
    {
        if (_id == 0) return;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_RECTSOURCE | PInvoke.DWM_TNP_VISIBLE |
                      PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { left = destination.Left, top = destination.Top, right = destination.Right, bottom = destination.Bottom },
            rcSource = new RECT { left = source.Left, top = source.Top, right = source.Right, bottom = source.Bottom },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };
        _ = PInvoke.DwmUpdateThumbnailProperties(_id, in props);
    }

    public void Dispose()
    {
        if (_id == 0) return;
        _ = PInvoke.DwmUnregisterThumbnail(_id);
        _id = 0;
    }
}
