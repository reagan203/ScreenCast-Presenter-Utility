using ScreenCastPresenter.Output;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Short entrance animations for SCP's own windows and pages (never the TV): a 150 ms fade and a
/// small upward slide, like Windows 11 flyouts and Settings pages. Off when the user has turned
/// off Settings › Accessibility › Visual effects › Animation effects.
/// </summary>
internal static class Motion
{
    public static readonly TimeSpan Entrance = TimeSpan.FromMilliseconds(160);

    /// <summary>Windows' "Animation effects" switch (SPI_GETCLIENTAREAANIMATION).</summary>
    public static unsafe bool Enabled
    {
        get
        {
            BOOL on = true;
            return !PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &on, 0) || on;
        }
    }

    /// <summary>Slides a control up into place from <paramref name="offset"/> pixels lower.</summary>
    public static Task SlideIn(Control control, int offset)
    {
        if (!Enabled || offset == 0) return Task.CompletedTask;
        int end = control.Top;
        return Fader.RunAsync(1, 0, Entrance, remaining =>
        {
            if (!control.IsDisposed) control.Top = end + (int)Math.Round(offset * remaining);
        });
    }

    /// <summary>Fades a window in and slides it up into place. The window must be shown already.</summary>
    public static Task Enter(Form form, int offset)
    {
        if (!Enabled) return Task.CompletedTask;
        int end = form.Top;
        form.Opacity = 0;
        return Fader.RunAsync(0, 1, Entrance, progress =>
        {
            if (form.IsDisposed) return;
            form.Opacity = progress;
            form.Top = end + (int)Math.Round(offset * (1 - progress));
        });
    }
}
