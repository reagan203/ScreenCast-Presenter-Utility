using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Recording;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// "Save the last minute" (FR-106): while it's on and something is shown full screen, the last
/// minute or so of what the audience sees is kept in short parts in a temporary folder (the oldest
/// deleted as new ones come), and one key joins them into a video in the recordings folder. It
/// records the TV's picture, so everything hidden there is hidden in the replay too. Off unless
/// switched on.
/// </summary>
internal sealed partial class ScpApp
{
    private RecordingController? _replay;
    private string? _replayFolder;
    private DisplayMonitor? _replayScreen;

    /// <summary>Where the replay buffer keeps its parts while it runs (deleted when it stops).</summary>
    private static string ReplayRoot => Path.Combine(Path.GetTempPath(), "ScreenCastPresenter Replay");

    public bool ReplayRunning => _replay?.IsActive == true;

    private RecordingController Replay
    {
        get
        {
            if (_replay is not null) return _replay;
            _replay = new RecordingController(() => new RecordingHelperProcess());
            _replay.ReplaySaved += (_, saved) => Guard(() => OnReplaySaved(saved.Path, saved.Length));
            _replay.ReplayFailed += (_, why) => Guard(() => Message("The last minute wasn't saved: " + why, BadgeDot.Warning));
            _replay.Finished += (_, _) => Guard(OnReplayStopped);
            return _replay;
        }
    }

    /// <summary>Settings › Recording, the tray and "--on replay": keep the last minute of what's shown.</summary>
    public void SetReplay(bool on)
    {
        if (on && Blocked(_policy.ReplayAllowed, "keeping the last minute")) return;
        Settings.ReplayOn = on;
        Save();
        UpdateReplay();
        _tray.Refresh();
        Message(on
            ? $"The last {Slideshow.Words(Settings.ReplaySeconds)} of what's shown full screen is kept on this PC, so you can save it when something goes well{OrPress("save-replay")}."
            : "The last minute is no longer kept.", BadgeDot.Accent);
    }

    /// <summary>Settings › Recording: how much is kept.</summary>
    public void SetReplaySeconds(int seconds)
    {
        Settings.ReplaySeconds = ReplayBuffer.Normalize(seconds);
        Save();
        if (ReplayRunning) Replay.Stop(); // started again with the new length when it has stopped
    }

    /// <summary>Starts, moves or stops the buffer to match what's shown.</summary>
    private void UpdateReplay()
    {
        bool want = !_quitting && Settings.ReplayOn && _session.State == SessionState.Presenting && !_session.OutputIsFloating
            && _session.OutputMonitor is not null && _session.OutputHandle != 0;
        if (!want)
        {
            if (ReplayRunning) Replay.Stop();
            return;
        }
        DisplayMonitor screen = _session.OutputMonitor!;
        if (ReplayRunning)
        {
            if (_replayScreen is { } now && !now.IsSameScreen(screen)) Replay.Stop(); // moved: started again on the new screen when stopped
            return;
        }
        if (Replay.Status != RecordingStatus.Off) return; // still stopping
        _replayFolder = Path.Combine(ReplayRoot, Guid.NewGuid().ToString("N"));
        _replayScreen = screen;
        Rectangle bounds = DwmHelpers.GetVisibleBounds((Windows.Win32.Foundation.HWND)_session.OutputHandle);
        CropRegion? crop = bounds.IsEmpty ? null : CropRegion.FromScreen(Rectangle.Intersect(bounds, screen.Bounds), screen.Bounds);
        var request = new RecordingRequest(SourceWindow.ForScreen(screen) with { Crop = crop is { IsFull: false } ? crop : null },
            Cursor: false, Settings.MeetingSound, Microphone: false, _replayFolder, ReplaySeconds: Settings.ReplaySeconds);
        if (Replay.Start(request, "the last minute", followsOutput: true) is { } problem)
        {
            Diag("replay: " + problem);
            Message("The last minute can't be kept: " + problem, BadgeDot.Warning);
        }
        else Diag($"replay: keeping {Settings.ReplaySeconds} s of {screen.Name}");
    }

    /// <summary>"save-replay": the last minute into the recordings folder.</summary>
    private void SaveReplay()
    {
        if (Blocked(_policy.ReplayAllowed && _policy.RecordingAllowed, "saving the last minute")) return;
        if (!ReplayRunning)
        {
            Message(Settings.ReplayOn
                ? "Nothing is being kept yet: the last minute is kept while something is shown full screen."
                : "Turn on “Keep the last minute” first (Settings › Recording or the tray), then it can be saved.");
            return;
        }
        string folder = Settings.RecordingFolder ?? SavedFiles.DefaultRecordingFolder;
        string path = SavedFiles.NextPath(folder, "Replay", DateTime.Now, ".mp4", File.Exists);
        Replay.SaveReplay(path);
        Message("Saving the last minute…", BadgeDot.Accent);
    }

    private void OnReplaySaved(string path, TimeSpan length)
    {
        Diag($"replay: saved {length} to {path}");
        _badge.ShowActions("The last minute is saved", $"{Path.GetFileName(path)} · {RecordingClockText(length)}",
        [
            new BadgeButton("Open", true, () => ShellOpen(path)),
            new BadgeButton("Show in folder", false, () => ShellOpen("explorer.exe", $"/select,\"{path}\"")),
        ]);
    }

    /// <summary>The buffer stopped: its parts go, and it starts again if it's still wanted (another screen, a new length).</summary>
    private void OnReplayStopped()
    {
        DeleteReplayFolder(_replayFolder);
        _replayFolder = null;
        _replayScreen = null;
        if (!_quitting) BeginInvokeOnUi(() => Guard(UpdateReplay));
    }

    private static void DeleteReplayFolder(string? folder)
    {
        if (folder is null) return;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag("replay: " + ex.Message); // tidied at the next start
        }
    }

    /// <summary>At start: parts left by a buffer that didn't stop cleanly (a crash, a power cut) are deleted.</summary>
    private static void TidyReplayParts()
    {
        try
        {
            if (Directory.Exists(ReplayRoot)) Directory.Delete(ReplayRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag("replay: " + ex.Message);
        }
    }
}
