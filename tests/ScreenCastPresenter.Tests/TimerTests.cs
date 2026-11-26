using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-66: blank with a countdown.</summary>
public class BlankCountdownTests
{
    [Theory]
    [InlineData(300, "5:00")]
    [InlineData(299.2, "5:00")]   // rounded up: it never shows less than is left
    [InlineData(59.01, "1:00")]
    [InlineData(7, "0:07")]
    [InlineData(0.4, "0:01")]
    [InlineData(0, "0:00")]
    [InlineData(-5, "0:00")]
    [InlineData(3725, "1:02:05")]
    public void The_clock_reads_like_a_timer(double seconds, string expected) =>
        Assert.Equal(expected, BlankCountdown.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_TV_shows_the_message_then_the_time_left()
    {
        Assert.Equal("We'll be right back\nBack in 4:59", BlankCountdown.Text("We'll be right back", TimeSpan.FromSeconds(298.5)));
        Assert.Equal("Coffee\nStarting again in a moment", BlankCountdown.Text("Coffee", TimeSpan.Zero));
        Assert.Equal("Coffee\nStarting again in a moment", BlankCountdown.Text("Coffee", TimeSpan.FromSeconds(-30)));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(4, 5)]
    [InlineData(0, 1)]
    [InlineData(1000, 20)]
    public void Lengths_snap_to_the_offered_ones(int minutes, int expected) => Assert.Equal(expected, BlankCountdown.Normalize(minutes));

    [Fact]
    public void Settings_repair_a_strange_length()
    {
        var s = new AppSettings { CountdownMinutes = -3, TalkMinutes = 33 };
        s.Normalize();
        Assert.Equal(1, s.CountdownMinutes);
        Assert.Equal(30, s.TalkMinutes);
        Assert.Equal(5, new AppSettings().CountdownMinutes);
    }
}

/// <summary>FR-67: the talk timer in the now-showing bar.</summary>
public class TalkTimerTests
{
    private static readonly DateTime T0 = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Counts_from_when_showing_started_and_doesnt_restart_on_a_switch()
    {
        var timer = new TalkTimer();
        timer.Start(T0);
        timer.Start(T0.AddMinutes(3)); // a switch: still the same talk
        Assert.Equal("12:04", timer.Text(T0.AddMinutes(12).AddSeconds(4.9), 0));
        Assert.Equal("12:04 of 20:00", timer.Text(T0.AddMinutes(12).AddSeconds(4), 20));
    }

    [Fact]
    public void Stopping_resets_it()
    {
        var timer = new TalkTimer();
        timer.Start(T0);
        timer.Reset();
        Assert.Null(timer.Started);
        Assert.Equal(TimeSpan.Zero, timer.Elapsed(T0.AddHours(1)));
        timer.Start(T0.AddHours(1));
        Assert.Equal("0:30", timer.Text(T0.AddHours(1).AddSeconds(30), 0));
    }

    [Fact]
    public void Warns_once_two_minutes_before_and_once_at_the_end()
    {
        var timer = new TalkTimer();
        timer.Start(T0);
        Assert.Equal(TalkAlert.None, timer.Check(T0.AddMinutes(17), 20));
        Assert.Equal(TalkAlert.TwoMinutesLeft, timer.Check(T0.AddMinutes(18), 20));
        Assert.Equal(TalkAlert.None, timer.Check(T0.AddMinutes(18.5), 20));
        Assert.Equal(TalkAlert.TimeUp, timer.Check(T0.AddMinutes(20), 20));
        Assert.Equal(TalkAlert.None, timer.Check(T0.AddMinutes(25), 20));
    }

    [Fact]
    public void Arriving_late_skips_straight_to_times_up()
    {
        var timer = new TalkTimer();
        timer.Start(T0);
        Assert.Equal(TalkAlert.TimeUp, timer.Check(T0.AddMinutes(30), 20)); // the PC was asleep: one warning, not two
        Assert.Equal(TalkAlert.None, timer.Check(T0.AddMinutes(31), 20));
    }

    [Fact]
    public void No_length_or_not_started_means_no_warnings()
    {
        var timer = new TalkTimer();
        Assert.Equal(TalkAlert.None, timer.Check(T0, 20));
        timer.Start(T0);
        Assert.Equal(TalkAlert.None, timer.Check(T0.AddHours(5), 0));
    }

    [Fact]
    public void A_clock_change_backwards_never_shows_negative_time()
    {
        var timer = new TalkTimer();
        timer.Start(T0);
        Assert.Equal(TimeSpan.Zero, timer.Elapsed(T0.AddMinutes(-10)));
    }
}

/// <summary>The session's part in a countdown: blanking with a message and changing it in place.</summary>
public class SessionCountdownTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;
    private readonly List<string> _messages = [];

    public SessionCountdownTests()
    {
        _session = new SessionController(_platform);
        _session.Message += (_, m) => _messages.Add(m);
    }

    private static SourceWindow Notepad => TestData.Window(101, "Notes - Notepad");

    [Fact]
    public async Task Blanks_through_black_and_shows_the_message()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.BlankWithMessageAsync("Back soon\nBack in 5:00");
        Assert.True(_session.IsBlanked);
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]);
        Assert.Equal("Back soon\nBack in 5:00", _platform.Outputs[^1].Message);
        Assert.Equal("Back soon\nBack in 5:00", _session.IdleMessage);
    }

    [Fact]
    public async Task Ticking_changes_the_text_without_a_fade()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.BlankWithMessageAsync("Back in 5:00");
        int fades = _platform.Fades.Count;
        _session.UpdateIdleMessage("Back in 4:59");
        Assert.Equal("Back in 4:59", _platform.Outputs[^1].Message);
        Assert.Equal(1, _platform.Outputs[^1].MessageBrightness);
        Assert.Equal("Back in 4:59", _platform.Engines[^1].IdleMessage);
        Assert.Equal(fades, _platform.Fades.Count);
    }

    [Fact]
    public async Task Showing_again_ends_it_and_later_ticks_change_nothing()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.BlankWithMessageAsync("Back in 5:00");
        await _session.ToggleBlankAsync(withMessage: false);
        Assert.False(_session.IsBlanked);
        Assert.Null(_session.IdleMessage);
        _session.UpdateIdleMessage("Back in 4:58");
        Assert.Null(_platform.Outputs[^1].Message);
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1], 3);
    }

    [Fact]
    public async Task Already_blank_with_a_message_just_changes_the_text()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: true);
        await _session.BlankWithMessageAsync("Back in 2:00");
        Assert.Equal("Back in 2:00", _platform.Outputs[^1].Message);
        Assert.True(_session.IsBlanked);
    }

    [Fact]
    public async Task Nothing_shown_says_so()
    {
        await _session.BlankWithMessageAsync("Back in 5:00");
        Assert.False(_session.IsBlanked);
        Assert.Contains("Nothing is being shown right now.", _messages);
    }

    [Fact]
    public async Task A_blank_press_queued_before_the_countdown_doesnt_undo_it()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task press = _session.ToggleBlankAsync(withMessage: false);   // fading out, held
        Task countdown = _session.BlankWithMessageAsync("Back in 1:00");
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(press, countdown);
        Assert.True(_session.IsBlanked);
        Assert.Equal("Back in 1:00", _session.IdleMessage);
    }
}
