using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>Session behaviour against fakes: every fade, switch and failure path without touching Windows.</summary>
public class SessionControllerTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;
    private readonly List<string> _messages = [];

    public SessionControllerTests()
    {
        _session = new SessionController(_platform);
        _session.Message += (_, m) => _messages.Add(m);
    }

    private static SourceWindow Notepad => TestData.Window(101, "Notes - Notepad");
    private static SourceWindow Deck => TestData.Window(202, "Deck - PowerPoint", "POWERPNT");

    [Fact]
    public async Task Present_creates_a_cloaked_output_then_reveals_and_fades_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, topMost: true);

        FakeOutput output = Assert.Single(_platform.Outputs);
        Assert.Equal(["ShowCloaked", "Reveal"], output.Calls.Where(c => c is "ShowCloaked" or "Reveal"));
        Assert.Equal(SessionState.Presenting, _session.State);
        Assert.Equal(1, _session.CurrentOpacity, 3);
        FakeEngine engine = Assert.Single(_platform.Engines);
        Assert.Equal(0, engine.Opacities[0], 3);     // starts invisible (NFR-12)
        Assert.Equal(1, engine.Opacities[^1], 3);
        Assert.Equal(SessionController.FirstFadeIn, _platform.Fades.Last().Duration);
    }

    private static OutputPlacement Floating => new(TestData.Laptop, false, new Rectangle(1000, 600, 800, 480));

    [Fact]
    public async Task A_floating_window_is_reused_across_switches_and_isnt_a_full_screen()
    {
        await _session.PresentAsync(Notepad, Floating);
        await _session.PresentAsync(Deck, Floating with { Window = new Rectangle(0, 0, 640, 360) }); // where it was saved doesn't matter now
        FakeOutput output = Assert.Single(_platform.Outputs);
        Assert.True(output.IsFloating);
        Assert.True(_session.OutputIsFloating);
        Assert.Null(_session.FullScreenOutput); // the presenter's screen stays theirs: badge, picker and outline may use it
        Assert.Equal(TestData.Laptop, _session.OutputMonitor);
    }

    [Fact]
    public async Task Switching_between_fullscreen_and_a_window_replaces_the_output()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.MoveOutputAsync(Floating);
        Assert.Equal(2, _platform.Outputs.Count);
        Assert.True(_platform.Outputs[0].Disposed);
        Assert.True(_platform.Outputs[1].IsFloating);
        Assert.Equal(Notepad, _session.Source); // still showing the same thing

        await _session.MoveOutputAsync(TestData.Tv, true);
        Assert.Equal(3, _platform.Outputs.Count);
        Assert.False(_platform.Outputs[2].IsFloating);
        Assert.Equal(TestData.Tv, _session.FullScreenOutput);
    }

    [Fact]
    public async Task Closing_the_floating_window_stops_showing_and_remembers_where_it_was()
    {
        await _session.PresentAsync(Notepad, Floating);
        FakeOutput output = _platform.Outputs[0];
        output.WindowBounds = new Rectangle(50, 60, 700, 400); // the user moved and resized it
        output.UserCloses();
        await Task.Yield();
        for (int i = 0; i < 20 && _session.State != SessionState.Off; i++) await Task.Delay(10);
        Assert.Equal(SessionState.Off, _session.State);
        Assert.True(output.Disposed);
        Assert.Equal(new Rectangle(50, 60, 700, 400), _session.FloatingBounds);
    }

    [Fact]
    public async Task Showing_your_own_screen_in_the_window_makes_it_a_private_preview()
    {
        // One laptop screen, the window on it, the whole laptop screen shown (Teams-style preview).
        await _session.PresentAsync(SourceWindow.ForScreen(TestData.Laptop), Floating);
        Assert.True(_session.IsPrivatePreview);
        Assert.True(_platform.Outputs[0].PrivatePreview); // hidden from capture: no endless mirror, never in the share

        await _session.PresentAsync(Notepad, Floating); // a window: shareable again
        Assert.False(_session.IsPrivatePreview);
        Assert.False(_platform.Outputs[0].PrivatePreview);

        await _session.PresentAsync(SourceWindow.ForScreen(TestData.Tv), Floating); // another screen: shareable
        Assert.False(_platform.Outputs[0].PrivatePreview);
    }

    [Fact]
    public async Task A_full_screen_output_is_never_made_private()
    {
        await _session.PresentAsync(SourceWindow.ForScreen(TestData.Laptop), TestData.Tv, true);
        Assert.False(_session.IsPrivatePreview);
        Assert.False(_platform.Outputs[0].PrivatePreview);
    }

    [Fact]
    public async Task The_floating_window_takes_the_shape_of_what_it_shows_before_it_is_seen()
    {
        await _session.PresentAsync(Notepad, Floating);
        FakeOutput output = _platform.Outputs[0];
        Assert.Equal(new Size(1600, 900), output.Shapes[^1]);
        Assert.True(output.ShapedBeforeReveal, "never seen with black bars");

        _platform.SourceSize = new Size(900, 1200); // a different, portrait source
        await _session.PresentAsync(Deck, Floating);
        Assert.Equal(new Size(900, 1200), output.Shapes[^1]);
    }

    [Fact]
    public async Task Only_a_floating_window_steps_back()
    {
        await _session.PresentAsync(Notepad, Floating);
        _session.SetOutputSteppedBack(true);
        _session.SetOutputSteppedBack(false);
        Assert.Equal([true, false], _platform.Outputs[0].StepBacks);

        await _session.MoveOutputAsync(new OutputPlacement(TestData.Tv, true));
        _session.SetOutputSteppedBack(true); // full screen on the TV: never steps back
        Assert.Empty(_platform.Outputs[^1].StepBacks);
    }

    [Fact]
    public async Task A_minimised_source_never_reshapes_the_floating_window_and_restoring_brings_its_shape_back()
    {
        await _session.PresentAsync(Notepad, Floating);
        FakeOutput output = _platform.Outputs[0];
        FakeWatcher watcher = _platform.Watchers[0];
        int before = output.Shapes.Count;

        _platform.SourceSize = new Size(160, 28); // what a minimised window measures as
        watcher.Minimize();
        watcher.RaiseChanged(); // Windows reports the move to the minimised spot
        Assert.Equal(before, output.Shapes.Count);

        _platform.SourceSize = new Size(1600, 900);
        watcher.Restore();
        for (int i = 0; i < 20 && output.Shapes.Count == before; i++) await Task.Delay(10);
        Assert.Equal(new Size(1600, 900), output.Shapes[^1]);
    }

    [Fact]
    public async Task A_crop_reshapes_the_floating_window_to_the_part_shown()
    {
        await _session.PresentAsync(Notepad, Floating);
        await _session.SetCropAsync(CropRegion.Create(0, 0, 0.5, 1));
        Assert.Equal(new Size(800, 900), _platform.Outputs[0].Shapes[^1]);
    }

    [Fact]
    public async Task A_full_screen_output_keeps_the_screen_shape()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Empty(_platform.Outputs[0].Shapes); // letterboxed on the TV, as before
    }

    [Fact]
    public async Task Double_clicking_the_output_is_passed_on_and_a_closed_output_is_ignored()
    {
        int clicks = 0;
        _session.OutputDoubleClicked += (_, _) => clicks++;
        await _session.PresentAsync(Notepad, Floating);
        FakeOutput first = _platform.Outputs[0];
        first.UserDoubleClicks();
        Assert.Equal(1, clicks);

        await _session.MoveOutputAsync(new OutputPlacement(TestData.Tv, true));
        first.UserDoubleClicks(); // the old window is gone
        Assert.Equal(1, clicks);
        _platform.Outputs[^1].UserDoubleClicks();
        Assert.Equal(2, clicks);
    }

    [Fact]
    public async Task Minimising_the_floating_window_warns_that_Zoom_shows_nothing()
    {
        await _session.PresentAsync(Notepad, Floating);
        _platform.Outputs[0].SetMinimized(true);
        Assert.Contains(_messages, m => m.Contains("minimised", StringComparison.Ordinal) && m.Contains("Zoom", StringComparison.Ordinal));
        int count = _messages.Count;
        _platform.Outputs[0].SetMinimized(false); // restoring says nothing
        Assert.Equal(count, _messages.Count);
    }

    [Fact]
    public async Task Switching_on_the_same_screen_reuses_the_output_and_goes_through_black()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.PresentAsync(Deck, TestData.Tv, true);

        Assert.Single(_platform.Outputs);                          // no new window, no flash
        FakeEngine first = _platform.Engines[0];
        Assert.Equal(0, first.Opacities[^1], 3);                    // old source faded to black...
        Assert.True(first.Disposed);
        Assert.Equal(0, _platform.Engines[1].Opacities[0], 3);      // ...new one starts black
        Assert.Equal(Notepad, _session.PreviousSource);
        Assert.Equal(Deck, _session.Source);
    }

    [Fact]
    public async Task Presenting_the_same_window_again_does_nothing()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        int fades = _platform.Fades.Count;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Single(_platform.Engines);
        Assert.Equal(fades, _platform.Fades.Count);
    }

    [Fact]
    public async Task Moving_to_another_screen_replaces_the_output()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.MoveOutputAsync(TestData.Third, true);
        Assert.Equal(2, _platform.Outputs.Count);
        Assert.True(_platform.Outputs[0].Disposed);
        Assert.Contains("Conceal", _platform.Outputs[0].Calls); // cloaked before closing
        Assert.Equal(TestData.Third, _session.OutputMonitor);
    }

    [Fact]
    public async Task Blank_fades_to_black_and_back_and_message_fades_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: true);
        Assert.True(_session.IsBlanked);
        Assert.Equal(0, _session.CurrentOpacity, 3);
        FakeOutput output = _platform.Outputs[0];
        Assert.Equal(_session.BlankMessage, output.Message);
        Assert.Equal(1, output.MessageBrightness, 3);

        await _session.ToggleBlankAsync(withMessage: true);
        Assert.False(_session.IsBlanked);
        Assert.Null(output.Message);
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Blank_with_nothing_showing_explains_instead_of_failing()
    {
        await _session.ToggleBlankAsync(false);
        Assert.Equal(SessionState.Off, _session.State);
        Assert.Contains(_messages, m => m.Contains("Nothing is being shown"));
    }

    [Fact]
    public async Task Picking_a_new_source_while_blanked_shows_it()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(false);
        await _session.PresentAsync(Deck, TestData.Tv, true);
        Assert.False(_session.IsBlanked);
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Picking_the_same_source_while_blanked_unblanks()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(false);
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.False(_session.IsBlanked);
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Dimming_never_goes_above_the_dimmed_level()
    {
        await _session.SetDimAsync(0.3);
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.All(_platform.Engines[0].Opacities, o => Assert.True(o <= 0.7 + 1e-9, $"{o}"));
        Assert.Equal(0.7, _session.CurrentOpacity, 3);

        await _session.SetDimAsync(5); // clamped
        Assert.Equal(1 - AppSettings.MaxDim, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Source_closing_leaves_the_idle_screen()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.Watchers[0].RaiseClosed();
        Assert.Equal(SessionState.IdleScreen, _session.State);
        Assert.Null(_session.Source);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.Contains(_messages, m => m.Contains("was closed"));
        Assert.False(_platform.Outputs[0].Disposed); // idle screen stays (FR-06, FR-16)
    }

    [Fact]
    public async Task Events_from_an_old_source_are_ignored()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        FakeWatcher old = _platform.Watchers[0];
        await _session.PresentAsync(Deck, TestData.Tv, true);
        old.RaiseClosed();
        Assert.Equal(SessionState.Presenting, _session.State);
        Assert.Equal(Deck, _session.Source);
    }

    [Fact]
    public async Task Minimise_cuts_to_black_and_restore_fades_back()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        FakeWatcher watcher = _platform.Watchers[0];
        watcher.Minimize();
        Assert.Equal(0, _session.CurrentOpacity, 3);
        watcher.Restore();
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Restore_while_blanked_stays_black()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(false);
        FakeWatcher watcher = _platform.Watchers[0];
        watcher.Minimize();
        watcher.Restore();
        Assert.Equal(0, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Minimised_source_is_presented_black_with_an_explanation()
    {
        _platform.NextWatcherMinimized = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Equal(0, _session.CurrentOpacity, 3);
        Assert.Contains(_messages, m => m.Contains("minimised"));
    }

    [Fact]
    public async Task Engine_failure_leaves_a_black_idle_screen_not_a_crash()
    {
        _platform.OfferFallback = false;
        _platform.FailNextEngine = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Equal(SessionState.IdleScreen, _session.State);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.Contains(_messages, m => m.StartsWith("Couldn't show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watcher_failure_disposes_the_engine()
    {
        _platform.FailNextWatcher = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.Equal(SessionState.IdleScreen, _session.State);
    }

    // ── browsers: another tab or page cuts to black at once, and comes back ──

    [Fact]
    public async Task Moving_off_the_chosen_page_cuts_to_black_at_once_without_a_fade()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        FakeEngine engine = _platform.Engines[^1];
        int fades = _platform.Fades.Count;
        _session.CutAway();
        Assert.True(_session.IsAwayFromPage);
        Assert.Equal(0, engine.Opacities[^1]);
        Assert.Equal(0, _session.TargetOpacity);
        Assert.Equal(fades, _platform.Fades.Count); // no fade: the new page is never seen
        _session.CutAway(); // twice is fine
        Assert.True(_session.IsAwayFromPage);
    }

    [Fact]
    public async Task A_fade_that_is_running_cant_bring_the_new_page_back()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        FakeEngine engine = _platform.Engines[^1];
        _platform.HoldFades = true;
        Task dim = _session.SetDimAsync(0.2); // a fade towards 80%, held mid-way
        _session.CutAway();
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await dim;
        Assert.Equal(0, engine.Opacities[^1]);
    }

    [Fact]
    public async Task Going_back_to_the_chosen_page_fades_it_back_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _session.CutAway();
        await _session.ComeBackAsync();
        Assert.False(_session.IsAwayFromPage);
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1]);
    }

    [Fact]
    public async Task Coming_back_while_blanked_stays_blank()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: false);
        _session.CutAway();
        await _session.ComeBackAsync();
        Assert.True(_session.IsBlanked);
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]);
    }

    [Fact]
    public async Task Showing_something_else_starts_on_its_own_page()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _session.CutAway();
        await _session.PresentAsync(Deck, TestData.Tv, true);
        Assert.False(_session.IsAwayFromPage);
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1]);
    }

    [Fact]
    public void Nothing_shown_nothing_to_cut()
    {
        _session.CutAway();
        Assert.False(_session.IsAwayFromPage);
    }

    [Fact]
    public async Task Rapid_switches_are_coalesced_to_the_last_one()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;

        Task first = _session.PresentAsync(Deck, TestData.Tv, true);         // starts fading out, then waits
        var queued = Enumerable.Range(0, 20).Select(i => _session.PresentAsync(TestData.Window(1000 + i, $"W{i}"), TestData.Tv, true)).ToList();

        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(queued.Append(first));

        // Notepad, Deck, and only the newest of the 20 queued requests.
        Assert.Equal(3, _platform.Engines.Count);
        Assert.Equal(1019, _session.Source!.Handle);
    }

    [Fact]
    public async Task Stop_cancels_queued_switches_and_closes_the_output()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task switching = _session.PresentAsync(Deck, TestData.Tv, true);
        Task queued = _session.PresentAsync(TestData.Window(7, "Queued"), TestData.Tv, true);
        Task stop = _session.StopAsync();

        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(switching, queued, stop);

        Assert.Equal(SessionState.Off, _session.State);
        Assert.DoesNotContain(_platform.Engines, e => e.Source == 7);
        Assert.All(_platform.Outputs, o => Assert.True(o.Disposed));
    }

    [Theory]
    [InlineData(501, true)]
    [InlineData(500, false)]
    public async Task Mashing_blank_runs_at_most_one_more_fade_and_keeps_parity(int presses, bool blankedAtEnd)
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        var tasks = Enumerable.Range(0, presses).Select(_ => _session.ToggleBlankAsync(false)).ToList();
        int fadesWhileHeld = _platform.Fades.Count;
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(tasks);

        Assert.Equal(blankedAtEnd, _session.IsBlanked);
        Assert.True(_platform.Fades.Count - fadesWhileHeld <= 2, $"{_platform.Fades.Count - fadesWhileHeld} extra fades: a backlog built up");
    }

    [Fact]
    public async Task Mashing_dim_applies_only_the_last_level()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task first = _session.SetDimAsync(0.1);
        var rest = Enumerable.Range(0, 50).Select(i => _session.SetDimAsync(i % 2 == 0 ? 0.4 : 0.2)).ToList();
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(rest.Append(first));
        Assert.Equal(0.2, _session.Dim, 6);
        Assert.Equal(0.8, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Stop_cancels_queued_blanks()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task blank = _session.ToggleBlankAsync(false);
        Task queued = _session.ToggleBlankAsync(false);
        Task stop = _session.StopAsync();
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(blank, queued, stop);
        Assert.Equal(SessionState.Off, _session.State);
        Assert.False(_session.IsBlanked);
    }

    // ───────────── M3: engines, freeze, crop ─────────────

    [Fact]
    public async Task Failing_engine_at_start_falls_back_to_the_other_engine()
    {
        _platform.FailNextEngine = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Equal(SessionState.Presenting, _session.State);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.True(_platform.Engines[1].IsFallback);
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Engine_failing_mid_session_falls_back_and_fades_back_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.Engines[0].RaiseFailed();
        Assert.True(_platform.Engines[0].Disposed);
        FakeEngine fallback = _platform.Engines[^1];
        Assert.True(fallback.IsFallback);
        Assert.False(fallback.Disposed);
        Assert.Equal(1, _session.CurrentOpacity, 3);
        Assert.Equal(SessionState.Presenting, _session.State);
    }

    [Fact]
    public async Task Engine_failing_with_no_fallback_goes_idle()
    {
        _platform.OfferFallback = false;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.Engines[0].RaiseFailed();
        Assert.Equal(SessionState.IdleScreen, _session.State);
        Assert.Contains(_messages, m => m.Contains("stopped working"));
    }

    [Fact]
    public async Task Freeze_on_a_freezable_engine_toggles_without_a_new_engine()
    {
        _platform.EnginesSupportFreeze = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleFreezeAsync();
        Assert.True(_session.IsFrozen);
        Assert.True(_platform.Engines[0].Frozen);
        await _session.ToggleFreezeAsync();
        Assert.False(_session.IsFrozen);
        Assert.False(_platform.Engines[0].Frozen);
        Assert.Single(_platform.Engines);
    }

    [Fact]
    public async Task Freeze_on_dwm_hands_over_to_a_frozen_engine_at_the_same_brightness()
    {
        await _session.SetDimAsync(0.2);
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleFreezeAsync();

        Assert.Equal(2, _platform.Engines.Count);
        Assert.True(_platform.Engines[0].Disposed);       // the live DWM thumbnail is gone...
        FakeEngine frozen = _platform.Engines[1];
        Assert.True(frozen.StartFrozen);                  // ...the frozen copy was already underneath
        Assert.Equal(0.8, frozen.Opacities[^1], 3);       // at the same brightness: no visible jump
        Assert.True(_session.IsFrozen);
    }

    [Fact]
    public async Task Freeze_with_nothing_showing_explains()
    {
        await _session.ToggleFreezeAsync();
        Assert.Contains(_messages, m => m.Contains("Nothing is being shown"));
    }

    [Fact]
    public async Task Switching_source_ends_freeze()
    {
        _platform.EnginesSupportFreeze = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleFreezeAsync();
        await _session.PresentAsync(Deck, TestData.Tv, true);
        Assert.False(_session.IsFrozen);
    }

    [Fact]
    public async Task Minimised_source_keeps_its_last_picture_on_a_freezable_engine()
    {
        _platform.EnginesSupportFreeze = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.Watchers[0].Minimize();
        Assert.Equal(1, _session.CurrentOpacity, 3);      // UC-06: not cut to black
        Assert.Contains(_messages, m => m.Contains("last picture"));
    }

    [Fact]
    public async Task Crop_change_fades_through_black_and_updates_the_source()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        var crop = new CropRegion(0.1, 0.1, 0.5, 0.5);
        await _session.SetCropAsync(crop);
        Assert.Equal(crop, _platform.Engines[0].Crop);
        Assert.Equal(crop, _session.Source!.Crop);
        Assert.Contains(0.0, _platform.Engines[0].Opacities.Skip(3)); // went through black
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Presenting_the_same_window_with_a_crop_applies_the_crop_only()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        var crop = new CropRegion(0, 0, 0.5, 1);
        await _session.PresentAsync(Notepad with { Crop = crop }, TestData.Tv, true);
        Assert.Single(_platform.Engines);
        Assert.Equal(crop, _platform.Engines[0].Crop);
    }

    [Fact]
    public async Task Full_crop_is_the_same_as_no_crop()
    {
        await _session.PresentAsync(Notepad with { Crop = new CropRegion(0.2, 0.2, 0.5, 0.5) }, TestData.Tv, true);
        await _session.SetCropAsync(CropRegion.Full);
        Assert.Null(_session.Source!.Crop);
    }

    [Fact]
    public async Task Blank_message_is_drawn_by_the_engine_too()
    {
        _platform.EnginesSupportFreeze = true; // WGC covers the window, so it must draw the message itself
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: true);
        Assert.Equal(_session.BlankMessage, _platform.Engines[0].IdleMessage);
        await _session.ToggleBlankAsync(withMessage: true);
        Assert.Null(_platform.Engines[0].IdleMessage);
    }

    [Fact]
    public async Task Screen_sources_can_be_presented()
    {
        await _session.PresentAsync(SourceWindow.ForScreen(TestData.Laptop), TestData.Tv, true);
        Assert.Equal(SessionState.Presenting, _session.State);
        Assert.True(_session.Source!.IsScreen);
        Assert.True(_platform.Engines[0].SupportsFreeze);
    }

    [Fact]
    public async Task Stop_when_nothing_is_showing_is_harmless()
    {
        int events = 0;
        _session.StateChanged += (_, _) => events++;
        await _session.StopAsync();
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task A_thousand_switches_leave_exactly_one_live_engine()
    {
        for (int i = 0; i < 1000; i++)
            await _session.PresentAsync(TestData.Window(5000 + i % 7, $"W{i}"), TestData.Tv, true);
        Assert.Single(_platform.Engines, e => !e.Disposed);
        Assert.Single(_platform.Watchers, w => !w.Disposed);
        Assert.Single(_platform.Outputs);
    }
}

internal sealed class FakePlatform : ISessionPlatform
{
    private readonly List<(TaskCompletionSource Done, double To, Action<double> Apply)> _held = [];

    public List<FakeOutput> Outputs { get; } = [];
    public List<FakeEngine> Engines { get; } = [];
    public List<FakeWatcher> Watchers { get; } = [];
    public List<(double From, double To, TimeSpan Duration)> Fades { get; } = [];
    public bool FailNextEngine { get; set; }
    public bool FailNextWatcher { get; set; }
    public bool NextWatcherMinimized { get; set; }
    public bool HoldFades { get; set; }

    /// <summary>Engines created for windows can freeze (like WGC) when true; otherwise they behave like DWM.</summary>
    public bool EnginesSupportFreeze { get; set; }

    /// <summary>Whether a fallback engine is offered when one fails.</summary>
    public bool OfferFallback { get; set; } = true;

    /// <summary>What every source "is" in size (before its crop); null: unknown.</summary>
    public Size? SourceSize { get; set; } = new Size(1600, 900);

    public Size? ContentSize(SourceWindow source) =>
        SourceSize is { } whole ? source.Crop is { } crop ? crop.ToPixels(whole).Size : whole : null;

    public IOutputSurface CreateOutput(OutputPlacement placement)
    {
        var output = new FakeOutput(placement.Monitor) { IsFloating = placement.IsFloating, WindowBounds = placement.Window ?? placement.Monitor.Bounds };
        Outputs.Add(output);
        return output;
    }

    public ICaptureEngine CreateEngine(SourceWindow source, bool startFrozen = false)
    {
        var engine = new FakeEngine(FailNextEngine, supportsFreeze: EnginesSupportFreeze || startFrozen || source.IsScreen) { StartFrozen = startFrozen };
        FailNextEngine = false;
        Engines.Add(engine);
        return engine;
    }

    public ICaptureEngine? CreateFallbackEngine(SourceWindow source, ICaptureEngine failed)
    {
        if (!OfferFallback) return null;
        var engine = new FakeEngine(false, supportsFreeze: !((FakeEngine)failed).SupportsFreeze) { IsFallback = true };
        Engines.Add(engine);
        return engine;
    }

    public ISourceWatcher Watch(SourceWindow source)
    {
        if (FailNextWatcher)
        {
            FailNextWatcher = false;
            throw new InvalidOperationException("The window no longer exists.");
        }
        var watcher = new FakeWatcher { IsMinimized = NextWatcherMinimized };
        NextWatcherMinimized = false;
        Watchers.Add(watcher);
        return watcher;
    }

    public Task Fade(double from, double to, TimeSpan duration, Action<double> apply)
    {
        Fades.Add((from, to, duration));
        apply(from);
        if (HoldFades)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add((done, to, apply));
            return done.Task;
        }
        apply((from + to) / 2);
        apply(to);
        return Task.CompletedTask;
    }

    public void ReleaseHeldFades()
    {
        foreach ((TaskCompletionSource done, double to, Action<double> apply) in _held.ToList())
        {
            apply(to);
            done.SetResult();
        }
        _held.Clear();
    }
}

internal sealed class FakeOutput(DisplayMonitor monitor) : IOutputSurface
{
    public List<string> Calls { get; } = [];
    public string? Message { get; private set; }
    public double MessageBrightness { get; private set; }
    public bool Disposed { get; private set; }
    public DisplayMonitor Monitor { get; } = monitor;
    public bool IsFloating { get; init; }
    public bool IsMinimized { get; private set; }
    public Rectangle WindowBounds { get; set; }
    public nint Handle => 999;
    public Size ClientSize => IsFloating ? WindowBounds.Size : Monitor.Bounds.Size;
    public bool IsAlive => !Disposed;
    public event EventHandler? Resized { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler? MinimizedChanged;

    /// <summary>The user clicks ✕ on the floating window.</summary>
    public void UserCloses() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? DoubleClicked;

    /// <summary>The user double-clicks the output.</summary>
    public void UserDoubleClicks() => DoubleClicked?.Invoke(this, EventArgs.Empty);

    /// <summary>Every content shape it was given, and whether the first came before it was revealed.</summary>
    public List<Size?> Shapes { get; } = [];
    public bool ShapedBeforeReveal { get; private set; }

    /// <summary>Every step-back request, in order.</summary>
    public List<bool> StepBacks { get; } = [];

    public void StepBack(bool back) => StepBacks.Add(back);

    public void SetContentAspect(Size? content)
    {
        if (Shapes.Count == 0) ShapedBeforeReveal = !Calls.Contains("Reveal");
        Shapes.Add(content);
    }

    public bool PrivatePreview { get; private set; }

    public void SetPrivatePreview(bool on) => PrivatePreview = on;

    public void SetMinimized(bool minimized)
    {
        IsMinimized = minimized;
        MinimizedChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ShowCloaked() => Calls.Add("ShowCloaked");
    public void Reveal() => Calls.Add("Reveal");
    public void Conceal() => Calls.Add("Conceal");

    public void SetIdleMessage(string? message, double brightness = 1)
    {
        Message = message;
        MessageBrightness = brightness;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeEngine(bool fail, bool supportsFreeze) : ICaptureEngine
{
    public List<double> Opacities { get; } = [];
    public bool Disposed { get; private set; }
    public nint Source { get; private set; }
    public CropRegion? Crop { get; private set; }
    public bool Frozen { get; private set; }
    public bool StartFrozen { get; init; }
    public bool IsFallback { get; init; }
    public string? IdleMessage { get; private set; }
    public string Name => supportsFreeze ? "Fake WGC" : "Fake DWM";
    public bool SupportsFreeze => supportsFreeze;
    public event EventHandler<string>? Failed;

    public Task StartAsync(SourceWindow source, nint output, Size outputClientSize)
    {
        Source = source.Handle;
        Crop = source.Crop;
        Frozen = StartFrozen;
        if (fail) throw new InvalidOperationException("Capture refused.");
        Opacities.Add(0);
        return Task.CompletedTask;
    }

    public void SetOpacity(double opacity) => Opacities.Add(opacity);
    public void OnSourceChanged() { }
    public void OnOutputResized(Size outputClientSize) { }
    public void SetCrop(CropRegion? crop) => Crop = crop;
    public void SetFrozen(bool frozen) => Frozen = supportsFreeze && frozen;
    public void SetIdleMessage(string? message, double brightness) => IdleMessage = brightness > 0 ? message : null;
    public void RaiseFailed() => Failed?.Invoke(this, "GPU reset");
    public void Dispose() => Disposed = true;
}

internal sealed class FakeWatcher : ISourceWatcher
{
    public bool IsMinimized { get; set; }
    public bool Disposed { get; private set; }
    public event EventHandler? Closed;
    public event EventHandler? Changed;
    public event EventHandler? Minimized;
    public event EventHandler? Restored;

    public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Minimize()
    {
        IsMinimized = true;
        Minimized?.Invoke(this, EventArgs.Empty);
    }

    public void Restore()
    {
        IsMinimized = false;
        Restored?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disposed = true;
}
