using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// Split screen (FR-104): the main picture and up to three others side by side, stacked, in a
/// grid, or one large and the rest small. Each other picture follows the same privacy rules as the
/// main one: never-show apps refused, its hidden parts covered, automatic hiding applied, and a
/// whole screen with a never-show app on it held black.
/// </summary>
internal sealed partial class ScpApp
{
    /// <summary>The pictures beside the main one, in order.</summary>
    private readonly List<SourceWindow> _split = [];

    public IReadOnlyList<SourceWindow> SplitPictures => _split;

    public SplitLayout SplitLayoutNow => SplitScreen.Parse(Settings.SplitLayout);

    /// <summary>"split-screen": back to one picture, or choose something to show beside it.</summary>
    private void ToggleSplit()
    {
        if (_split.Count > 0)
        {
            StopSplit("Back to one picture.");
            return;
        }
        AddToSplit();
    }

    /// <summary>"split-add": choose one more picture (up to <see cref="SplitScreen.MaxOthers"/>).</summary>
    private void AddToSplit()
    {
        if (_session.State != SessionState.Presenting)
        {
            Message("Show something first, then put something beside it.");
            return;
        }
        if (_split.Count >= SplitScreen.MaxOthers)
        {
            Message($"Split screen shows {SplitScreen.MaxOthers + 1} pictures at most. Take one off first.");
            return;
        }
        OpenPicker(PickerMode.Split);
    }

    /// <summary>Why <paramref name="source"/> can't go beside what's shown; null when it can.</summary>
    internal string? SplitProblem(SourceWindow source)
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { } main) return "Show something first, then put something beside it.";
        if (source.IsSameSource(main)) return "That's already the main picture. Choose something else to show beside it.";
        if (_split.Any(s => s.IsSameSource(source))) return "That's already on the TV.";
        if (_split.Count >= SplitScreen.MaxOthers) return $"Split screen shows {SplitScreen.MaxOthers + 1} pictures at most. Take one off first.";
        if (IsNeverShown(source)) return $"“{source.AppName}” is on your never-show list, so it isn't shown.";
        if (source.Screen is { } screen && _session.FullScreenOutput is { } tv && screen.IsSameScreen(tv))
            return $"{screen.Name} is where your presentation is shown, so it can't be shown on itself.";
        if (source.IsWindow && !WindowEnumerator.IsAlive(source.Handle)) return $"“{Shorten(source.Title, 40)}” has closed.";
        return null;
    }

    /// <summary>Shows <paramref name="source"/> beside what's shown.</summary>
    public async Task AddSplitAsync(SourceWindow source, bool quiet = false)
    {
        if (SplitProblem(source) is { } problem)
        {
            Message(problem, IsNeverShown(source) ? BadgeDot.Warning : BadgeDot.Accent);
            return;
        }
        if (_inset is not null) RemoveInset(null); // one or the other: the small picture would cover a pane
        if (LeaveOutTaskbar(source) is not { } clear) return; // FR-112: a screen beside it without its taskbar
        source = clear;
        _split.Add(source);
        SendOtherHiddenParts(); // before the picture: its hidden parts are covered from its first frame
        UpdatePrivateAppGuard(); // a never-show app on a screen shown beside it: black before it's sent
        if (_session.Effects is null) await ReshowAsync(); // shown the light way: again through the helper, which sends the panes with it
        else _session.Effects.Split(SplitLayoutNow, _split);
        if (_qrText is { } qrNow) _session.Effects?.Qr(qrNow, QrCorner());
        UpdateWidget();
        _tray.Refresh();
        if (quiet) return;
        Message($"“{SourceName(source)}” shows beside it, {SplitScreen.Words(SplitLayoutNow)}.", BadgeDot.Accent,
            Button("Change the layout", NextSplitLayout, primary: false),
            Button("Back to one picture", () => StopSplit(null), primary: false));
    }

    /// <summary>"split-layout": the next arrangement.</summary>
    private void NextSplitLayout() => SetSplitLayout(SplitScreen.Next(SplitLayoutNow));

    /// <summary>Settings, the command line and the layout key: how split screen arranges its pictures.</summary>
    public void SetSplitLayout(SplitLayout layout)
    {
        Settings.SplitLayout = layout.ToString();
        Save();
        if (_split.Count == 0) return;
        _session.Effects?.Split(layout, _split);
        Message($"Split screen: {SplitScreen.Words(layout)}.", BadgeDot.Accent);
    }

    /// <summary>Back to one picture.</summary>
    private void StopSplit(string? why)
    {
        if (_split.Count == 0) return;
        _split.Clear();
        _session.Effects?.Split(SplitLayoutNow, []);
        UpdatePrivateAppGuard();
        UpdateWidget();
        _tray.Refresh();
        if (why is not null) Message(why);
    }

    /// <summary>One picture off; the rest stay.</summary>
    private void RemoveFromSplit(SourceWindow source, string? why)
    {
        if (_split.RemoveAll(s => s.IsSameSource(source)) == 0) return;
        _session.Effects?.Split(SplitLayoutNow, _split);
        UpdatePrivateAppGuard();
        UpdateWidget();
        _tray.Refresh();
        if (why is not null) Message(why);
    }

    /// <summary>Stopped showing: the panes go with the picture. What a pane showed became the main picture: it goes from the side.</summary>
    private void UpdateSplit()
    {
        if (_split.Count == 0) return;
        if (_session.State != SessionState.Presenting)
        {
            StopSplit(null);
            return;
        }
        if (_session.Source is { } main && _split.Any(s => s.IsSameSource(main))) RemoveFromSplit(main, "That's the main picture now, so it's off the side.");
    }

    /// <summary>A pane's window closed or its camera went away (the helper already took it off).</summary>
    private void OnPaneFailed(string reason)
    {
        int before = _split.Count;
        _split.RemoveAll(s => s.IsWindow && !WindowEnumerator.IsAlive(s.Handle));
        if (_split.Count != before) _session.Effects?.Split(SplitLayoutNow, _split);
        UpdatePrivateAppGuard();
        UpdateWidget();
        Message(reason, BadgeDot.Warning);
    }

    /// <summary>The never-show list grew: panes it now covers go at once.</summary>
    private void EnforceNeverShowInSplit()
    {
        foreach (SourceWindow s in _split.Where(IsNeverShown).ToList())
            RemoveFromSplit(s, $"“{s.AppName}” is on your never-show list, so it's off the TV.");
    }

    /// <summary>Hidden parts (FR-74) of the other pictures on the TV: the small picture and the panes.</summary>
    private void SendOtherHiddenParts()
    {
        if (_session.Effects is not { } effects) return;
        foreach (SourceWindow s in _split.Append(_inset).OfType<SourceWindow>())
            effects.HiddenFor(s, Redactions.For(Settings, s), Settings.HiddenPartStyle);
    }

    /// <summary>A new engine took over: it gets the other pictures' hidden parts, then the panes.</summary>
    private void ReplaySplit(IEffectsEngine effects)
    {
        foreach (SourceWindow s in _split.Append(_inset).OfType<SourceWindow>())
            effects.HiddenFor(s, Redactions.For(Settings, s), Settings.HiddenPartStyle);
        if (_split.Count > 0) effects.Split(SplitLayoutNow, _split);
    }

    /// <summary>The screens shown beside the main picture (for the never-show guard).</summary>
    private IEnumerable<Rectangle> SplitScreenAreas() => _split.Select(ShownScreenArea).OfType<Rectangle>();

    /// <summary>"--split TEXT": the open window whose app or title contains the text, beside what's shown.</summary>
    private async Task<string?> SplitFromCommandLineAsync(string query)
    {
        if (CommandLine.FindWindow(WindowEnumerator.GetPresentableWindows(), query) is not { } window) return $"No open window matches “{query}”.";
        if (SplitProblem(window) is { } problem) return problem;
        await AddSplitAsync(window);
        return _split.Any(s => s.IsSameSource(window)) ? null : LastCommandMessage("It couldn't be shown beside it.");
    }
}
