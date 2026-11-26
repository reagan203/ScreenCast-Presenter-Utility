using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-71: the drawing layer, as real (unshown) windows: where lines go, and that the audience never sees the layer.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class DrawingOverlayTests
{
    // A window's client area at (100, 100), 800 × 400, cropped to its right half.
    private static readonly Rectangle Area = new(100, 100, 800, 400);
    private static readonly Rectangle Shown = new(500, 100, 400, 400);

    private sealed class Effects : IEffectsEngine
    {
        public List<string> Calls { get; } = [];
        public List<PointF> Points { get; } = [];
        public void Click(PointF at, bool secondary) { }
        public void Pointer(PointF? at) { }
        public void Laser(PointF? at) { }
        public void ShowCursor(bool on) { }
        public void InkStart(InkStroke stroke) => Calls.Add($"start {stroke.Tool}");
        public void InkAdd(int id, IReadOnlyList<PointF> points) => Points.AddRange(points);
        public void InkRemove(int id) => Calls.Add("remove");
        public void InkClear() => Calls.Add("clear");
        public void Inset(SourceWindow? source, InsetCorner corner, int percent) { }
        public event EventHandler<string>? InsetFailed { add { } remove { } }
        public void InkSet(int id, IReadOnlyList<PointF> points)
        {
            Calls.Add("set");
            Points.AddRange(points);
        }
        public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) { }
        public void Spotlight(PointF? at) { }
        public void Caption(string? name, string? second) { }
        public void Logo(string? path) { }
        public void Qr(string? text, InsetCorner corner) { }
        public void LiveCaption(string? text) { }
    }

    [Fact]
    public void The_layer_covers_what_the_audience_sees_and_is_hidden_from_capture()
    {
        var drawing = new InkDrawing();
        using var overlay = new DrawingOverlay(drawing, () => null, () => (Area, Shown), 1);
        (nint canvas, nint toolbar) = overlay.CreateWindows();
        Assert.Equal(Shown, overlay.CanvasBounds);
        var ex = (WINDOW_EX_STYLE)PInvoke.GetWindowLong((HWND)canvas, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        Assert.True(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_LAYERED));
        Assert.True(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW), "not in Alt+Tab or the taskbar");
        Assert.False(ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TRANSPARENT), "it must catch the pen, not pass it to the app");
        if (Environment.GetEnvironmentVariable("SCP_DEV_ALLOW_CAPTURE") != "1")
        {
            Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)canvas), "the layer never reaches a meeting or the TV");
            Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)toolbar), "nor does the toolbar");
        }
    }

    [Fact]
    public void A_line_drawn_on_screen_reaches_the_TV_as_fractions_of_the_content()
    {
        var drawing = new InkDrawing();
        var effects = new Effects();
        using var overlay = new DrawingOverlay(drawing, () => effects, () => (Area, Shown), 1);
        overlay.CreateWindows();
        overlay.BeginStroke(new Point(500, 300));  // the middle of the window's left edge of the shown half
        overlay.AddPoint(new Point(900, 500));     // its bottom-right corner
        overlay.AddPoint(new Point(900, 500));     // didn't move: nothing new
        overlay.EndStroke();

        InkStroke stroke = Assert.Single(drawing.Strokes);
        Assert.Equal([new PointF(0.5f, 0.5f), new PointF(1f, 1f)], stroke.Points);
        Assert.Equal(["start Pen"], effects.Calls);
        Assert.Equal(stroke.Points, effects.Points);
        Assert.Equal(DrawingOverlay.PenWidth, stroke.Width);
    }

    [Fact]
    public void Tools_colours_undo_and_clear()
    {
        var drawing = new InkDrawing();
        var effects = new Effects();
        using var overlay = new DrawingOverlay(drawing, () => effects, () => (Area, Shown), 1);
        overlay.CreateWindows();
        Assert.True(overlay.HandleKey(Keys.H));
        Assert.Equal(InkTool.Highlighter, overlay.Tool);
        overlay.SetColour(Color.FromArgb(0x2E, 0xB8, 0x72));
        overlay.BeginStroke(new Point(600, 200));
        overlay.EndStroke();
        Assert.Equal(DrawingOverlay.HighlighterWidth, drawing.Strokes[0].Width);
        Assert.Equal(Color.FromArgb(0x2E, 0xB8, 0x72).ToArgb(), drawing.Strokes[0].Argb);

        Assert.True(overlay.HandleKey(Keys.Control | Keys.Z));
        Assert.Empty(drawing.Strokes);
        Assert.Contains("remove", effects.Calls);

        overlay.BeginStroke(new Point(600, 200));
        overlay.EndStroke();
        Assert.True(overlay.HandleKey(Keys.Delete));
        Assert.Empty(drawing.Strokes);
        Assert.Contains("clear", effects.Calls);
    }

    [Fact]
    public void Esc_ends_drawing_and_L_hands_over_to_the_laser()
    {
        using var overlay = new DrawingOverlay(new InkDrawing(), () => null, () => (Area, Shown), 1);
        overlay.CreateWindows();
        bool closed = false, laser = false;
        overlay.Closed += (_, _) => closed = true;
        overlay.LaserChosen += (_, _) => laser = true;
        Assert.True(overlay.HandleKey(Keys.L));
        Assert.True(closed && laser);
    }

    [Fact]
    public void When_what_is_shown_goes_out_of_view_drawing_ends()
    {
        (Rectangle, Rectangle)? geometry = (Area, Shown);
        using var overlay = new DrawingOverlay(new InkDrawing(), () => null, () => geometry, 1);
        overlay.CreateWindows();
        bool closed = false;
        overlay.Closed += (_, _) => closed = true;
        geometry = null; // minimised, closed or moved to the TV
        Assert.False(overlay.UpdateGeometry());
        Assert.True(closed);
    }

    [Fact]
    public void Lines_stay_on_the_content_when_the_window_moves()
    {
        var drawing = new InkDrawing();
        (Rectangle Area, Rectangle Shown) geometry = (Area, Shown);
        using var overlay = new DrawingOverlay(drawing, () => null, () => geometry, 1);
        overlay.CreateWindows();
        overlay.BeginStroke(new Point(700, 300));
        overlay.EndStroke();
        PointF before = drawing.Strokes[0].Points[0];
        geometry = (Rectangle.Inflate(Area, 0, 0) with { X = 300 }, Shown with { X = 700 }); // moved 200 px right
        Assert.True(overlay.UpdateGeometry());
        Assert.Equal(new Rectangle(700, 100, 400, 400), overlay.CanvasBounds);
        Assert.Equal(before, drawing.Strokes[0].Points[0]); // stored relative to the content, so nothing to redo
    }
}
