using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Recording;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// The check before sharing (FR-102), chapters for recordings and a timeline of what was shown (FR-105).
/// </summary>
internal sealed partial class ScpApp
{
    /// <summary>What "Check before showing" (the setting) has already checked this session, so each is checked once.</summary>
    private readonly HashSet<string> _checkedThisSession = new(StringComparer.Ordinal);

    /// <summary>"check-before-showing": check what's ready, or choose something to check.</summary>
    private void CheckBeforeShowing()
    {
        if (_next is { } ready) _ = GuardAsync(() => CheckThenShowAsync(ready));
        else OpenPicker(PickerMode.Check);
    }

    /// <summary>Checks <paramref name="source"/>, and shows it if the presenter says so.</summary>
    public async Task CheckThenShowAsync(SourceWindow source)
    {
        if (!await CheckAsync(source, asked: true)) return;
        if (_next is { } ready && ready.IsSameSource(source)) await GoAsync();
        else await PresentAsync(source);
    }

    /// <summary>
    /// The check: true to go ahead and show it. Asked for (<paramref name="asked"/>), the result is
    /// always shown; from the setting, only when something was found that automatic hiding wouldn't cover.
    /// </summary>
    private async Task<bool> CheckAsync(SourceWindow source, bool asked)
    {
        _checkedThisSession.Add(CaptureProtocol.CoverKey(source));
        if (source.IsOffScreen)
        {
            if (asked) Message("The check works on windows and screens: a camera's or a picture's picture isn't on your screen to read.");
            return !asked;
        }
        string preview = Path.Combine(Path.GetTempPath(), $"scp-check-{Guid.NewGuid():N}.png");
        try
        {
            if (asked) Message($"Checking “{SourceName(source)}”…", BadgeDot.Accent);
            CheckReport? report = await CaptureHost.CheckToFileAsync(source, preview, Redactions.For(Settings, source), Settings.AutoHideWords);
            if (report is null)
            {
                if (asked) Message($"“{SourceName(source)}” couldn't be checked: it may have closed or be minimised.", BadgeDot.Warning);
                return !asked; // from the setting: hidden parts and automatic hiding still apply
            }
            SensitiveKinds missing = report.Missing(Settings.AutoHideKinds);
            Diag($"check: {SourceName(source)}: {report.Words()} (not covered: {missing})");
            if (!asked && missing == SensitiveKinds.None) return true; // nothing that wouldn't be covered: no question
            CheckForm.Choice choice;
            using (var form = new CheckForm(SourceName(source), preview, report, missing))
            {
                TryDelete(preview); // read into memory: the picture of private things doesn't stay on disk
                form.ShowDialog();
                choice = form.Result;
            }
            switch (choice)
            {
                case CheckForm.Choice.HideThenShow:
                    SetAutoHide(Settings.AutoHideKinds | missing);
                    return true;
                case CheckForm.Choice.Show:
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            TryDelete(preview);
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag("check: " + ex.Message);
            }
        }
    }

    /// <summary>With "Check before showing" on: the first time something is shown this session, it's checked first. False not to show it.</summary>
    private async Task<bool> CheckFirstTimeAsync(SourceWindow source)
    {
        if (!Settings.CheckBeforeShowing || _playlistSwitching || _slideshowSwitching || source.IsOffScreen) return true;
        if (_checkedThisSession.Contains(CaptureProtocol.CoverKey(source))) return true;
        return await CheckAsync(source, asked: false);
    }

    /// <summary>Settings › Privacy: check each window or screen the first time it's shown.</summary>
    public void SetCheckBeforeShowing(bool on)
    {
        if (!on && _policy.CheckBeforeShowing)
        {
            Message("Your organisation keeps “Check before showing” on.", BadgeDot.Warning);
            return;
        }
        Settings.CheckBeforeShowing = on;
        Save();
        if (on) _checkedThisSession.Clear();
    }

    // ───────────────────────────── chapters and the timeline (FR-105) ─────────────────────────────

    private ChapterLog? _chapters;
    private TimelineWriter? _timeline;
    private string _lastShownNote = "";

    /// <summary>What the TV shows now, in a few words (for chapters and the timeline); null when nothing.</summary>
    private string? ShownNote()
    {
        if (_session.State == SessionState.Off) return null;
        if (_session.IsBlanked) return _countdownEnd is not null ? "Break" : "Blank";
        if (_session.Source is not { } s) return "Blank";
        string name = SourceName(s);
        return _split.Count > 0 ? $"{name} and {string.Join(", ", _split.Select(SourceName))}" : name;
    }

    /// <summary>Something changed: a chapter starts in the recording, and a line goes in the timeline.</summary>
    private void NoteShown()
    {
        string? note = ShownNote();
        string key = note ?? "";
        if (key == _lastShownNote) return;
        _lastShownNote = key;
        if (_chapters is not null && _recording.IsActive && note is not null) _chapters.Add(_recording.Elapsed, note);
        UpdateTimeline(note);
    }

    /// <summary>A recording started: its chapters start with what's shown.</summary>
    private void StartChapters()
    {
        _chapters = Settings.RecordingChapters ? new ChapterLog() : null;
        if (ShownNote() is { } now) _chapters?.Add(TimeSpan.Zero, now);
    }

    /// <summary>A recording was saved: its chapters go beside it.</summary>
    private void SaveChapters(RecordingResult result)
    {
        ChapterLog? chapters = _chapters;
        _chapters = null;
        if (chapters is null || !result.Saved) return;
        if (chapters.Save(result.Path, result.Length)) Diag($"chapters: {chapters.Chapters.Count} saved beside {result.Path}");
    }

    /// <summary>The timeline follows what's shown while "Save a timeline" is on: a new one each time showing starts.</summary>
    private void UpdateTimeline(string? note)
    {
        if (!Settings.SaveTimeline)
        {
            _timeline?.Dispose();
            _timeline = null;
            return;
        }
        if (note is null)
        {
            if (_timeline is null) return;
            _timeline.Add(DateTime.Now, "Stopped showing");
            _timeline.Dispose();
            _timeline = null;
            return;
        }
        if (_timeline is null)
        {
            (TimelineWriter? writer, string? problem) = TimelineWriter.Start(Settings.TranscriptFolder ?? SavedFiles.DefaultTranscriptFolder, DateTime.Now);
            if (writer is null)
            {
                Message(problem!, BadgeDot.Warning);
                return;
            }
            _timeline = writer;
        }
        _timeline.Add(DateTime.Now, note == "Blank" || note == "Break" ? note : $"Showing “{note}”");
    }

    /// <summary>Settings › Recording: chapters beside each recording.</summary>
    public void SetRecordingChapters(bool on)
    {
        Settings.RecordingChapters = on;
        Save();
    }

    /// <summary>Settings › Accessibility and records: a timeline of each presentation in the transcripts folder.</summary>
    public void SetSaveTimeline(bool on)
    {
        Settings.SaveTimeline = on;
        Save();
        _lastShownNote = "";
        NoteShown();
        if (!on) UpdateTimeline(null);
    }
}
