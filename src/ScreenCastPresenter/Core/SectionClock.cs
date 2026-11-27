namespace ScreenCastPresenter.Core;

/// <summary>A warning about the time spent on what's shown.</summary>
internal enum SectionAlert { None, OneMinuteLeft, Over }

/// <summary>How long was spent on one thing shown, and how long it was meant to take (zero: no budget).</summary>
internal sealed record SectionTime(string Key, string Name, TimeSpan Spent, TimeSpan Budget)
{
    public bool HasBudget => Budget > TimeSpan.Zero;
    public TimeSpan Over => HasBudget && Spent > Budget ? Spent - Budget : TimeSpan.Zero;
}

/// <summary>
/// Time budgets (FR-121): each favourite and scene can have a length ("Demo: 5 minutes"). While it's
/// shown the now-showing bar counts its time against that, and the presenter is told, privately,
/// when a minute is left and when it's over. Coming back to something adds to its time; the talk
/// timer restarting starts them all again. Everything shown is timed (budget or not), which is what
/// a rehearsal report lists (FR-127). Times come from the caller's clock, so tests need no waiting.
/// </summary>
internal sealed class SectionClock
{
    public const int MaxMinutes = 180;

    private readonly Dictionary<string, (string Name, TimeSpan Spent, TimeSpan Budget)> _sections = [];
    private readonly List<string> _order = [];
    private string? _current;
    private DateTime _since;
    private bool _running;
    private readonly HashSet<(string Key, SectionAlert Alert)> _told = [];

    /// <summary>A budget in minutes tidied: 0 (none) to <see cref="MaxMinutes"/>.</summary>
    public static int Normalize(int minutes) => Math.Clamp(minutes, 0, MaxMinutes);

    /// <summary>Something else is shown (or the same thing again). Its time keeps adding up from where it was.</summary>
    public void Show(string key, string name, TimeSpan budget, DateTime now)
    {
        Pause(now);
        if (!_sections.TryGetValue(key, out var section))
        {
            section = (name, TimeSpan.Zero, budget);
            _order.Add(key);
        }
        _sections[key] = (name.Length > 0 ? name : section.Name, section.Spent, budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
        _current = key;
        _since = now;
        _running = true;
    }

    /// <summary>The time stops counting (blank, a break, stopped showing) until <see cref="Resume"/>.</summary>
    public void Pause(DateTime now)
    {
        if (!_running || _current is null) return;
        var s = _sections[_current];
        if (now > _since) _sections[_current] = (s.Name, s.Spent + (now - _since), s.Budget);
        _running = false;
    }

    public void Resume(DateTime now)
    {
        if (_running || _current is null) return;
        _since = now;
        _running = true;
    }

    /// <summary>Everything starts from nothing again (the talk timer restarted, or a new rehearsal).</summary>
    public void Reset()
    {
        _sections.Clear();
        _order.Clear();
        _told.Clear();
        _current = null;
        _running = false;
    }

    /// <summary>What's being timed now, with its time so far; null when nothing is.</summary>
    public SectionTime? Current(DateTime now)
    {
        if (_current is null) return null;
        var s = _sections[_current];
        TimeSpan spent = s.Spent + (_running && now > _since ? now - _since : TimeSpan.Zero);
        return new SectionTime(_current, s.Name, spent, s.Budget);
    }

    /// <summary>Everything timed so far, in the order first shown.</summary>
    public IReadOnlyList<SectionTime> All(DateTime now) =>
        [.. _order.Select(k => k == _current && Current(now) is { } c ? c : new SectionTime(k, _sections[k].Name, _sections[k].Spent, _sections[k].Budget))];

    /// <summary>A warning due now for what's shown, each said once per thing (not again when coming back to it).</summary>
    public SectionAlert Check(DateTime now)
    {
        if (Current(now) is not { HasBudget: true } c || !_running) return SectionAlert.None;
        SectionAlert alert = c.Spent >= c.Budget ? SectionAlert.Over
            : c.Budget > TimeSpan.FromMinutes(1) && c.Budget - c.Spent <= TimeSpan.FromMinutes(1) ? SectionAlert.OneMinuteLeft
            : SectionAlert.None;
        if (alert == SectionAlert.None || !_told.Add((c.Key, alert))) return SectionAlert.None;
        if (alert == SectionAlert.Over) _told.Add((c.Key, SectionAlert.OneMinuteLeft)); // jumped straight past it
        return alert;
    }

    /// <summary>"Demo 3:12 of 5:00", "Demo 1:05 over", or "Demo 3:12" without a budget.</summary>
    public static string Text(SectionTime s)
    {
        string name = TrayMenuPlan.Shorten(s.Name, 24);
        if (!s.HasBudget) return $"{name} {BlankCountdown.Clock(s.Spent)}";
        return s.Spent > s.Budget ? $"{name} {BlankCountdown.Clock(s.Over)} over" : $"{name} {BlankCountdown.Clock(s.Spent)} of {BlankCountdown.Clock(s.Budget)}";
    }
}
