using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.Tests;

/// <summary>Records every call as text, so what the helper received can be compared with what was sent.</summary>
internal sealed class TextEffects : IEffectsEngine
{
    public List<string> Calls { get; } = [];
    private static string P(PointF? p) => p is { } v ? $"{v.X:0.###},{v.Y:0.###}" : "none";
    public void Click(PointF at, bool secondary) => Calls.Add($"click {P(at)} {secondary}");
    public void Pointer(PointF? at) => Calls.Add($"pointer {P(at)}");
    public void Laser(PointF? at) => Calls.Add($"laser {P(at)}");
    public void ShowCursor(bool on) => Calls.Add($"cursor {on}");
    public void InkStart(InkStroke s) => Calls.Add($"ink-start {s.Id} {s.Tool} {s.Argb} {s.Width:0.####} [{s.Text}]");
    public void InkAdd(int id, IReadOnlyList<PointF> points) => Calls.Add($"ink-add {id} {string.Join(' ', points.Select(p => P(p)))}");
    public void InkSet(int id, IReadOnlyList<PointF> points) => Calls.Add($"ink-set {id} {string.Join(' ', points.Select(p => P(p)))}");
    public void InkRemove(int id) => Calls.Add($"ink-remove {id}");
    public void InkClear() => Calls.Add("ink-clear");
    public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) =>
        Calls.Add($"redact {style} {string.Join(' ', regions.Select(r => $"{r.X:0.###},{r.Y:0.###},{r.Width:0.###},{r.Height:0.###}"))}".TrimEnd());
    public void Spotlight(PointF? at) => Calls.Add($"spotlight {P(at)}");
    public void Caption(string? name, string? second) => Calls.Add(name is null ? "caption none" : $"caption [{name}] [{second}]");
    public void Logo(string? path) => Calls.Add($"logo [{path}]");
    public void Qr(string? text, InsetCorner corner) => Calls.Add(text is null ? "qr none" : $"qr {corner} [{text}]");
    public void LiveCaption(string? text) => Calls.Add(text is null ? "live none" : $"live [{text}]");
    public void Inset(SourceWindow? source, InsetCorner corner, int percent) => Calls.Add(source is null ? "inset none" : $"inset {corner} {percent}");
    public event EventHandler<string>? InsetFailed { add { } remove { } }
}

/// <summary>
/// The stage tools reach the helper intact: each line made by the app (as the remote engine sends
/// it) is read by the helper's own parser into the very same call. Malformed lines are ignored.
/// </summary>
public class StageProtocolTests
{
    /// <summary>Sends <paramref name="line"/> through the helper's parser; the call it made.</summary>
    private static string Through(string line)
    {
        var helper = new TextEffects();
        Assert.True(CaptureHost.ApplyMark(helper, line), $"not understood: {line}");
        return Assert.Single(helper.Calls);
    }

    private static string Direct(Action<TextEffects> call)
    {
        var direct = new TextEffects();
        call(direct);
        return Assert.Single(direct.Calls);
    }

    public static TheoryData<string> Cases => new()
    {
        "redact-solid", "redact-blur", "redact-none", "spotlight", "spotlight-off", "caption", "caption-one-line", "caption-off",
        "logo", "logo-off", "qr", "qr-wifi", "qr-off", "live", "live-off", "ink-set", "ink-text", "ink-arrow",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_stage_command_arrives_as_sent(string which)
    {
        CropRegion a = CropRegion.Create(0.1, 0.2, 0.3, 0.4)!, b = CropRegion.Create(0.6, 0.6, 0.2, 0.2)!;
        PointF[] points = [new(0.1f, 0.2f), new(0.55f, 0.75f)];
        var text = new InkStroke(7, InkTool.Text, Color.Yellow.ToArgb(), 0.045f) { Text = "Revenue: up 12% (Q3)" };
        var arrow = new InkStroke(8, InkTool.Arrow, Color.Red.ToArgb(), 0.006f);
        (string line, Action<TextEffects> call) = which switch
        {
            "redact-solid" => (CaptureProtocol.Redact([a, b], RedactStyle.Solid), (Action<TextEffects>)(e => e.Redact([a, b], RedactStyle.Solid))),
            "redact-blur" => (CaptureProtocol.Redact([a], RedactStyle.Blurred), e => e.Redact([a], RedactStyle.Blurred)),
            "redact-none" => (CaptureProtocol.Redact([], RedactStyle.Blurred), e => e.Redact([], RedactStyle.Solid)),
            "spotlight" => (CaptureProtocol.Spotlight(points[1]), e => e.Spotlight(points[1])),
            "spotlight-off" => (CaptureProtocol.Spotlight(null), e => e.Spotlight(null)),
            "caption" => (CaptureProtocol.Caption("Jane Doe", "Head of Sales, EMEA"), e => e.Caption("Jane Doe", "Head of Sales, EMEA")),
            "caption-one-line" => (CaptureProtocol.Caption("Jane Doe", ""), e => e.Caption("Jane Doe", "")),
            "caption-off" => (CaptureProtocol.Caption(null, null), e => e.Caption(null, null)),
            "logo" => (CaptureProtocol.Logo(@"C:\Users\Jane Doe\AppData\Roaming\ScreenCastPresenter\blank-logo.png"), e => e.Logo(@"C:\Users\Jane Doe\AppData\Roaming\ScreenCastPresenter\blank-logo.png")),
            "logo-off" => (CaptureProtocol.Logo(null), e => e.Logo(null)),
            "qr" => (CaptureProtocol.Qr("https://example.com/a b?c=d", InsetCorner.BottomLeft), e => e.Qr("https://example.com/a b?c=d", InsetCorner.BottomLeft)),
            "qr-wifi" => (CaptureProtocol.Qr(QrCode.WiFi("Guest Net", "p a;ss", false), InsetCorner.TopLeft), e => e.Qr(QrCode.WiFi("Guest Net", "p a;ss", false), InsetCorner.TopLeft)),
            "qr-off" => (CaptureProtocol.Qr(null, InsetCorner.TopRight), e => e.Qr(null, InsetCorner.TopRight)),
            "live" => (CaptureProtocol.Live("and that's why  it went up"), e => e.LiveCaption("and that's why  it went up")),
            "live-off" => (CaptureProtocol.Live(null), e => e.LiveCaption(null)),
            "ink-set" => (CaptureProtocol.InkSet(3, points), e => e.InkSet(3, points)),
            "ink-text" => (CaptureProtocol.InkStart(text), e => e.InkStart(text)),
            "ink-arrow" => (CaptureProtocol.InkStart(arrow), e => e.InkStart(arrow)),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };
        Assert.DoesNotContain('\n', line);
        Assert.Equal(Direct(call), Through(line));
    }

    [Fact]
    public void Nonsense_is_ignored_and_never_breaks_the_helper()
    {
        var helper = new TextEffects();
        string[] junk =
        [
            "", " ", "redact", "redact 0", "redact x 1,2,3,4", "redact 0 nan,nan,1,1", "spotlight", "spotlight a,b", "caption", "logo", "qr", "qr 2", "qr x",
            "live", "ink-set", "ink-set x", "ink-start 1 99 0 0.1", "ink-start 1 4 0 -1 words", "inset", "zzz 1 2 3", new string('x', 10_000),
        ];
        foreach (string line in junk) CaptureHost.ApplyMark(helper, line);
        // Taking things off is harmless; nothing is ever put on the TV by nonsense.
        Assert.DoesNotContain(helper.Calls, c => c.StartsWith("ink-start", StringComparison.Ordinal) || c.StartsWith("caption [", StringComparison.Ordinal)
            || c.StartsWith("qr ", StringComparison.Ordinal) && c != "qr none" || c.StartsWith("live [", StringComparison.Ordinal) || c.StartsWith("logo [C", StringComparison.Ordinal));
        Assert.All(helper.Calls.Where(c => c.StartsWith("redact", StringComparison.Ordinal)), c => Assert.Equal("redact Solid", c));
        Assert.False(CaptureHost.ApplyMark(helper, "opacity 1")); // not a mark: the host handles it itself
    }

    [Fact]
    public void A_name_with_the_line_separator_in_it_cant_shift_the_second_line()
    {
        string line = CaptureProtocol.Caption("Jane" + CaptureProtocol.LineSeparator + "Doe", "CEO");
        Assert.Equal("caption [Jane Doe] [CEO]", Through(line));
    }
}

/// <summary>Arrows, boxes and text drawn with the mouse (FR-78), on real (hidden) windows.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class DrawingShapesTests
{
    private static readonly Rectangle Area = new(100, 100, 800, 400);
    private static readonly Rectangle Shown = new(500, 100, 400, 400);

    [Fact]
    public void Keys_choose_the_arrow_box_and_text_tools()
    {
        using var overlay = new DrawingOverlay(new InkDrawing(), () => null, () => (Area, Shown), 1);
        overlay.CreateWindows();
        Assert.True(overlay.HandleKey(Keys.A));
        Assert.Equal(InkTool.Arrow, overlay.Tool);
        Assert.True(overlay.HandleKey(Keys.B));
        Assert.Equal(InkTool.Box, overlay.Tool);
        Assert.True(overlay.HandleKey(Keys.T));
        Assert.Equal(InkTool.Text, overlay.Tool);
        Assert.Equal(DrawingOverlay.ShapeWidth, DrawingOverlay.WidthFor(InkTool.Arrow));
        Assert.Equal(DrawingOverlay.TextHeight, DrawingOverlay.WidthFor(InkTool.Text));
    }

    [Fact]
    public void Dragging_an_arrow_sends_its_start_and_moving_end()
    {
        var drawing = new InkDrawing();
        var effects = new TextEffects();
        using var overlay = new DrawingOverlay(drawing, () => effects, () => (Area, Shown), 1);
        overlay.CreateWindows();
        overlay.SetTool(InkTool.Arrow);
        overlay.BeginStroke(new Point(500, 300));
        overlay.AddPoint(new Point(700, 300));
        overlay.AddPoint(new Point(900, 500));
        overlay.AddPoint(new Point(900, 500)); // didn't move: nothing sent
        overlay.EndStroke();
        InkStroke arrow = Assert.Single(drawing.Strokes);
        Assert.Equal([new PointF(0.5f, 0.5f), new PointF(1f, 1f)], arrow.Points);
        Assert.Equal("ink-start 1 Arrow", effects.Calls[0][..17]);
        Assert.Equal("ink-set 1 0.5,0.5 1,1", effects.Calls[^1]);
        Assert.Equal(2, effects.Calls.Count(c => c.StartsWith("ink-set", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_box_that_was_only_clicked_is_taken_away_again()
    {
        var drawing = new InkDrawing();
        var effects = new TextEffects();
        using var overlay = new DrawingOverlay(drawing, () => effects, () => (Area, Shown), 1);
        overlay.CreateWindows();
        overlay.SetTool(InkTool.Box);
        overlay.BeginStroke(new Point(600, 300));
        overlay.EndStroke();
        Assert.Empty(drawing.Strokes);
        Assert.Equal("ink-remove 1", effects.Calls[^1]);
    }

    [Fact]
    public void The_toolbar_and_text_box_are_hidden_from_capture()
    {
        if (Environment.GetEnvironmentVariable("SCP_DEV_ALLOW_CAPTURE") == "1") return;
        using var form = new FieldsForm("Your name on the TV", "Shown along the bottom.", "Save");
        form.AddField("Name", "Jane", 60);
        form.CreateControl();
        Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)form.Handle), "what's typed (a Wi-Fi password) never reaches a meeting");
    }

    [Fact]
    public void A_form_that_isnt_filled_in_says_why_and_stays_open()
    {
        using var form = new FieldsForm("A QR code on the TV", "x", "Show it");
        (_, TextBox link) = form.AddField("Link", "", 400);
        form.Check = () => link.Text.Length == 0 ? "Type or paste a link." : null;
        ComboBox days = form.AddList("Days", ["Weekdays", "Every day"], 1);
        Assert.Equal(1, days.SelectedIndex);
        int changed = -1;
        form.AddChoice(["Link", "Wi-Fi"], 0, i => changed = i);
        Assert.Equal(0, changed); // told at once which is chosen
        form.CreateControl();
        Assert.Equal("Type or paste a link.", form.Check());
        link.Text = "https://example.com";
        Assert.Null(form.Check());
    }
}

/// <summary>The stage tools are where people look: tray, shortcuts, help, icons.</summary>
public class StageCatalogTests
{
    private static readonly string[] NewActions =
        ["hide-part", "show-hidden", "audience-view", "get-ready", "go", "spotlight", "caption", "qr", "live-captions", "second-tv", "save-room"];

    [Fact]
    public void Every_new_action_is_in_the_catalogue_with_an_icon_and_runs_from_the_tray()
    {
        foreach (string id in NewActions)
        {
            Assert.NotNull(ShortcutCatalog.Find(id));
            Assert.NotNull(Icons.ForCommand(id));
            Assert.True(TrayMenuPlan.IsKnownCommand(id), id);
        }
        Assert.True(TrayMenuPlan.IsKnownCommand("room:Board room"));
        Assert.False(TrayMenuPlan.IsKnownCommand("room:"));
        Assert.True(TrayMenuPlan.IsKnownCommand("settings:rooms"));
        Assert.True(TrayMenuPlan.IsKnownCommand("stop-second-tv"));
        Assert.NotNull(Icons.ForCommand("room:Lobby"));
    }

    [Fact]
    public void Default_keys_are_the_ones_promised_and_clash_with_nothing()
    {
        Assert.Equal("Ctrl+Alt+X", ShortcutCatalog.Find("hide-part")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+Shift+X", ShortcutCatalog.Find("show-hidden")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+V", ShortcutCatalog.Find("audience-view")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+G", ShortcutCatalog.Find("go")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+Shift+G", ShortcutCatalog.Find("get-ready")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+Shift+F", ShortcutCatalog.Find("spotlight")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+Shift+N", ShortcutCatalog.Find("caption")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+Q", ShortcutCatalog.Find("qr")!.Default!.Value.ToString());
        Assert.Equal("Ctrl+Alt+Shift+T", ShortcutCatalog.Find("live-captions")!.Default!.Value.ToString());
        Assert.Null(ShortcutCatalog.Find("second-tv")!.Default);
        var defaults = ShortcutCatalog.All.Where(a => a.Default is not null).Select(a => a.Default!.Value).ToList();
        Assert.Equal(defaults.Count, defaults.Distinct().Count());
    }

    [Fact]
    public void Each_new_feature_has_a_how_do_i()
    {
        foreach (string command in new[] { "hide-part", "audience-view", "get-ready", "spotlight", "caption", "qr", "live-captions", "second-tv", "settings:rooms" })
            Assert.Contains(HelpTopics.All, t => t.Command == command);
        Assert.Contains(HelpTopics.All, t => t.Question.Contains("arrows", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(HelpTopics.All, t => t.Question.Contains("logo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(HelpTopics.All, t => t.Question.Contains("automatically", StringComparison.OrdinalIgnoreCase));
    }

    private static TrayMenuState Presenting => new()
    {
        Session = SessionState.Presenting,
        Status = "● Showing on TV: Slides",
        Rooms = ["Board room", "Lobby"],
        SecondTvPossible = true,
    };

    private static List<MenuEntry> All(TrayMenuState s) => [.. TrayMenuPlan.Flatten(TrayMenuPlan.Build(s))];

    [Fact]
    public void While_presenting_the_tray_offers_every_stage_tool()
    {
        List<MenuEntry> menu = All(Presenting);
        foreach (string command in new[] { "hide-part", "show-hidden", "spotlight", "caption", "qr", "live-captions", "audience-view", "second-tv", "save-room", "settings:rooms", "room:Board room", "room:Lobby" })
            Assert.Contains(menu, e => e.Command == command);
        Assert.False(menu.Single(e => e.Command == "show-hidden").Enabled); // nothing hidden yet
        Assert.True(All(Presenting with { HasHiddenParts = true }).Single(e => e.Command == "show-hidden").Enabled);
        Assert.DoesNotContain(menu, e => e.Command == "go"); // nothing ready
    }

    [Fact]
    public void Tray_lines_follow_what_is_on()
    {
        List<MenuEntry> on = All(Presenting with { SpotlightOn = true, CaptionOn = true, HasQr = true, LiveCaptions = true, AudienceView = true, NextTitle = "Budget.xlsx", SecondTvOn = true });
        Assert.True(on.Single(e => e.Command == "spotlight").Checked);
        Assert.True(on.Single(e => e.Command == "live-captions").Checked);
        Assert.True(on.Single(e => e.Command == "audience-view").Checked);
        Assert.Equal("Take my name off", on.Single(e => e.Command == "caption").Text);
        Assert.Equal("Take the QR code off", on.Single(e => e.Command == "qr").Text);
        Assert.Equal("Show “Budget.xlsx” now", on.Single(e => e.Command == "go").Text);
        Assert.Contains(on, e => e.Command == "stop-second-tv");
        Assert.DoesNotContain(on, e => e.Command == "second-tv");
        Assert.False(All(Presenting with { SecondTvPossible = false }).Single(e => e.Command == "second-tv").Enabled);
    }

    [Fact]
    public void Rooms_are_offered_even_when_nothing_is_shown()
    {
        List<MenuEntry> idle = All(new TrayMenuState { Session = SessionState.Off, Status = "Ready", Rooms = ["Lobby"] });
        Assert.Contains(idle, e => e.Command == "room:Lobby");
        Assert.Contains(idle, e => e.Command == "save-room");
        Assert.DoesNotContain(idle, e => e.Command is "hide-part" or "spotlight" or "audience-view");
    }

    [Fact]
    public void Settings_has_a_rooms_and_schedule_section()
    {
        Assert.Contains(SettingsForm.Section.Rooms, Enum.GetValues<SettingsForm.Section>());
        Assert.True(Array.IndexOf(Enum.GetValues<SettingsForm.Section>(), SettingsForm.Section.Rooms) < Array.IndexOf(Enum.GetValues<SettingsForm.Section>(), SettingsForm.Section.Advanced));
    }
}
