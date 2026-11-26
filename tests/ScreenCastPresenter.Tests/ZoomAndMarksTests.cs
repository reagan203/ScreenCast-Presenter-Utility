using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-64: zooming in around the pointer.</summary>
public class ZoomViewTests
{
    [Fact]
    public void A_view_is_centred_on_the_pointer_and_level_times_smaller()
    {
        CropRegion v = ZoomView.View(new PointF(0.5f, 0.5f), 2, null);
        Assert.Equal(0.25, v.X, 6);
        Assert.Equal(0.25, v.Y, 6);
        Assert.Equal(0.5, v.Width, 6);
        Assert.Equal(0.5, v.Height, 6);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(-0.5f, 1.7f)]
    [InlineData(float.NaN, float.PositiveInfinity)]
    public void Near_or_past_an_edge_the_view_stops_at_it(float x, float y)
    {
        CropRegion v = ZoomView.View(new PointF(x, y), 3, null);
        Assert.InRange(v.X, 0, 1 - v.Width + 1e-9);
        Assert.InRange(v.Y, 0, 1 - v.Height + 1e-9);
    }

    [Fact]
    public void The_view_never_leaves_the_crop()
    {
        var crop = new CropRegion(0.6, 0.1, 0.3, 0.4);
        for (float x = -0.2f; x <= 1.2f; x += 0.1f)
        for (float y = -0.2f; y <= 1.2f; y += 0.1f)
        {
            CropRegion v = ZoomView.View(new PointF(x, y), 2, crop);
            Assert.True(v.X >= crop.X - 1e-9 && v.Y >= crop.Y - 1e-9, $"{v} starts outside {crop}");
            Assert.True(v.X + v.Width <= crop.X + crop.Width + 1e-9 && v.Y + v.Height <= crop.Y + crop.Height + 1e-9, $"{v} ends outside {crop}");
            Assert.Equal(crop.Width / 2, v.Width, 6);
        }
    }

    [Fact]
    public void A_tiny_crop_cant_be_zoomed_below_the_smallest_crop()
    {
        var tiny = new CropRegion(0.5, 0.5, CropRegion.MinSize, CropRegion.MinSize);
        CropRegion v = ZoomView.View(new PointF(0.5f, 0.5f), 4, tiny);
        Assert.Equal(CropRegion.MinSize, v.Width, 9);
        Assert.Equal(tiny.X, v.X, 9);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2.4, 2)]
    [InlineData(100, 4)]
    [InlineData(0, 1.5)]
    [InlineData(double.NaN, 2)]
    public void Levels_snap_to_the_offered_ones(double level, double expected) => Assert.Equal(expected, ZoomView.Normalize(level));

    [Fact]
    public void Closer_goes_round_the_levels()
    {
        Assert.Equal(3, ZoomView.Next(2));
        Assert.Equal(4, ZoomView.Next(3));
        Assert.Equal(1.5, ZoomView.Next(4));
    }

    [Fact]
    public void Following_eases_towards_the_pointer_and_arrives()
    {
        PointF at = new(0.2f, 0.2f), target = new(0.8f, 0.6f);
        PointF first = ZoomView.Ease(at, target);
        Assert.True(first.X > at.X && first.X < target.X);
        for (int i = 0; i < 100; i++) at = ZoomView.Ease(at, target);
        Assert.Equal(target, at);
    }

    [Fact]
    public void A_zoom_starts_in_the_middle_of_the_crop_when_the_pointer_is_elsewhere() =>
        Assert.Equal(new PointF(0.75f, 0.25f), ZoomView.Middle(new CropRegion(0.5, 0, 0.5, 0.5)));

    [Fact]
    public void Settings_keep_an_offered_level()
    {
        var s = new AppSettings { ZoomLevel = 7 };
        s.Normalize();
        Assert.Equal(4, s.ZoomLevel);
        Assert.Equal(2, new AppSettings().ZoomLevel);
    }
}

/// <summary>FR-65: clicks and the pointer on the TV.</summary>
public class PointerInputTests
{
    [Fact]
    public void Swapped_buttons_make_the_physical_right_button_the_primary_one()
    {
        Assert.Equal((true, false), PointerInput.Buttons(leftDown: true, rightDown: false, swapped: false));
        Assert.Equal((false, true), PointerInput.Buttons(leftDown: true, rightDown: false, swapped: true));
        Assert.Equal((true, false), PointerInput.Buttons(leftDown: false, rightDown: true, swapped: true));
    }

    [Fact]
    public void A_held_button_is_one_click()
    {
        Assert.Equal((true, false), PointerInput.Pressed((false, false), (true, false)));
        Assert.Equal((false, false), PointerInput.Pressed((true, false), (true, false)));
        Assert.Equal((false, true), PointerInput.Pressed((true, false), (true, true)));
        Assert.Equal((false, false), PointerInput.Pressed((true, true), (false, false))); // letting go isn't a click
    }

    [Fact]
    public void The_pointer_is_placed_as_a_fraction_of_what_is_shown()
    {
        Assert.Equal(new PointF(0.5f, 0.25f), PointerInput.Fraction(new Point(200, 150), new Rectangle(100, 100, 200, 200)));
        Assert.True(float.IsNaN(PointerInput.Fraction(new Point(1, 1), Rectangle.Empty).X));
        // On a screen left of the main one.
        Assert.Equal(new PointF(0.5f, 0.5f), PointerInput.Fraction(new Point(-960, 540), new Rectangle(-1920, 0, 1920, 1080)));
    }

    [Fact]
    public void A_click_ring_grows_and_fades_then_is_gone()
    {
        (float Radius, float Alpha) start = EffectStyle.Ring(TimeSpan.Zero)!.Value;
        (float Radius, float Alpha) mid = EffectStyle.Ring(EffectStyle.RingLife / 2)!.Value;
        Assert.True(mid.Radius > start.Radius);
        Assert.True(mid.Alpha < start.Alpha);
        Assert.Equal(1, start.Alpha);
        Assert.Null(EffectStyle.Ring(EffectStyle.RingLife));
        Assert.Null(EffectStyle.Ring(TimeSpan.FromMilliseconds(-1)));
        Assert.True(EffectStyle.Ring(EffectStyle.RingLife - TimeSpan.FromMilliseconds(1))!.Value.Radius <= EffectStyle.RingRadius);
    }

    [Fact]
    public void The_laser_trail_fades_out()
    {
        Assert.Equal(1, EffectStyle.TrailAlpha(TimeSpan.Zero));
        Assert.True(EffectStyle.TrailAlpha(EffectStyle.TrailLife / 2) is > 0 and < 1);
        Assert.Null(EffectStyle.TrailAlpha(EffectStyle.TrailLife));
    }

    [Fact]
    public void Marks_land_on_the_same_spot_of_the_content_through_crop_and_letterbox()
    {
        var area = new RectangleF(0, 30, 1000, 500);    // the client area inside the captured frame
        var shown = new RectangleF(500, 30, 500, 250);  // its top-right quarter (a crop)
        var dest = new RectangleF(100, 0, 800, 400);    // drawn letterboxed on the canvas
        Assert.Equal(new PointF(100, 0), EffectStyle.Map(new PointF(0.5f, 0f), area, shown, dest));
        Assert.Equal(new PointF(900, 400), EffectStyle.Map(new PointF(1f, 0.5f), area, shown, dest));
        PointF outside = EffectStyle.Map(new PointF(0.1f, 0.9f), area, shown, dest); // off the shown part: clipped when drawn
        Assert.False(dest.Contains(outside));
        Assert.True(float.IsNaN(EffectStyle.Map(new PointF(0.5f, 0.5f), area, RectangleF.Empty, dest).X));
    }

    [Fact]
    public void Marks_need_the_helper_and_say_so_when_choosing_how_to_show() =>
        Assert.True(CaptureChoice.DrawsPointerAndScreens(isScreen: false, startFrozen: false, preferWgc: false, showPointer: true));

    [Fact]
    public void The_new_actions_have_keys_names_and_icons()
    {
        foreach (string id in new[] { "zoom-in", "zoom-in-more", "highlight-clicks", "pointer-ring", "laser" })
        {
            ShortcutAction action = ShortcutCatalog.Find(id)!;
            Assert.NotNull(action.Default);
            Assert.NotNull(UI.Icons.ForCommand(id));
        }
        // "zoom" stays the meeting-sharing command; zooming in has its own id.
        Assert.Null(ShortcutCatalog.Find("zoom"));
        Assert.True(TrayMenuPlan.AppCommands.Contains("zoom"));
    }
}

/// <summary>The lines that carry marks to the capture helper.</summary>
public class MarksProtocolTests
{
    [Fact]
    public void Points_survive_the_trip()
    {
        Assert.Equal("0.25,0.5", CaptureProtocol.Point(new PointF(0.25f, 0.5f)));
        Assert.Equal(new PointF(0.1234f, 0.9876f), CaptureProtocol.ParsePoint(CaptureProtocol.Point(new PointF(0.12341f, 0.98759f))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.5")]
    [InlineData("a,b")]
    [InlineData("NaN,0.5")]
    [InlineData("5,0.5")]
    [InlineData("0.5,-3")]
    [InlineData(",0.5")]
    public void Garbled_points_are_ignored(string text) => Assert.Null(CaptureProtocol.ParsePoint(text));

    [Fact]
    public void Each_mark_has_its_own_line()
    {
        Assert.Equal("click 0.5,0.5 1", CaptureProtocol.Click(new PointF(0.5f, 0.5f), secondary: true));
        Assert.Equal("pointer none", CaptureProtocol.Pointer(null));
        Assert.Equal("pointer 0.1,0.2", CaptureProtocol.Pointer(new PointF(0.1f, 0.2f)));
        Assert.Equal("laser none", CaptureProtocol.Laser(null));
        Assert.Equal("cursor 0", CaptureProtocol.Cursor(false));
        Assert.Equal("ink-remove 7", CaptureProtocol.InkRemove(7));
    }

    [Fact]
    public void A_line_drawn_on_the_TV_survives_the_trip()
    {
        var stroke = new InkStroke(12, InkTool.Highlighter, Color.FromArgb(255, 0x12, 0x34, 0x56).ToArgb(), 0.012f);
        InkStroke parsed = CaptureHost.ParseInkStart(CaptureProtocol.InkStart(stroke))!;
        Assert.Equal(12, parsed.Id);
        Assert.Equal(InkTool.Highlighter, parsed.Tool);
        Assert.Equal(stroke.Argb, parsed.Argb);
        Assert.Equal(0.012f, parsed.Width, 5);

        string add = CaptureProtocol.InkAdd(12, [new PointF(0.1f, 0.2f), new PointF(0.3f, 0.4f)]);
        string[] p = add.Split(' ', 3);
        Assert.Equal(["ink-add", "12"], p[..2]);
        Assert.Equal([new PointF(0.1f, 0.2f), new PointF(0.3f, 0.4f)], CaptureProtocol.ParsePoints(p[2]));
    }

    [Theory]
    [InlineData("ink-start 1 9 0 0.01")]  // no such tool
    [InlineData("ink-start 1 0 0 0")]     // no width
    [InlineData("ink-start 1 0 0")]       // missing a part
    [InlineData("ink-begin 1 0 0 0.01")]
    public void A_garbled_line_start_is_ignored(string line) => Assert.Null(CaptureHost.ParseInkStart(line));

    [Fact]
    public void Garbled_points_in_a_line_are_skipped() =>
        Assert.Equal([new PointF(0.5f, 0.5f)], CaptureProtocol.ParsePoints("x,y 0.5,0.5  9,9"));
}

/// <summary>The drawing's lines, undo, and sending it all to a new engine.</summary>
public class InkDrawingTests
{
    [Fact]
    public void Lines_get_new_ids_and_undo_takes_the_last()
    {
        var drawing = new InkDrawing();
        InkStroke a = drawing.Begin(InkTool.Pen, Color.Red, 0.01f), b = drawing.Begin(InkTool.Pen, Color.Red, 0.01f);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(b.Id, drawing.Undo());
        Assert.Equal(a.Id, drawing.Undo());
        Assert.Null(drawing.Undo());
    }

    [Fact]
    public void A_drawing_and_each_line_stay_bounded()
    {
        var drawing = new InkDrawing();
        for (int i = 0; i < InkDrawing.MaxStrokes + 10; i++) drawing.Begin(InkTool.Pen, Color.Red, 0.01f);
        Assert.Equal(InkDrawing.MaxStrokes, drawing.Strokes.Count);

        var stroke = new InkStroke(1, InkTool.Pen, 0, 0.01f);
        for (int i = 0; i < InkStroke.MaxPoints + 50; i++) stroke.Add(new PointF(i / 10000f, 0.5f));
        Assert.Equal(InkStroke.MaxPoints, stroke.Points.Count);
        Assert.False(stroke.Add(new PointF(float.NaN, 0)));
        var dup = new InkStroke(2, InkTool.Pen, 0, 0.01f);
        dup.Add(new PointF(0.1f, 0.1f));
        Assert.False(dup.Add(new PointF(0.1f, 0.1f))); // a pointer that didn't move adds nothing
    }

    [Fact]
    public void A_new_engine_gets_the_whole_drawing_after_a_clear()
    {
        var drawing = new InkDrawing();
        InkStroke a = drawing.Begin(InkTool.Pen, Color.Red, 0.01f);
        a.Add(new PointF(0.1f, 0.1f));
        a.Add(new PointF(0.2f, 0.2f));
        drawing.Begin(InkTool.Highlighter, Color.Yellow, 0.03f); // started, no points yet
        var engine = new RecordingEffects();
        drawing.Replay(engine);
        Assert.Equal(["clear", $"start {a.Id}", $"add {a.Id} 2", $"start {a.Id + 1}"], engine.Calls);
    }

    private sealed class RecordingEffects : IEffectsEngine
    {
        public List<string> Calls { get; } = [];
        public void Click(PointF at, bool secondary) => Calls.Add("click");
        public void Pointer(PointF? at) => Calls.Add("pointer");
        public void Laser(PointF? at) => Calls.Add("laser");
        public void ShowCursor(bool on) => Calls.Add("cursor");
        public void InkStart(InkStroke stroke) => Calls.Add($"start {stroke.Id}");
        public void InkAdd(int id, IReadOnlyList<PointF> points) => Calls.Add($"add {id} {points.Count}");
        public void InkRemove(int id) => Calls.Add($"remove {id}");
        public void InkClear() => Calls.Add("clear");
        public void Inset(SourceWindow? source, InsetCorner corner, int percent) => Calls.Add(source is null ? "inset none" : "inset");
        public event EventHandler<string>? InsetFailed { add { } remove { } }
        public void InkSet(int id, IReadOnlyList<PointF> points) => Calls.Add($"set {id} {points.Count}");
        public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) => Calls.Add($"redact {regions.Count} {style}");
        public void Spotlight(PointF? at) => Calls.Add(at is null ? "spotlight none" : "spotlight");
        public void Caption(string? name, string? second) => Calls.Add(name is null ? "caption none" : $"caption {name}|{second}");
        public void Logo(string? path) => Calls.Add(path is null ? "logo none" : "logo");
        public void Qr(string? text, InsetCorner corner) => Calls.Add(text is null ? "qr none" : $"qr {corner}");
        public void LiveCaption(string? text) => Calls.Add(text is null ? "live none" : $"live {text}");
    }
}

/// <summary>The session's zoomed view: through black to zoom, instantly to follow, never past the crop.</summary>
public class SessionZoomTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;

    public SessionZoomTests() => _session = new SessionController(_platform);

    private static SourceWindow Notepad => TestData.Window(101, "Notes - Notepad");

    [Fact]
    public async Task Zooming_in_goes_through_black_and_shows_the_view()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        int fades = _platform.Fades.Count;
        var view = new CropRegion(0.25, 0.25, 0.5, 0.5);
        await _session.SetViewAsync(view);
        Assert.Equal(view, _session.View);
        Assert.Equal(view, _platform.Engines[^1].Crop);
        Assert.Equal(0, _platform.Fades[fades].To);           // out to black first
        Assert.Equal(1, _platform.Fades[^1].To, 3);           // then back
        Assert.Null(_session.Source!.Crop);                    // the chosen crop is untouched
    }

    [Fact]
    public async Task Following_moves_the_view_at_once_without_a_fade()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.SetViewAsync(new CropRegion(0, 0, 0.5, 0.5));
        int fades = _platform.Fades.Count;
        var next = new CropRegion(0.1, 0.1, 0.5, 0.5);
        _session.SetView(next);
        Assert.Equal(next, _platform.Engines[^1].Crop);
        Assert.Equal(fades, _platform.Fades.Count);
    }

    [Fact]
    public async Task Following_does_nothing_unless_zoomed_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _session.SetView(new CropRegion(0.1, 0.1, 0.5, 0.5));
        Assert.Null(_session.View);
        Assert.Null(_platform.Engines[^1].Crop);
    }

    [Fact]
    public async Task Zooming_out_shows_the_crop_again()
    {
        var crop = new CropRegion(0.5, 0, 0.5, 0.5);
        await _session.PresentAsync(Notepad with { Crop = crop }, TestData.Tv, true);
        await _session.SetViewAsync(new CropRegion(0.6, 0.1, 0.25, 0.25));
        await _session.SetViewAsync(null);
        Assert.Null(_session.View);
        Assert.Equal(crop, _platform.Engines[^1].Crop);
    }

    [Fact]
    public async Task A_new_crop_or_something_new_ends_zooming_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.SetViewAsync(new CropRegion(0, 0, 0.5, 0.5));
        await _session.SetCropAsync(new CropRegion(0, 0, 0.8, 0.8));
        Assert.Null(_session.View);

        await _session.SetViewAsync(new CropRegion(0, 0, 0.4, 0.4));
        await _session.PresentAsync(TestData.Window(202, "Deck"), TestData.Tv, true);
        Assert.Null(_session.View);
    }

    [Fact]
    public async Task Freezing_keeps_the_zoomed_view_and_announces_the_new_engine()
    {
        int changes = 0;
        _session.EngineChanged += (_, _) => changes++;
        await _session.PresentAsync(Notepad, TestData.Tv, true); // a DWM-like fake: freezing hands over to a new engine
        var view = new CropRegion(0.2, 0.2, 0.5, 0.5);
        await _session.SetViewAsync(view);
        int before = changes;
        await _session.ToggleFreezeAsync();
        Assert.True(_session.IsFrozen);
        Assert.Equal(view, _platform.Engines[^1].Crop);
        Assert.Equal(before + 1, changes);
    }

    [Fact]
    public async Task A_fallback_engine_keeps_the_zoomed_view()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        var view = new CropRegion(0.2, 0.2, 0.5, 0.5);
        await _session.SetViewAsync(view);
        _platform.Engines[^1].RaiseFailed();
        for (int i = 0; i < 50 && _platform.Engines.Count < 2; i++) await Task.Delay(10);
        await Task.Delay(50);
        Assert.Equal(view, _platform.Engines[^1].Crop);
    }

    [Fact]
    public void Nothing_shown_nothing_to_zoom() => Assert.True(_session.SetViewAsync(new CropRegion(0, 0, 0.5, 0.5)).IsCompletedSuccessfully);
}
