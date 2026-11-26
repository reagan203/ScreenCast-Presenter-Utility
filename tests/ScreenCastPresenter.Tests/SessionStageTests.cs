using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>An engine that draws marks (like the helper's), recording what it was told and when.</summary>
internal sealed class FakeMarksEngine(bool fail, bool supportsFreeze) : FakeEngine(fail, supportsFreeze), IEffectsEngine
{
    public List<string> Marks { get; } = [];

    /// <summary>How many opacities had been set when the hidden parts last arrived.</summary>
    public int? RedactedAtOpacity { get; private set; }

    public IReadOnlyList<CropRegion> Hidden { get; private set; } = [];
    public string? LogoPath { get; private set; }

    public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style)
    {
        Hidden = regions;
        RedactedAtOpacity ??= Opacities.Count;
        Marks.Add($"redact {regions.Count} {style}");
    }

    public void Logo(string? path)
    {
        LogoPath = path;
        Marks.Add(path is null ? "logo none" : "logo");
    }

    public void Click(PointF at, bool secondary) => Marks.Add("click");
    public void Pointer(PointF? at) => Marks.Add("pointer");
    public void Laser(PointF? at) => Marks.Add("laser");
    public void ShowCursor(bool on) => Marks.Add("cursor");
    public void InkStart(InkStroke stroke) => Marks.Add("ink-start");
    public void InkAdd(int id, IReadOnlyList<PointF> points) => Marks.Add("ink-add");
    public void InkSet(int id, IReadOnlyList<PointF> points) => Marks.Add("ink-set");
    public void InkRemove(int id) => Marks.Add("ink-remove");
    public void InkClear() => Marks.Add("ink-clear");
    public void Spotlight(PointF? at) => Marks.Add(at is null ? "spotlight none" : "spotlight");
    public void Caption(string? name, string? second) => Marks.Add(name is null ? "caption none" : "caption");
    public void Qr(string? text, InsetCorner corner) => Marks.Add(text is null ? "qr none" : "qr");
    public void LiveCaption(string? text) => Marks.Add(text is null ? "live none" : "live");
    public void Inset(SourceWindow? source, InsetCorner corner, int percent) => Marks.Add("inset");
    public event EventHandler<string>? InsetFailed { add { } remove { } }
}

/// <summary>
/// FR-74: a source with hidden parts is never seen without them. The session covers them before
/// anything fades in, on every engine that takes over, and refuses an engine that can't cover them.
/// </summary>
public class SessionHiddenPartsTests
{
    private readonly FakePlatform _platform = new() { EnginesDrawMarks = true };
    private readonly SessionController _session;
    private readonly AppSettings _settings = new();
    private static readonly CropRegion Box = CropRegion.Create(0.6, 0.1, 0.3, 0.2)!;

    public SessionHiddenPartsTests()
    {
        _session = new SessionController(_platform) { HiddenPartsFor = s => Redactions.PartsFor(_settings, s) };
    }

    private static SourceWindow Mail => TestData.Window(11, "Inbox - Outlook", "OUTLOOK");
    private static SourceWindow Notes => TestData.Window(12, "Notes - Notepad");

    private FakeMarksEngine Engine => (FakeMarksEngine)_platform.Engines[^1];

    /// <summary>The boxes arrived before the engine was ever asked to show anything.</summary>
    private static void CoveredWhileBlack(FakeMarksEngine engine)
    {
        int at = engine.RedactedAtOpacity ?? throw new Xunit.Sdk.XunitException("never covered");
        Assert.All(engine.Opacities.Take(at), o => Assert.Equal(0, o));
    }

    [Fact]
    public async Task Hidden_parts_are_covered_before_the_picture_is_ever_seen()
    {
        Redactions.Add(_settings, Mail, Box);
        await _session.PresentAsync(Mail, TestData.Tv, true);
        Assert.Equal(SessionState.Presenting, _session.State);
        Assert.Equal([Box], Engine.Hidden);
        int at = Engine.RedactedAtOpacity!.Value;
        Assert.All(Engine.Opacities.Take(at), o => Assert.Equal(0, o)); // nothing visible before the boxes arrived
        Assert.True(Engine.Opacities.Count > at && Engine.Opacities[^1] > 0.99);
    }

    [Fact]
    public async Task Switching_to_something_without_hidden_parts_sends_none_and_back_sends_them_again()
    {
        Redactions.Add(_settings, Mail, Box);
        await _session.PresentAsync(Mail, TestData.Tv, true);
        await _session.PresentAsync(Notes, TestData.Tv, true);
        Assert.Empty(Engine.Hidden);
        await _session.PresentAsync(Mail, TestData.Tv, true);
        Assert.Equal([Box], Engine.Hidden);
        CoveredWhileBlack(Engine); // told while still black
    }

    [Fact]
    public async Task The_new_source_is_known_when_an_engine_takes_over()
    {
        SourceWindow? seen = null;
        _session.EngineChanged += (_, _) => seen = _session.Source;
        await _session.PresentAsync(Notes, TestData.Tv, true);
        await _session.PresentAsync(Mail, TestData.Tv, true);
        Assert.Equal(Mail.Handle, seen!.Handle); // marks are sent for what's about to be shown, not the last thing
    }

    [Fact]
    public async Task An_engine_that_cant_cover_them_never_shows_it()
    {
        Redactions.Add(_settings, Mail, Box);
        _platform.EnginesDrawMarks = false; // the light way
        string? said = null;
        _session.Message += (_, m) => said = m;
        await _session.PresentAsync(Mail, TestData.Tv, true);
        Assert.NotEqual(SessionState.Presenting, _session.State);
        Assert.All(_platform.Engines, e => Assert.DoesNotContain(e.Opacities, o => o > 0));
        Assert.Contains("hidden from the audience", said);
    }

    [Fact]
    public async Task Nor_does_a_fallback_that_cant_cover_them()
    {
        Redactions.Add(_settings, Mail, Box);
        await _session.PresentAsync(Mail, TestData.Tv, true);
        FakeMarksEngine first = Engine;
        first.RaiseFailed(); // the helper broke: the session tries the other way, which can't cover
        await Task.Delay(50);
        Assert.NotEqual(SessionState.Presenting, _session.State);
        Assert.All(_platform.Engines.Where(e => e.IsFallback), e => Assert.DoesNotContain(e.Opacities, o => o > 0));
    }

    [Fact]
    public async Task A_fallback_that_can_cover_them_takes_over_covered()
    {
        Redactions.Add(_settings, Mail, Box);
        _platform.FallbackDrawsMarks = true;
        await _session.PresentAsync(Mail, TestData.Tv, true);
        Engine.RaiseFailed();
        await Task.Delay(50);
        Assert.Equal(SessionState.Presenting, _session.State);
        var fallback = (FakeMarksEngine)_platform.Engines.Single(e => e.IsFallback);
        Assert.Equal([Box], fallback.Hidden);
        CoveredWhileBlack(fallback);
    }

    [Fact]
    public async Task Changing_them_while_showing_applies_at_once_or_says_it_cant()
    {
        await _session.PresentAsync(Mail, TestData.Tv, true);
        Assert.Empty(Engine.Hidden);
        Redactions.Add(_settings, Mail, Box);
        Assert.True(_session.UpdateHiddenParts());
        Assert.Equal([Box], Engine.Hidden);
        Redactions.Clear(_settings, Mail);
        Assert.True(_session.UpdateHiddenParts());
        Assert.Empty(Engine.Hidden);
        Assert.Equal("redact 0 Solid", Engine.Marks[^1]);

        _platform.EnginesDrawMarks = false;
        await _session.PresentAsync(Notes, TestData.Tv, true);
        Redactions.Add(_settings, Notes, Box);
        Assert.False(_session.UpdateHiddenParts()); // the app shows it again through the helper
    }

    [Fact]
    public async Task Freezing_a_light_way_picture_covers_them_on_the_frozen_one()
    {
        _platform.EnginesDrawMarks = false;
        await _session.PresentAsync(Notes, TestData.Tv, true);
        _platform.EnginesDrawMarks = true;
        Redactions.Add(_settings, Notes, Box);
        await _session.ToggleFreezeAsync();
        Assert.True(_session.IsFrozen);
        var frozen = (FakeMarksEngine)_platform.Engines[^1];
        Assert.Equal([Box], frozen.Hidden);
    }

    [Fact]
    public async Task A_restart_keeps_them_covered()
    {
        Redactions.Add(_settings, Mail, Box);
        await _session.PresentAsync(Mail, TestData.Tv, true);
        await _session.RestartEngineAsync();
        Assert.Equal(2, _platform.Engines.Count);
        Assert.Equal([Box], Engine.Hidden);
        CoveredWhileBlack(Engine);
    }
}

/// <summary>FR-80: the logo goes to the window and every engine, now and later.</summary>
public class SessionLogoTests
{
    private readonly FakePlatform _platform = new() { EnginesDrawMarks = true };
    private readonly SessionController _session;

    public SessionLogoTests() => _session = new SessionController(_platform);

    [Fact]
    public async Task The_logo_reaches_the_output_and_each_engine()
    {
        _session.SetLogo(@"C:\logo.png");
        await _session.PresentAsync(TestData.Window(1, "A"), TestData.Tv, true);
        Assert.Equal(@"C:\logo.png", _platform.Outputs[^1].Logo);
        Assert.Equal(@"C:\logo.png", ((FakeMarksEngine)_platform.Engines[^1]).LogoPath);
        _session.SetLogo(null);
        Assert.Null(_platform.Outputs[^1].Logo);
        Assert.Null(((FakeMarksEngine)_platform.Engines[^1]).LogoPath);
        _session.SetLogo(@"C:\other.png");
        await _session.PresentAsync(TestData.Window(2, "B"), TestData.Tv, true);
        Assert.Equal(@"C:\other.png", ((FakeMarksEngine)_platform.Engines[^1]).LogoPath);
        Assert.Equal(@"C:\other.png", _session.Logo);
    }
}

/// <summary>FR-85: a second session runs beside the first without either touching the other.</summary>
public class SecondTvSessionTests
{
    [Fact]
    public async Task Two_sessions_show_different_things_on_different_screens_independently()
    {
        var p1 = new FakePlatform();
        var p2 = new FakePlatform();
        var first = new SessionController(p1);
        var second = new SessionController(p2);
        await first.PresentAsync(TestData.Window(1, "Slides"), TestData.Tv, true);
        await second.PresentAsync(TestData.Window(2, "Agenda"), new OutputPlacement(TestData.Third, true));
        Assert.Equal(TestData.Tv.DeviceName, first.FullScreenOutput!.DeviceName);
        Assert.Equal(TestData.Third.DeviceName, second.FullScreenOutput!.DeviceName);
        await second.ToggleBlankAsync(withMessage: false);
        Assert.True(second.IsBlanked);
        Assert.False(first.IsBlanked);
        await second.StopAsync();
        Assert.Equal(SessionState.Presenting, first.State);
        Assert.Equal(1, first.Source!.Handle);
    }
}
