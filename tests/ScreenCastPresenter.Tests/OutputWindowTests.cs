using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Tests;

/// <summary>Real output windows (no display needed to be unlocked): the styles Zoom and privacy depend on.</summary>
public class OutputWindowTests
{
    private static DisplayMonitor Screen => DisplayHelpers.GetMonitors().First();

    private static (WINDOW_STYLE Style, WINDOW_EX_STYLE Ex) Styles(nint hwnd) =>
        ((WINDOW_STYLE)PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE),
         (WINDOW_EX_STYLE)PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));

    [Fact]
    public void Floating_window_is_an_ordinary_sizable_window_Zoom_can_list()
    {
        Rectangle bounds = OutputPlacement.DefaultWindow(Screen);
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: true, bounds));
        window.CreateControl();
        (WINDOW_STYLE style, WINDOW_EX_STYLE ex) = Styles(window.Handle);

        Assert.True(window.IsFloating);
        Assert.True(style.HasFlag(WINDOW_STYLE.WS_THICKFRAME), "resizable");
        Assert.True(style.HasFlag(WINDOW_STYLE.WS_CAPTION), "has a title bar to drag");
        Assert.False(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW), "tool windows don't appear in Zoom's window list");
        Assert.False(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_NOACTIVATE));
        Assert.True(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST), "kept on top while presenting (the default), like Teams' preview");
        Assert.True(window.ShowInTaskbar);
        Assert.Equal("ScreenCast Presenter output", window.Text);
        Assert.False(DwmHelpers.IsExcludedFromCapture((HWND)window.Handle), "Zoom must see it (FR-14)");
        Assert.True(DwmHelpers.IsCloaked((HWND)window.Handle), "created cloaked (NFR-12)");
        Assert.Equal(bounds, window.WindowBounds);
    }

    [Fact]
    public void Floating_window_can_be_an_ordinary_window_too()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: false, OutputPlacement.DefaultWindow(Screen)));
        window.CreateControl();
        Assert.False(Styles(window.Handle).Ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST));
    }

    [Fact]
    public void Private_preview_hides_the_window_from_capture_and_back()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, true, OutputPlacement.DefaultWindow(Screen)));
        window.ShowCloaked(); // as in the app: shown (cloaked, so nothing appears) before it shows a source
        window.SetPrivatePreview(true);
        Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)window.Handle));
        window.SetPrivatePreview(false);
        Assert.False(DwmHelpers.IsExcludedFromCapture((HWND)window.Handle), "back to shareable (FR-14)");
    }

    [Fact]
    public void Fullscreen_output_stays_out_of_alt_tab_and_the_taskbar()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: true));
        window.CreateControl();
        (WINDOW_STYLE style, WINDOW_EX_STYLE ex) = Styles(window.Handle);

        Assert.False(window.IsFloating);
        Assert.False(style.HasFlag(WINDOW_STYLE.WS_CAPTION));
        Assert.True(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW), "FR-13");
        Assert.True(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_NOACTIVATE));
        Assert.True(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST));
        Assert.False(window.ShowInTaskbar);
        Assert.False(DwmHelpers.IsExcludedFromCapture((HWND)window.Handle));
        Assert.Equal(Screen.Bounds, window.Bounds);
    }

    [Fact]
    public void Closing_the_floating_window_asks_the_session_to_stop_instead_of_vanishing()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, false, OutputPlacement.DefaultWindow(Screen)));
        window.ShowCloaked(); // shown (the ✕ only exists on a shown window) but cloaked, so nothing appears
        int requests = 0;
        window.CloseRequested += (_, _) => requests++;
        window.Close(); // what the ✕ does
        Assert.Equal(1, requests);
        Assert.False(window.IsDisposed); // still there: the session fades it out and closes it
    }
}
