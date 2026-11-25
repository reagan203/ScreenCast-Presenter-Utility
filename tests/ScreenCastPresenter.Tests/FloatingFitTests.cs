using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Tests;

/// <summary>The floating window takes the shape of what it shows: no black bars, kept while resizing.</summary>
public class FloatingFitTests
{
    private static readonly Size Min = new(304, 141), Max = new(1904, 980);

    [Theory]
    [InlineData(800, 480, 1920, 1080, 800, 450)]   // a wide screen: the height follows the width
    [InlineData(800, 600, 1080, 1920, 551, 980)]   // portrait, too tall at that width: the height is the screen's
    [InlineData(800, 450, 4000, 1000, 800, 200)]   // an ultra-wide strip: same width, a shorter window
    [InlineData(400, 300, 100, 3000, 33, 980)]     // a very tall sliver: the screen wins over the minimum width
    [InlineData(640, 360, 1280, 720, 640, 360)]    // already the right shape: unchanged
    public void The_client_area_takes_the_contents_shape(int cw, int ch, int contentW, int contentH, int expectedW, int expectedH) =>
        Assert.Equal(new Size(expectedW, expectedH), FloatingFit.Client(new Size(cw, ch), new Size(contentW, contentH), Min, Max));

    [Theory]
    [InlineData(0, 900)]
    [InlineData(1600, 0)]
    [InlineData(-5, -5)]
    public void Unknown_content_keeps_the_current_size(int w, int h) =>
        Assert.Equal(new Size(800, 480), FloatingFit.Client(new Size(800, 480), new Size(w, h), Min, Max));

    [Fact]
    public void A_pixel_of_rounding_is_the_same_shape()
    {
        Assert.True(FloatingFit.SameShape(new Size(1600, 900), new Size(1601, 900)));
        Assert.False(FloatingFit.SameShape(new Size(1600, 900), new Size(1600, 1000)));
        Assert.False(FloatingFit.SameShape(new Size(0, 900), new Size(1600, 900)));
    }

    private static readonly Size NonClient = new(16, 39); // borders and title bar

    [Theory]
    [InlineData(FloatingFit.Right, 100, 100, 816, 489)]       // drag the right edge: height follows, top-left stays
    [InlineData(FloatingFit.BottomRight, 100, 100, 816, 489)] // a corner: the same
    [InlineData(FloatingFit.Bottom, 100, 100, 816, 489)]      // the bottom edge: the width follows
    public void Dragging_keeps_the_shape(int edge, int x, int y, int w, int h)
    {
        // 16:9 content; the user dragged the window to 816 × 600 (client 800 × 561).
        Rectangle dragged = new(100, 100, 816, edge == FloatingFit.Bottom ? 489 : 600);
        Rectangle fitted = FloatingFit.Sizing(dragged, edge, NonClient, new Size(1600, 900), Min);
        Assert.Equal(new Rectangle(x, y, w, h), fitted);
        Assert.Equal(16.0 / 9, (double)(fitted.Width - NonClient.Width) / (fitted.Height - NonClient.Height), 2);
    }

    [Fact]
    public void Dragging_the_left_or_top_keeps_the_opposite_corner_still()
    {
        var dragged = Rectangle.FromLTRB(50, 40, 916, 640);
        Rectangle topLeft = FloatingFit.Sizing(dragged, FloatingFit.TopLeft, NonClient, new Size(1600, 900), Min);
        Assert.Equal(dragged.Right, topLeft.Right);
        Assert.Equal(dragged.Bottom, topLeft.Bottom);
        Rectangle left = FloatingFit.Sizing(dragged, FloatingFit.Left, NonClient, new Size(1600, 900), Min);
        Assert.Equal(dragged.Right, left.Right);
        Assert.Equal(dragged.Top, left.Top);
    }

    [Fact]
    public void Dragging_never_goes_below_the_minimum_size()
    {
        Rectangle tiny = FloatingFit.Sizing(new Rectangle(0, 0, 60, 60), FloatingFit.BottomRight, NonClient, new Size(1600, 900), Min);
        Assert.True(tiny.Width - NonClient.Width >= Min.Width);
        Assert.True(tiny.Height - NonClient.Height >= Min.Height);
    }

    [Fact]
    public void A_reshaped_window_stays_on_screen()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(new Rectangle(1120, 640, 800, 400), FloatingFit.KeepOn(new Rectangle(1500, 900, 800, 400), area));
        Assert.Equal(new Rectangle(0, 0, 800, 400), FloatingFit.KeepOn(new Rectangle(-50, -30, 800, 400), area));
    }

    // ───────────────────────────── what the source measures as ─────────────────────────────

    private static readonly SourceWindow Editor = new(1, "Code", "Code", false);

    [Fact]
    public void A_minimised_window_has_no_shape()
    {
        Assert.Null(Win32SessionPlatform.ContentSizeOf(Editor, minimized: true, new Size(1600, 900)));
        Assert.Equal(new Size(1600, 900), Win32SessionPlatform.ContentSizeOf(Editor, minimized: false, new Size(1600, 900)));
    }

    [Theory]
    [InlineData(160, 28)]  // the minimised title strip
    [InlineData(0, 0)]     // closed
    [InlineData(1200, 20)] // a sliver
    [InlineData(40, 900)]
    public void A_window_too_small_to_be_a_picture_has_no_shape(int w, int h) =>
        Assert.Null(Win32SessionPlatform.ContentSizeOf(Editor, minimized: false, new Size(w, h)));

    [Fact]
    public void A_crop_gives_the_shape_of_the_part_shown()
    {
        SourceWindow cropped = Editor with { Crop = CropRegion.Create(0, 0, 0.5, 0.5) };
        Assert.Equal(new Size(800, 450), Win32SessionPlatform.ContentSizeOf(cropped, minimized: false, new Size(1600, 900)));
    }

    [Fact]
    public void A_really_minimised_window_is_seen_as_minimised()
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(-3000, -3000, 600, 400), ShowInTaskbar = false };
        form.Show();
        form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        var source = new SourceWindow(form.Handle, "Test", "Test", true);
        Assert.Null(Win32SessionPlatform.ContentSizeOf(source, WindowEnumerator.IsMinimized(form.Handle), WindowEnumerator.ClientSize(form.Handle)));
    }

    // ───────────────────────────── the real window ─────────────────────────────

    private static DisplayMonitor Screen => DisplayHelpers.GetMonitors().First();

    [Fact]
    public void The_floating_window_has_only_a_close_button()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: true, OutputPlacement.DefaultWindow(Screen)));
        window.CreateControl();
        var style = (WINDOW_STYLE)PInvoke.GetWindowLong((HWND)window.Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        Assert.False(style.HasFlag(WINDOW_STYLE.WS_MINIMIZEBOX), "minimised, Zoom would share nothing");
        Assert.False(style.HasFlag(WINDOW_STYLE.WS_MAXIMIZEBOX));
        Assert.True(style.HasFlag(WINDOW_STYLE.WS_SYSMENU), "✕ stays: it stops showing");
        Assert.True(style.HasFlag(WINDOW_STYLE.WS_THICKFRAME), "still resizable");
    }

    [Fact]
    public void The_real_floating_window_reshapes_to_its_content_at_once_the_first_time()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: false, new Rectangle(Screen.WorkArea.Left + 20, Screen.WorkArea.Top + 20, 700, 700)));
        window.CreateControl();
        _ = window.Handle;
        int width = window.ClientSize.Width;
        window.SetContentAspect(new Size(1600, 900));
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(1600.0 / 900, (double)window.ClientSize.Width / window.ClientSize.Height, 1);
        window.SetContentAspect(null); // nothing shown: keeps its shape
        Assert.Equal(1600.0 / 900, (double)window.ClientSize.Width / window.ClientSize.Height, 1);
    }

    [Fact]
    public void A_full_screen_output_ignores_content_shapes()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: false));
        window.CreateControl();
        Size before = window.ClientSize;
        window.SetContentAspect(new Size(900, 1600));
        Assert.Equal(before, window.ClientSize);
    }
}
