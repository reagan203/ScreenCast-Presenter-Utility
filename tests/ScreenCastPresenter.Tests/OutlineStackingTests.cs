using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// The yellow outline marks only the presented window: it sits directly above that window, so a
/// window in front of the presented one covers the outline too. Around a whole screen it stays on top.
/// Uses real windows for a moment in a corner of the main screen.
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public class OutlineStackingTests
{
    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.25, 2)]
    [InlineData(1.5, 3)]
    [InlineData(2.0, 4)]
    [InlineData(0.5, 1)]
    [InlineData(0.1, 1)]
    public void The_outline_is_thin_but_never_vanishes(double scale, int pixels) =>
        Assert.Equal(pixels, SourceOutline.Thickness(scale));

    private static Form Window(Rectangle bounds)
    {
        var form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = bounds, ShowInTaskbar = false, BackColor = Color.DimGray };
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static bool IsTopMost(nint hwnd) =>
        ((WINDOW_EX_STYLE)PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);

    /// <summary>The windows directly above <paramref name="hwnd"/>, nearest first.</summary>
    private static List<nint> Above(nint hwnd, int count)
    {
        var list = new List<nint>();
        for (HWND w = PInvoke.GetWindow((HWND)hwnd, GET_WINDOW_CMD.GW_HWNDPREV); !w.IsNull && list.Count < count; w = PInvoke.GetWindow(w, GET_WINDOW_CMD.GW_HWNDPREV))
            list.Add((nint)w);
        return list;
    }

    private static List<nint> OutlineWindows(SourceOutline outline) =>
        typeof(SourceOutline).GetProperty("Overlays", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(outline) is IEnumerable<Form> forms ? forms.Select(f => f.Handle).ToList() : [];

    [Fact]
    public void Around_a_window_the_outline_sits_just_above_it_and_under_windows_in_front()
    {
        List<DisplayMonitor> monitors = DisplayHelpers.GetMonitors();
        Rectangle work = monitors.First(m => m.IsPrimary).WorkArea;
        var sourceBounds = new Rectangle(work.Right - 260, work.Bottom - 200, 200, 150);
        using Form source = Window(sourceBounds);
        using Form cover = Window(new Rectangle(sourceBounds.X - 40, sourceBounds.Y - 40, 120, 120)); // overlaps the corner
        using var outline = new SourceOutline();

        outline.Update(sourceBounds, "Showing on TV", monitors, output: null, window: source.Handle);
        Application.DoEvents();
        List<nint> strips = OutlineWindows(outline);
        Assert.NotEmpty(strips);
        Assert.All(strips, s => Assert.False(IsTopMost(s), "not above every window any more"));

        List<nint> above = Above(source.Handle, strips.Count + 1);
        Assert.Equal(strips.OrderBy(h => h), above.Take(strips.Count).OrderBy(h => h)); // the outline, right above the source
        Assert.Contains(cover.Handle, Above(source.Handle, strips.Count + 5)); // and the window in front stays in front of it

        // The presented window comes to the front: the outline follows it there.
        source.Activate();
        PInvoke.SetWindowPos((HWND)source.Handle, (HWND)(nint)0, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        outline.Update(sourceBounds, "Showing on TV", monitors, output: null, window: source.Handle);
        Application.DoEvents();
        Assert.Equal(strips.OrderBy(h => h), Above(source.Handle, strips.Count).OrderBy(h => h));
        Assert.DoesNotContain(cover.Handle, Above(source.Handle, strips.Count + 5));
    }

    [Fact]
    public void Around_a_whole_screen_the_outline_stays_on_top()
    {
        List<DisplayMonitor> monitors = DisplayHelpers.GetMonitors();
        using var outline = new SourceOutline();
        outline.Update(monitors[0].Bounds, "Showing on TV", monitors, output: null, window: 0);
        Application.DoEvents();
        List<nint> strips = OutlineWindows(outline);
        Assert.NotEmpty(strips);
        Assert.All(strips, s => Assert.True(IsTopMost(s)));
        Assert.All(strips, s => Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)s), "never in a screen share"));
    }
}
