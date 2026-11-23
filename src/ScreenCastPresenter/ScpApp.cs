using System.Diagnostics;
using Microsoft.Win32;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Recording;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// Composition root: owns the session, shortcuts, tray, badge, outline and settings, and turns
/// every user action (shortcut, palette, tray, picker) into the same code path (<see cref="RunAsync"/>).
/// </summary>
internal sealed class ScpApp : IDisposable
{
    private static readonly double[] DimSteps = [0, 0.1, 0.2, 0.3, 0.4];

    private readonly SettingsStore _store;
    private readonly SessionController _session;
    private readonly HotkeyManager _hotkeys;
    private readonly CaptureGuard _guard;
    private readonly PresentingBadge _badge;
    private readonly SourceOutline _outline;
    private readonly TrayIcon _tray;
    private readonly RecordingController _recording;
    private readonly StartupRegistration _startup = new(Registry.CurrentUser, Environment.ProcessPath ?? "ScreenCastPresenter.exe");
    private readonly System.Windows.Forms.Timer _trimTimer = new() { Interval = 3000 };
    private List<DisplayMonitor> _monitors;
    private PickerOverlay? _picker;
    private CommandPalette? _palette;
    private ShortcutsForm? _shortcutsForm;
    private SettingsForm? _settingsForm;
    private NowShowingWidget? _widget;
    private FirstRunForm? _firstRun;
    private bool _shortcutsSuspended;
    private bool _duplicateWarned;
    private string? _lastSaveProblem;
    private bool _quitting;

    public ScpApp(SettingsStore store)
    {
        _store = store;
        _guard = new CaptureGuard();
        _monitors = DisplayHelpers.GetMonitors();
        _session = new SessionController(new Win32SessionPlatform(() => store.Current)) { BlankMessage = store.Current.BlankMessage };
        _hotkeys = new HotkeyManager();
        _badge = new PresentingBadge(PresenterScreen);
        _outline = new SourceOutline();
        _recording = new RecordingController(() => new RecordingHelperProcess());
        _tray = new TrayIcon(this);

        _session.StateChanged += (_, _) => OnSessionChanged(reveal: true);
        _session.SourceGeometryChanged += (_, _) => OnSessionChanged(reveal: false);
        _session.Message += (_, message) =>
        {
            Diag("session: " + message);
            _badge.ShowMessage(message);
        };
        _recording.Changed += (_, _) => OnRecordingChanged();
        _recording.Warning += (_, text) => Message(text, BadgeDot.Warning);
        _recording.Finished += (_, result) => OnRecordingFinished(result);
        _hotkeys.Pressed += (_, id) =>
        {
            Diag("shortcut: " + id);
            _ = RunAsync(id);
        };
        Theme.Changed += OnThemeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            TrimMemory();
        };
    }

    /// <summary>
    /// Memory budgets (NFR-01/02/09): a few seconds after things go quiet, hand freed memory back
    /// to Windows. The workstation GC otherwise keeps several MB of garbage around until its next
    /// collection, which in a mostly idle tray app may be a long time. Takes a few milliseconds.
    /// </summary>
    private void ScheduleTrim()
    {
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private static void TrimMemory()
    {
        long before = Environment.WorkingSet;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // Opt-in diagnostics for the stress harness: managed vs whole-process memory after each trim.
        if (Environment.GetEnvironmentVariable("SCP_DIAG_LOG") is { Length: > 0 } log)
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            string line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff} private={process.PrivateMemorySize64 / 1048576.0:F1}MB managedHeap={info.HeapSizeBytes / 1048576.0:F1}MB committed={info.TotalCommittedBytes / 1048576.0:F1}MB workingSetBefore={before / 1048576.0:F1}MB\n");
            try { File.AppendAllText(log, line); } catch (IOException) { }
        }
    }

    public event EventHandler? QuitRequested;

    public AppSettings Settings => _store.Current;
    public SessionController Session => _session;
    public RecordingController Recording => _recording;
    public IReadOnlyList<DisplayMonitor> Monitors => _monitors;
    public IReadOnlyList<ShortcutBinding> Shortcuts => _hotkeys.Bindings;
    public bool ShortcutsPaused { get; private set; }

    /// <summary>Windows "Duplicate" mode (checked at start and on display changes): the TV shows everything.</summary>
    public bool IsDisplayDuplicated { get; private set; }

    /// <summary>The screen the output goes to next time: the saved one if connected, else the TV.</summary>
    public DisplayMonitor? ChosenOutput => _session.OutputMonitor is { } current
        ? _monitors.FirstOrDefault(m => m.IsSameScreen(current)) ?? current
        : OutputChooser.Choose(_monitors, Settings.OutputDevice);

    /// <summary>
    /// Where the output goes next (FR-10–FR-12): the floating window where it was last (or a
    /// default corner of the presenter's screen), or fullscreen on the chosen screen.
    /// </summary>
    public OutputPlacement? ChosenPlacement
    {
        get
        {
            if (Settings.OutputInWindow)
            {
                Rectangle? saved = _session.FloatingBounds ?? Settings.FloatingWindow?.ToRectangle();
                Rectangle? restored = OutputPlacement.Restore(saved, _monitors);
                DisplayMonitor? host = restored is { } r
                    ? _monitors.FirstOrDefault(m => m.WorkArea.Contains(new Point(r.Left + r.Width / 2, r.Top + 12)))
                    : PresenterScreen();
                if (host is null) return null;
                return new OutputPlacement(host, false, restored ?? OutputPlacement.DefaultWindow(host));
            }
            return ChosenOutput is { } m ? new OutputPlacement(m, _monitors.Count > 1) : null;
        }
    }

    /// <summary>The current choice in the output selector (a screen, or the window).</summary>
    public OutputOption CurrentOutputOption =>
        (_session.State != SessionState.Off ? _session.OutputIsFloating : Settings.OutputInWindow) ? OutputOption.Window : new OutputOption(ChosenOutput);

    /// <param name="startedByWindows">Started at sign-in (FR-42): stay quiet unless something needs attention.</param>
    public void Start(bool startedByWindows = false)
    {
        _ = _session.SetDimAsync(Settings.Dim);
        ApplyShortcuts(reportProblems: true);
        if (_store.LoadProblem is { } problem) _badge.ShowMessage(problem, BadgeDot.Warning);
        CheckDuplicateDisplay();
        PreparePickerSoon();
        TryRegistry(() => _startup.RepairPath()); // portable exe moved since "start with Windows" was set
        if (!Settings.FirstRunDone) OpenFirstRun();
        else if (!startedByWindows) Message($"ScreenCast Presenter is running. Press {ActiveShortcut("picker")?.ToString() ?? "the tray icon"} to choose what to show.");
    }

    // ───────────────────────────── start with Windows (FR-42) ─────────────────────────────

    public bool StartWithWindows => TryRegistry(() => _startup.IsEnabled);

    /// <summary>Registered, but switched off in Task Manager › Startup apps.</summary>
    public bool StartupDisabledInTaskManager => TryRegistry(() => _startup.DisabledInTaskManager);

    public void SetStartWithWindows(bool on)
    {
        bool done = TryRegistry(() =>
        {
            if (on) _startup.Enable();
            else _startup.Disable();
            return true;
        });
        if (!done) Message("Windows didn't allow SCP to change its startup setting.", BadgeDot.Warning);
        else if (on && _startup.DisabledInTaskManager)
            Message("SCP is switched off in Task Manager › Startup apps. Switch it on there too.", BadgeDot.Warning);
    }

    private static bool TryRegistry(Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    // ───────────────────────────── sleep, lock, remote desktop (SRS 4.4) ─────────────────────────────

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        Diag("power: " + e.Mode);
        if (e.Mode == PowerModes.Resume) RecoverAfterPause("resume");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Diag("session: " + e.Reason);
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            RecoverAfterPause(e.Reason.ToString());
    }

    /// <summary>
    /// The PC woke up, was unlocked, or a remote-desktop session switched: screens may have
    /// changed, a TV may have been unplugged while asleep, and shortcuts may have been grabbed by
    /// another app. Re-check everything, a moment later so Windows has settled.
    /// </summary>
    private void RecoverAfterPause(string why)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            Guard(() =>
            {
                Diag("recover: " + why);
                OnDisplaySettingsChanged(this, EventArgs.Empty);
                ApplyShortcuts(reportProblems: true);
                _session.Refresh();
                UpdateOutline();
            });
        };
        timer.Start();
    }

    private void OpenFirstRun()
    {
        if (_firstRun is { IsDisposed: false })
        {
            _firstRun.Activate();
            return;
        }
        _firstRun = new FirstRunForm(this);
        _firstRun.FormClosed += (_, _) =>
        {
            _firstRun = null;
            ScheduleTrim();
        };
        _firstRun.Show();
        _firstRun.Activate();
    }

    public void CompleteFirstRun()
    {
        Settings.FirstRunDone = true;
        Save();
        Message($"All set. Press {ActiveShortcut("picker")?.ToString() ?? "the tray icon"} whenever you want to show something.");
    }

    // ───────────────────────────── actions ─────────────────────────────

    /// <summary>Runs one action by id (FR-21, FR-25). Never throws.</summary>
    public async Task RunAsync(string id)
    {
        try
        {
            if (ShortcutCatalog.Find(id) is { Available: false } later)
            {
                Message($"“{later.Name}” arrives in a later update.");
                return;
            }

            switch (id)
            {
                case "picker": OpenPicker(); break;
                case "palette": OpenPalette(); break;
                case "present-active": await PresentActiveWindowAsync(); break;
                case "previous-source": await PresentPreviousAsync(); break;
                case "next-window": await CycleAsync(+1); break;
                case "previous-window": await CycleAsync(-1); break;
                case "stop": await _session.StopAsync(); break;
                case "blank": await _session.ToggleBlankAsync(withMessage: false); break;
                case "blank-message": await _session.ToggleBlankAsync(withMessage: true); break;
                case "dim": CycleDim(); break;
                case "move-output": await MoveOutputToNextScreenAsync(); break;
                case "switch-output": await SwitchOutputAsync(); break;
                case "toggle-outline": SetShowOutline(!Settings.ShowOutline); break;
                case "toggle-badge": SetShowBadge(!Settings.ShowBadge); break;
                case "toggle-widget": SetShowWidget(!Settings.ShowWidget); break;
                case "cheat-sheet": OpenShortcuts(); break;
                case ShortcutCatalog.PauseShortcutsId: TogglePauseShortcuts(); break;
                case "toggle-theme": SetTheme(Theme.Opposite); break;
                case "freeze": await _session.ToggleFreezeAsync(); break;
                case "crop": await CropCurrentAsync(); break;
                case "remove-crop": await RemoveCropAsync(); break;
                case "screenshot": await TakeScreenshotAsync(); break;
                case "record": ToggleRecording(); break;
                case "pause-recording": TogglePauseRecording(); break;
                default:
                    if (TrySlot(id, "save-favourite-", out int save)) SaveFavourite(save);
                    else if (TrySlot(id, "favourite-", out int show)) await ShowFavouriteAsync(show);
                    break;
            }
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    public async Task PresentAsync(SourceWindow source)
    {
        OutputPlacement? placement = ChosenPlacement;
        if (placement is null)
        {
            Message("SCP can't find a screen to show on.");
            return;
        }
        DisplayMonitor output = _session.OutputIsFloating && _session.OutputMonitor is { } at ? at : placement.Monitor;
        if (source.Screen is { } screen)
        {
            // FR-14: a screen that holds the output would show itself, endlessly.
            if (screen.IsSameScreen(output))
            {
                Message(placement.IsFloating
                    ? $"The output window is on Display {screen.Number}, so that screen can't be shown in it. Move the window to another screen, or pick another screen."
                    : $"Display {screen.Number} is where the output is, so it can't be shown on itself. Pick another screen or move the output.");
                return;
            }
            if (!_monitors.Any(m => m.IsSameScreen(screen)))
            {
                Message("That screen isn't connected any more.");
                return;
            }
        }
        else if (!WindowEnumerator.IsAlive(source.Handle))
        {
            Message($"“{source.Title}” has closed.");
            return;
        }

        // FR-04: apply the crop last used for this app.
        bool savedCrop = false;
        if (!source.IsScreen && source.Crop is null && Settings.SavedCrops.TryGetValue(source.AppName, out CropRegion? crop))
        {
            source = source with { Crop = crop };
            savedCrop = true;
        }

        CheckDuplicateDisplay();
        bool onlyScreen = _monitors.Count < 2 && !placement.IsFloating;
        bool wasOff = _session.State == SessionState.Off;
        await _session.PresentAsync(source, placement);
        if (_session.State != SessionState.Presenting) return;
        if (savedCrop)
            Message($"Cropped like last time. {ActiveShortcut("remove-crop")?.ToString() ?? "Remove the crop"} shows all of it.");
        if (onlyScreen)
            Message($"Only one screen is connected, so the output covers it. {StopHint()} {ActiveShortcut("switch-output")?.ToString() ?? "The tray menu"} shows it in a window instead.");
        if (wasOff) GuideZoomSharing();
    }

    // ───────────────────────────── M3: crop, screenshots ─────────────────────────────

    /// <summary>
    /// UC-04: drag a rectangle over the presented window (or screen) to show only that part. The
    /// crop is saved for the app and used next time.
    /// </summary>
    private async Task CropCurrentAsync()
    {
        if (_session.Source is not { } source)
        {
            Message("Show a window first, then crop it.");
            return;
        }
        await CropAsync(source, present: false);
    }

    /// <summary>Lets the user crop <paramref name="source"/>; presents it with the crop if asked (picker "Crop…").</summary>
    public async Task CropAsync(SourceWindow source, bool present)
    {
        if (await ChoosePartAsync(source, "Drag to choose what to show") is not { } choice) return;
        CropRegion? crop = choice.Crop;
        if (!source.IsScreen)
        {
            if (crop is null) Settings.SavedCrops.Remove(source.AppName);
            else Settings.SavedCrops[source.AppName] = crop;
            Save();
        }

        if (present) await PresentAsync(source with { Crop = crop });
        else await _session.SetCropAsync(crop);
        if (crop is not null) Message($"Showing part of “{source.Title}”. {ActiveShortcut("remove-crop")?.ToString() ?? "Remove the crop"} shows all of it.");
    }

    private sealed record PartChoice(CropRegion? Crop);

    /// <summary>
    /// Lets the user drag a part of a window or screen. Null when cancelled or impossible (the
    /// reason is shown); a null crop inside means "all of it".
    /// </summary>
    private async Task<PartChoice?> ChoosePartAsync(SourceWindow source, string hint)
    {
        Rectangle client;
        if (source.Screen is { } screen) client = screen.Bounds;
        else
        {
            SourceWindow? alive = WindowEnumerator.Refresh(source);
            if (alive is null)
            {
                Message($"“{source.Title}” has closed.");
                return null;
            }
            if (alive.IsMinimized)
            {
                Message($"Restore “{source.Title}” first, then choose the part you want.");
                return null;
            }
            client = WindowEnumerator.ClientBoundsOnScreen(source.Handle);
        }

        // Only on the presenter's screens: never draw the selection on the TV (SRS 4.5).
        DisplayMonitor? output = _session.State != SessionState.Off ? _session.FullScreenOutput : ChosenPlacement is { IsFloating: false } p ? p.Monitor : null;
        DisplayMonitor? host = _monitors
            .Where(m => _monitors.Count < 2 || output is null || !m.IsSameScreen(output))
            .OrderByDescending(m => Area(Rectangle.Intersect(m.Bounds, client)))
            .FirstOrDefault();
        Rectangle area = host is null ? Rectangle.Empty : Rectangle.Intersect(host.Bounds, client);
        if (host is null || Area(area) < Area(client) / 3)
        {
            Message("Move the window onto your own screen first, then choose the part you want.");
            return null;
        }

        Rectangle? initial = source.Crop?.ToScreen(client);
        Rectangle? chosen = await RegionSelector.SelectAsync(area, initial, hint, host.Scale);
        if (chosen is not { } selection) return null;

        CropRegion? crop = CropRegion.FromScreen(selection, client);
        return new PartChoice(crop is { IsFull: true } ? null : crop);
    }

    private static long Area(Rectangle r) => r.Width <= 0 || r.Height <= 0 ? 0 : (long)r.Width * r.Height;

    private async Task RemoveCropAsync()
    {
        if (_session.Source is not { } source)
        {
            Message("Nothing is being shown right now.");
            return;
        }
        if (!source.IsScreen && Settings.SavedCrops.Remove(source.AppName)) Save();
        if (source.Crop is null)
        {
            Message("It isn't cropped.");
            return;
        }
        await _session.SetCropAsync(null);
        Message($"Showing all of “{source.Title}”.");
    }

    /// <summary>
    /// FR-50/51: screenshot of what's being presented, or — when nothing is — of an area you drag
    /// on your screen. Saved to Pictures\Screenshots and copied to the clipboard.
    /// </summary>
    private async Task TakeScreenshotAsync()
    {
        SourceWindow? source = _session.State == SessionState.Presenting ? _session.Source : null;
        if (source is null)
        {
            if (PresenterScreen() is not { } screen) return;
            Rectangle? chosen = await RegionSelector.SelectAsync(screen.Bounds, null, "Drag an area, or press Enter for the whole screen", screen.Scale);
            if (chosen is not { } selection) return;
            source = SourceWindow.ForScreen(screen) with { Crop = CropRegion.FromScreen(selection, screen.Bounds) is { IsFull: false } c ? c : null };
            await Task.Delay(150); // let the selection overlay disappear (it's excluded from capture anyway)
        }

        string folder = Settings.ScreenshotFolder ?? SavedFiles.DefaultScreenshotFolder;
        string path;
        try
        {
            Directory.CreateDirectory(folder);
            path = SavedFiles.NextPath(folder, "Screenshot", DateTime.Now, ".png", File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message("Screenshot not saved: " + SavedFiles.Explain(ex), BadgeDot.Warning);
            return;
        }
        if (SavedFiles.FreeSpace(folder) is < 50L * 1024 * 1024)
        {
            Message("Screenshot not saved: the disk is almost full.", BadgeDot.Warning);
            return;
        }

        // The capture runs in the helper process, so the GPU driver never loads into SCP itself.
        bool saved;
        try
        {
            saved = await CaptureHost.CaptureStillToFileAsync(source, Settings.ShowCursor && source.IsScreen, path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine(ex);
            saved = false;
        }
        if (!saved)
        {
            Message(source.IsMinimized ? $"“{source.Title}” is minimised, so there's nothing to capture." : "Couldn't take a screenshot. Try again.", BadgeDot.Warning);
            return;
        }

        // FR-51: also on the clipboard. This is the one place a picture is held in SCP's memory
        // (NFR-09d), and only for the moment it takes to copy it.
        bool copied;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Image image = Image.FromStream(file);
            copied = TryCopyToClipboard(image);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            copied = false;
        }
        _badge.ShowActions(copied ? "Screenshot saved and copied" : "Screenshot saved", Path.GetFileName(path),
        [
            new BadgeButton("Open", true, () => ShellOpen(path)),
            new BadgeButton("Show in folder", false, () => ShellOpen("explorer.exe", $"/select,\"{path}\"")),
        ]);
        ScheduleTrim();
    }

    // ───────────────────────────── recording (FR-52–FR-61) ─────────────────────────────

    /// <summary>The record shortcut: stops a running recording; otherwise records what the TV shows, or asks what to record.</summary>
    private void ToggleRecording()
    {
        if (_recording.IsActive)
        {
            _recording.Stop();
            return;
        }
        if (_session.State == SessionState.Presenting) RecordOutput();
        else OpenRecordPicker();
    }

    private void TogglePauseRecording()
    {
        if (!_recording.IsActive)
        {
            Message("Nothing is being recorded.");
            return;
        }
        _recording.TogglePause();
    }

    /// <summary>
    /// FR-52 default / FR-55: records the output's screen, cropped to the output, so the file shows
    /// exactly what the audience sees through switches, blank and freeze. (Capturing the output
    /// window itself would miss the DWM thumbnail it displays: window capture leaves thumbnails out.)
    /// SCP's own windows are excluded from capture, so they never appear (FR-56). The recording
    /// stops when presenting stops or the output moves to another screen (see OnSessionChanged).
    /// </summary>
    public void RecordOutput()
    {
        if (_session.State == SessionState.Off || _session.OutputMonitor is not { } monitor || _session.OutputHandle == 0)
        {
            Message("Nothing is being shown. Pick what to record instead.");
            OpenRecordPicker();
            return;
        }
        if (_session.OutputIsFloating)
        {
            // Recording the window's spot on screen would also record whatever covers it, so
            // record the shown window itself (it won't follow switches).
            if (_session.Source is { } shown)
            {
                RecordSource(shown);
                Message("Recording what the output window shows. Switching what's shown won't change the recording.", BadgeDot.Recording);
            }
            return;
        }
        Rectangle outputBounds = DwmHelpers.GetVisibleBounds((Windows.Win32.Foundation.HWND)_session.OutputHandle);
        CropRegion? crop = outputBounds.IsEmpty ? null : CropRegion.FromScreen(Rectangle.Intersect(outputBounds, monitor.Bounds), monitor.Bounds);
        var source = SourceWindow.ForScreen(monitor) with { Crop = crop is { IsFull: false } ? crop : null };
        _recordedOutput = monitor;
        StartRecording(source, $"What {OutputName()} shows", followsOutput: true);
    }

    /// <summary>The output screen a "what the TV shows" recording captures; null otherwise.</summary>
    private DisplayMonitor? _recordedOutput;
    private string? _recordingEndNote;

    /// <summary>A "what the TV shows" recording ends when the presentation ends or moves screen.</summary>
    private void StopOutputRecordingIfOrphaned()
    {
        if (!_recording.IsActive || !_recording.FollowsOutput || _recordedOutput is not { } recorded) return;
        if (_recording.Status == RecordingStatus.Stopping) return;
        if (_session.State == SessionState.Off)
            _recordingEndNote = "Presentation ended, recording saved";
        else if (_session.OutputIsFloating || _session.FullScreenOutput is { } now && !now.IsSameScreen(recorded))
            _recordingEndNote = "The output moved screen, recording saved";
        else return;
        _recording.Stop();
    }

    /// <summary>Records a window or screen chosen in the picker (FR-57: carries on while you work).</summary>
    public void RecordSource(SourceWindow source)
    {
        if (!source.IsScreen && source.Handle == _session.OutputHandle)
        {
            RecordOutput();
            return;
        }
        string description = source.Screen is { } screen ? $"Display {screen.Number}" : Shorten(source.Title, 40);
        StartRecording(source, description, followsOutput: false);
        if (!source.IsScreen && source.IsMinimized)
            Message($"“{Shorten(source.Title, 40)}” is minimised, so only its sound is recorded until you restore it. Move it aside instead of minimising.", BadgeDot.Warning);
    }

    /// <summary>Records part of a window or screen (picker "Part of it…").</summary>
    public async Task RecordPartAsync(SourceWindow source)
    {
        if (await ChoosePartAsync(source, "Drag the part to record") is not { } choice) return;
        await Task.Delay(150); // let the selection overlay go (it's excluded from capture anyway)
        RecordSource(source with { Crop = choice.Crop });
    }

    private void StartRecording(SourceWindow source, string description, bool followsOutput)
    {
        string folder = Settings.RecordingFolder ?? SavedFiles.DefaultRecordingFolder;
        string path = SavedFiles.NextPath(folder, "Screen Recording", DateTime.Now, ".mp4", File.Exists);
        var request = new RecordingRequest(source, Settings.ShowCursor, Settings.MeetingSound, Settings.RecordMicrophone, path);
        if (_recording.Start(request, description, followsOutput) is { } problem)
        {
            Message("Recording didn't start: " + problem, BadgeDot.Warning);
            return;
        }
        Diag($"recording: start {description} sound={Settings.MeetingSound} mic={Settings.RecordMicrophone}");
        UpdateBadge(reveal: true);
    }

    public void SetRecordSound(MeetingSound sound)
    {
        Settings.MeetingSound = sound;
        Save();
        if (_recording.IsActive) Message("The sound choice applies to your next recording.");
    }

    public void SetRecordMicrophone(bool on)
    {
        Settings.RecordMicrophone = on;
        Save();
        if (_recording.IsActive) Message("The microphone choice applies to your next recording.");
    }

    public void OpenRecordingsFolder()
    {
        string folder = Settings.RecordingFolder ?? SavedFiles.DefaultRecordingFolder;
        try
        {
            Directory.CreateDirectory(folder);
            ShellOpen(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message(SavedFiles.Explain(ex), BadgeDot.Warning);
        }
    }

    /// <summary>FR-59: stop by itself after this much recording (null = only when stopped or the window closes).</summary>
    public TimeSpan? RecordingStopAfter { get; private set; }

    public void SetRecordingStopAfter(TimeSpan? limit)
    {
        RecordingStopAfter = limit;
        if (limit is { } l && _recording.IsActive)
            Message($"The recording stops by itself after {DurationWords(l)}.", BadgeDot.Recording);
        StopRecordingIfDue();
    }

    private void StopRecordingIfDue()
    {
        if (RecordingStopAfter is not { } limit || !_recording.IsActive || _recording.Status == RecordingStatus.Stopping) return;
        if (_recording.Elapsed < limit) return;
        _recordingEndNote = $"Stopped after {DurationWords(limit)} as planned, recording saved";
        _recording.Stop();
    }

    private void OnRecordingChanged()
    {
        StopRecordingIfDue();
        UpdateWidget();
        UpdateBadge(reveal: false);
        _tray.Refresh();
    }

    private void OnRecordingFinished(RecordingResult result)
    {
        Diag($"recording: finished {result.End} {result.Length} {result.Bytes} bytes saved={result.Saved} {result.Detail}");
        UpdateBadge(reveal: false);
        _tray.Refresh();
        if (!result.Saved)
        {
            _recordingEndNote = null;
            _recordedOutput = null;
            RecordingStopAfter = null;
            Message(result.Detail.Length > 0 ? "Recording not saved: " + result.Detail : "The recording didn't start.", BadgeDot.Warning);
            ScheduleTrim();
            return;
        }

        string? note = _recordingEndNote;
        _recordingEndNote = null;
        _recordedOutput = null;
        RecordingStopAfter = null; // a time limit is for one recording
        string title = result.End switch
        {
            RecordingEnd.Stopped when note is not null => note,
            RecordingEnd.SourceClosed when _recording.FollowsOutput => "The TV was disconnected, recording saved",
            RecordingEnd.SourceClosed => "Window closed, recording saved",
            RecordingEnd.DiskFull => "Disk nearly full, recording saved",
            RecordingEnd.Error => "Recording stopped early, saved so far",
            _ => "Recording saved",
        };
        string path = result.Path;
        _badge.ShowActions(title, $"{Path.GetFileName(path)} · {RecordingClockText(result.Length)}",
        [
            new BadgeButton("Open", true, () => ShellOpen(path)),
            new BadgeButton("Show in folder", false, () => ShellOpen("explorer.exe", $"/select,\"{path}\"")),
        ]);
        ScheduleTrim();
    }

    /// <summary>"30 minutes", "1 hour", "1½ hours", "2 hours".</summary>
    public static string DurationWords(TimeSpan t)
    {
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} minutes";
        double hours = t.TotalHours;
        string number = hours % 1 == 0.5 ? $"{(int)hours}½" : ((int)Math.Round(hours)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return number == "1" ? "1 hour" : $"{number} hours";
    }

    // ───────────────────────────── now-showing bar ─────────────────────────────

    public void SetShowWidget(bool show)
    {
        Settings.ShowWidget = show;
        Save();
        UpdateWidget();
        if (!show) Message($"Now-showing bar hidden. {ActiveShortcut("toggle-widget")?.ToString() ?? "Settings"} brings it back.");
    }

    /// <summary>
    /// The small always-on-top bar (like Teams' sharing bar): visible while presenting or
    /// recording, on the presenter's screen, never on the TV.
    /// </summary>
    private void UpdateWidget()
    {
        bool want = Settings.ShowWidget && (_session.State != SessionState.Off || _recording.IsActive) && !_quitting;
        if (!want)
        {
            _widget?.Hide();
            return;
        }
        if (_widget is not { IsDisposed: false })
        {
            _widget = new NowShowingWidget();
            _widget.Clicked += (_, _) => Guard(GoToShown);
            _widget.Moved += (_, at) => Guard(() => SettleWidget(at, save: true));
        }
        bool wasVisible = _widget.Visible;
        _widget.SetState(WidgetContent());
        if (!wasVisible)
        {
            PlaceWidget();
            _widget.Show();
        }
        else if (_session.FullScreenOutput is { } tv && _monitors.Count > 1 && tv.Bounds.Contains(Centre(_widget.Bounds)))
        {
            PlaceWidget(); // the output moved onto the bar's screen: never cover the TV
        }
        UpdateBadgeClearance();
    }

    private WidgetState WidgetContent()
    {
        SessionController s = _session;
        string? recording = RecordingBadgeText();
        var menu = new List<(string, Action)>();
        nint thumb = 0;
        Rectangle? crop = null;
        string title, detail;
        BadgeDot dot;

        if (s.State == SessionState.Presenting && s.Source is { } source)
        {
            title = source.IsScreen && source.Screen is { } screen ? $"Display {screen.Number} (whole screen)" : source.Title.Length > 0 ? source.Title : source.AppName;
            if (!source.IsScreen)
            {
                thumb = source.Handle;
                if (source.Crop is { } c) crop = c.ToPixels(WindowEnumerator.ClientSize(source.Handle));
            }
            (dot, detail) = s switch
            {
                { IsBlanked: true } => (BadgeDot.Hollow, $"{OutputNameCapitalised()} is blank · audience sees black"),
                { IsFrozen: true } => (BadgeDot.Warning, ShowingText(frozen: true) + " · click to go to it"),
                { IsSourceMinimized: true } => (BadgeDot.Warning, "Minimised · click to restore it"),
                _ => (BadgeDot.Accent, ShowingText(frozen: false) + (source.Crop is null ? "" : " · part of it")),
            };
            if (!source.IsScreen) menu.Add(("Go to this window", GoToShown));
            menu.Add((s.IsBlanked ? "Show again" : "Blank the TV", () => _ = RunAsync("blank")));
            menu.Add((s.IsFrozen ? "Go live" : "Freeze the TV", () => _ = RunAsync("freeze")));
            menu.Add(("Show something else…", OpenPicker));
            menu.Add(("Stop showing", () => _ = RunAsync("stop")));
        }
        else if (s.State == SessionState.IdleScreen)
        {
            (dot, title, detail) = (BadgeDot.Hollow, "Nothing chosen", $"{OutputNameCapitalised()} shows a blank screen · click to pick");
            menu.Add(("Pick what to show…", OpenPicker));
            menu.Add(("Stop showing", () => _ = RunAsync("stop")));
        }
        else
        {
            // Only recording (e.g. a webinar in the background).
            dot = BadgeDot.Recording;
            title = _recording.Description.Length > 0 ? _recording.Description : "Recording";
            detail = "Recording · click to go to it";
            if (_recording.SourceHandle != 0) thumb = _recording.SourceHandle;
        }

        if (_recording.IsActive)
        {
            if (menu.Count > 0) menu.Add(("-", () => { }));
            if (_recording.Status is RecordingStatus.Recording or RecordingStatus.Paused)
                menu.Add((_recording.Status == RecordingStatus.Paused ? "Resume recording" : "Pause recording", _recording.TogglePause));
            menu.Add(("Stop recording", _recording.Stop));
        }
        menu.Add(("-", () => { }));
        menu.Add(("Hide this bar", () => SetShowWidget(false)));
        return new WidgetState(dot, title, detail, recording, thumb, crop, menu);
    }

    /// <summary>Clicking the bar: bring the presented (or recorded) window to the front.</summary>
    private void GoToShown()
    {
        nint target = _session.State == SessionState.Presenting && _session.Source is { IsScreen: false } source ? source.Handle
            : _session.State == SessionState.Off && _recording.SourceHandle != 0 ? _recording.SourceHandle : 0;
        if (target != 0)
        {
            if (!WindowEnumerator.BringToFront(target)) Message("That window couldn't be brought to the front.");
            return;
        }
        if (_session.State == SessionState.IdleScreen) OpenPicker();
        else if (_session.Source?.Screen is { } screen) Message($"The whole of Display {screen.Number} is being shown.");
    }

    /// <summary>The saved spot if it's still on a connected screen other than the TV, else top centre of the presenter's screen.</summary>
    private void PlaceWidget()
    {
        if (_widget is null) return;
        Point at = Settings.WidgetPosition is { } saved ? new Point(saved.X, saved.Y) : DefaultWidgetSpot();
        SettleWidget(at, save: false);
    }

    private Point DefaultWidgetSpot()
    {
        Rectangle area = PresenterScreen()?.WorkArea ?? Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Point(area.Left + (area.Width - _widget!.Width) / 2, area.Top + _widget.LogicalToDeviceUnits(6));
    }

    /// <summary>Keeps the bar fully on one screen's work area and off the full-screen output.</summary>
    private void SettleWidget(Point at, bool save)
    {
        if (_widget is null) return;
        var bounds = new Rectangle(at, _widget.Size);
        DisplayMonitor? host = _monitors.FirstOrDefault(m => m.Bounds.Contains(Centre(bounds)));
        bool onTv = host is not null && _monitors.Count > 1 && _session.FullScreenOutput is { } tv && host.IsSameScreen(tv);
        if (host is null || onTv)
        {
            if (onTv && save) Message("The bar stays on your screen, so the audience never sees it.");
            bounds.Location = DefaultWidgetSpot();
            host = PresenterScreen();
            save = save && !onTv;
        }
        if (host is not null)
        {
            Rectangle area = host.WorkArea;
            bounds.X = Math.Clamp(bounds.X, area.Left, Math.Max(area.Left, area.Right - bounds.Width));
            bounds.Y = Math.Clamp(bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height));
        }
        _widget.Location = bounds.Location;
        if (save)
        {
            Settings.WidgetPosition = WindowBounds.From(bounds);
            Save();
        }
        UpdateBadgeClearance();
    }

    /// <summary>The badge drops below the bar when the bar sits at the top of the same screen.</summary>
    private void UpdateBadgeClearance()
    {
        int clearance = 0;
        if (_widget is { Visible: true } w && PresenterScreen() is { } screen && screen.Bounds.Contains(Centre(w.Bounds))
            && w.Top - screen.WorkArea.Top < w.Height * 2)
            clearance = w.Bottom - screen.WorkArea.Top + w.LogicalToDeviceUnits(4);
        _badge.TopClearance = clearance;
    }

    private static Point Centre(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    /// <summary>"4:05" or "1:02:03".</summary>
    public static string RecordingClockText(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>The badge's red segment (FR-54): "REC 4:05", "Paused 4:05", "Starting…", "Saving…".</summary>
    private string? RecordingBadgeText() => _recording.Status switch
    {
        RecordingStatus.Starting => "Starting…",
        RecordingStatus.Recording => "REC " + RecordingClockText(_recording.Elapsed),
        RecordingStatus.Paused => "Paused " + RecordingClockText(_recording.Elapsed),
        RecordingStatus.Stopping => "Saving…",
        _ => null,
    };

    private static string Shorten(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;

    private static bool TryCopyToClipboard(Image image)
    {
        // Another app may hold the clipboard for a moment.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    private void ShellOpen(string file, string? arguments = null)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Arguments = arguments ?? "" });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message("Windows couldn't open that.", BadgeDot.Warning);
        }
    }

    /// <summary>FR-32: Automatic (DWM for windows) or always WGC (shows the pointer, keeps minimised windows' last picture).</summary>
    public void SetCaptureEngine(bool preferWgc)
    {
        Settings.CaptureEngine = preferWgc ? "WGC" : "Automatic";
        Save();
        Message(preferWgc
            ? "Windows will be captured with Windows Graphics Capture from the next one you show."
            : "Capture engine: automatic.");
    }

    public void SetShowCursor(bool show)
    {
        Settings.ShowCursor = show;
        Save();
        Message(show ? "The mouse pointer will show on the TV (screens, and windows captured with Windows Graphics Capture)." : "The mouse pointer won't show on the TV.");
    }

    private async Task PresentActiveWindowAsync()
    {
        if (WindowEnumerator.GetActiveWindow() is { } window) await PresentAsync(window);
        else Message("Click into the window you want to show, then press the shortcut again.");
    }

    private async Task PresentPreviousAsync()
    {
        if (_session.PreviousSource is { } previous && WindowEnumerator.Refresh(previous) is { } alive)
        {
            await PresentAsync(alive);
            return;
        }
        // FR-41: after a restart, the last thing shown before SCP closed.
        if (Settings.LastSource is { } last && !(_session.Source is { } now && FavouriteMatcher.FromSource(now).Matches(last)))
        {
            if (last.ScreenDevice is { } device && _monitors.FirstOrDefault(m => m.DeviceName == device) is { } screen)
            {
                await PresentAsync(SourceWindow.ForScreen(screen));
                return;
            }
            if (last.ScreenDevice is null && FavouriteMatcher.FindBest(last, WindowEnumerator.GetPresentableWindows()) is { } match)
            {
                await PresentAsync(match);
                return;
            }
        }
        Message("There's no previous window to go back to.");
    }

    /// <summary>FR-41: remember what is shown, so "previous source" can bring it back after a restart.</summary>
    private void RememberLastSource()
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { } source) return;
        Favourite last = FavouriteMatcher.FromSource(source);
        if (Settings.LastSource is { } saved && saved.Matches(last) && saved.Title == last.Title) return;
        Settings.LastSource = last;
        Save();
    }

    private async Task CycleAsync(int direction)
    {
        List<SourceWindow> windows = WindowEnumerator.GetPresentableWindows();
        if (OutputChooser.Cycle(windows, _session.Source, direction) is { } next) await PresentAsync(next);
        else Message("There are no windows to show.");
    }

    private async Task MoveOutputToNextScreenAsync()
    {
        if (_monitors.Count < 2)
        {
            Message("Only one screen is connected.");
            return;
        }
        if (OutputChooser.Next(_monitors, _session.FullScreenOutput ?? ChosenOutput) is { } next) await SetOutputMonitorAsync(next);
    }

    /// <summary>FR-21 Ctrl+Alt+O: TV → other screens (a virtual one is the Zoom screen) → floating window → …</summary>
    private async Task SwitchOutputAsync()
    {
        OutputOption next = OutputOption.Next(_monitors, CurrentOutputOption);
        if (next.Screen is { } screen) await SetOutputMonitorAsync(screen);
        else await SetOutputWindowAsync();
    }

    /// <summary>
    /// FR-12: once per run, the first time something is shown on the Zoom screen or in the window,
    /// say exactly what to pick in Zoom.
    /// </summary>
    private void GuideZoomSharing()
    {
        if (_session.OutputIsFloating && !_guidedWindow)
        {
            _guidedWindow = true;
            Message("Showing in a window. In Zoom, click Share Screen and choose “ScreenCast Presenter output”. It can sit behind other windows, but don't minimise it.");
        }
        else if (_session.FullScreenOutput is { IsVirtual: true } zoom && !_guidedZoom)
        {
            _guidedZoom = true;
            Message($"Showing on the Zoom screen. In Zoom, click Share Screen and choose Screen {zoom.Number}. Your own screen stays private.");
        }
    }

    private bool _guidedWindow;
    private bool _guidedZoom;

    /// <summary>
    /// "Show in Zoom" (FR-12): the Zoom screen if a virtual display exists, else the floating
    /// window, with a pointer to the virtual display driver for a cleaner share.
    /// </summary>
    public async Task ShowInZoomAsync()
    {
        if (_monitors.FirstOrDefault(m => m.IsVirtual) is { } zoom)
        {
            await SetOutputMonitorAsync(zoom);
            if (_session.State == SessionState.Off) Message($"The Zoom screen (Display {zoom.Number}) is the output now. Pick what to show, then share Screen {zoom.Number} in Zoom.");
            return;
        }
        await SetOutputWindowAsync();
        if (_session.State == SessionState.Off)
            Message("Output: a window. Pick what to show, then in Zoom share the window “ScreenCast Presenter output”. For sharing a whole screen instead, install a virtual display driver; it then appears here as the Zoom screen.");
    }

    private async Task ShowFavouriteAsync(int slot)
    {
        if (Settings.Favourites[slot - 1] is not { } favourite)
        {
            Message($"Favourite {slot} is empty. Show a window, then press {ActiveShortcut(ShortcutCatalog.SaveFavouriteId(slot))?.ToString() ?? "save favourite"} to save it.");
            return;
        }
        if (favourite.ScreenDevice is { } device)
        {
            if (_monitors.FirstOrDefault(m => m.DeviceName == device) is { } screen) await PresentAsync(SourceWindow.ForScreen(screen));
            else Message($"The screen saved as favourite {slot} isn't connected.");
            return;
        }
        SourceWindow? match = FavouriteMatcher.FindBest(favourite, WindowEnumerator.GetPresentableWindows());
        if (match is null) Message($"{favourite.AppName} isn't open, so favourite {slot} can't be shown.");
        else await PresentAsync(match);
    }

    private void SaveFavourite(int slot)
    {
        if (_session.Source is not { } source)
        {
            Message("Show a window first, then save it as a favourite.");
            return;
        }
        SaveFavourite(source, slot);
    }

    /// <summary>Saves any window as favourite 1–9 (from the shortcut, the tray or Ctrl+1–9 in the picker).</summary>
    public void SaveFavourite(SourceWindow source, int slot)
    {
        if (slot is < 1 or > AppSettings.SlotCount) return;
        Settings.Favourites[slot - 1] = FavouriteMatcher.FromSource(source);
        Save();
        string key = ActiveShortcut(ShortcutCatalog.ShowFavouriteId(slot))?.ToString() ?? $"favourite {slot}";
        Message($"Saved “{source.Title}” as favourite {slot}. Press {key} to show it.");
    }

    private void CycleDim()
    {
        int index = Array.FindIndex(DimSteps, d => Math.Abs(d - Settings.Dim) < 0.001);
        double next = DimSteps[(index + 1) % DimSteps.Length];
        SetDim(next);
        Message(next == 0 ? "TV dimming off." : $"TV dimmed by {next:P0}.");
    }

    // ───────────────────────────── settings ─────────────────────────────

    public void SetOutputMonitor(DisplayMonitor monitor) => _ = SetOutputMonitorAsync(monitor);

    private async Task SetOutputMonitorAsync(DisplayMonitor monitor)
    {
        Settings.OutputDevice = monitor.DeviceName;
        Settings.OutputMode = "Screen";
        Save();
        bool move = _session.State != SessionState.Off && (_session.OutputIsFloating || _session.FullScreenOutput is { } current && !current.IsSameScreen(monitor));
        if (move) await _session.MoveOutputAsync(monitor, topMost: _monitors.Count > 1);
        Message($"Output: {monitor.Label}.");
        if (move) GuideZoomSharing();
    }

    /// <summary>FR-11: show in a floating window on the presenter's screen.</summary>
    public void SetOutputWindow() => _ = SetOutputWindowAsync();

    private async Task SetOutputWindowAsync()
    {
        Settings.OutputMode = "Window";
        Save();
        if (_session.State != SessionState.Off && !_session.OutputIsFloating && ChosenPlacement is { } placement)
        {
            await _session.MoveOutputAsync(placement);
            GuideZoomSharing();
        }
        else if (_session.State == SessionState.Off)
        {
            Message("Output: a window you can move, resize and share in Zoom.");
        }
    }

    public void SetDim(double dim)
    {
        Settings.Dim = dim;
        Save();
        _ = _session.SetDimAsync(dim);
    }

    public void SetTheme(ThemeMode mode)
    {
        Settings.ThemeMode = mode;
        Save();
        Theme.Mode = mode;
    }

    public void SetShowOutline(bool show)
    {
        Settings.ShowOutline = show;
        Save();
        UpdateOutline();
        Message(show ? "Yellow outline on." : "Yellow outline off.");
    }

    public void SetShowBadge(bool show)
    {
        Settings.ShowBadge = show;
        Save();
        UpdateBadge(reveal: show);
        if (!show) Message("Presenting badge off. Messages still appear here.");
    }

    public void TogglePauseShortcuts()
    {
        ShortcutsPaused = !ShortcutsPaused;
        ApplyShortcuts(reportProblems: false);
        string resume = ActiveShortcut(ShortcutCatalog.PauseShortcutsId)?.ToString() ?? "the tray menu";
        Message(ShortcutsPaused ? $"Shortcuts paused. Press {resume} to turn them back on." : "Shortcuts are on again.");
    }

    /// <summary>Changes one shortcut; null or "" switches it off, <paramref name="reset"/> restores the default.</summary>
    public ShortcutBinding? SetShortcut(string actionId, string? hotkey, bool reset = false)
    {
        if (reset) Settings.Shortcuts.Remove(actionId);
        else Settings.Shortcuts[actionId] = hotkey ?? "";
        Save();
        ApplyShortcuts(reportProblems: false);
        return _hotkeys.Bindings.FirstOrDefault(b => b.Action.Id == actionId);
    }

    public void ResetAllShortcuts()
    {
        Settings.Shortcuts.Clear();
        Settings.ShortcutsEnabled = true;
        Save();
        ApplyShortcuts(reportProblems: true);
    }

    public void SetShortcutsEnabled(bool enabled)
    {
        Settings.ShortcutsEnabled = enabled;
        Save();
        ApplyShortcuts(reportProblems: false);
    }

    public Hotkey? ActiveShortcut(string actionId) =>
        _hotkeys.Bindings.FirstOrDefault(b => b.Action.Id == actionId && b.Status == ShortcutStatus.Active)?.Hotkey;

    private void ApplyShortcuts(bool reportProblems)
    {
        List<ShortcutBinding> resolved = ShortcutResolver.Resolve(ShortcutCatalog.All, Settings.Shortcuts,
            Settings.ShortcutsEnabled && !_shortcutsSuspended, ShortcutsPaused, SafeTypesCharacter);
        IReadOnlyList<ShortcutBinding> bindings = _hotkeys.Apply(resolved);
        if (!reportProblems) return;

        var taken = bindings.Where(b => b.Status == ShortcutStatus.Taken && b.Hotkey is not null).ToList();
        if (taken.Count > 0)
        {
            string keys = string.Join(", ", taken.Take(3).Select(b => b.Hotkey!.Value.ToString()));
            string more = taken.Count > 3 ? $" and {taken.Count - 3} more" : "";
            _badge.ShowMessage($"{keys}{more} {(taken.Count == 1 ? "is" : "are")} used by another app. Change {(taken.Count == 1 ? "it" : "them")} in Keyboard shortcuts.", BadgeDot.Warning);
        }
        int moved = bindings.Count(b => b.Note?.StartsWith("Moved", StringComparison.Ordinal) == true);
        if (moved > 0)
            _badge.ShowMessage($"Some Ctrl+Alt keys type characters on your keyboard, so {moved} shortcut{(moved == 1 ? "" : "s")} also use Shift now.");
    }

    private static bool SafeTypesCharacter(Hotkey hotkey)
    {
        try
        {
            return KeyboardLayoutProbe.TypesCharacter(hotkey);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Any setting changed (from the tray, a shortcut or the settings window).</summary>
    public event EventHandler? SettingsChanged;

    private void Save()
    {
        _session.BlankMessage = Settings.BlankMessage;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (_store.Save())
        {
            _lastSaveProblem = null;
            return;
        }
        if (_store.SaveProblem != _lastSaveProblem) _badge.ShowMessage(_store.SaveProblem ?? "Settings couldn't be saved.", BadgeDot.Warning);
        _lastSaveProblem = _store.SaveProblem;
    }

    // ───────────────────────────── windows ─────────────────────────────

    /// <summary>Opens the picker on the presenter's screen, or closes it if it's already open (the shortcut toggles).</summary>
    public void OpenPicker() => OpenPicker(PickerMode.Show);

    /// <summary>The picker in "choose what to record" mode (FR-52, FR-57).</summary>
    public void OpenRecordPicker() => OpenPicker(PickerMode.Record);

    private void OpenPicker(PickerMode mode)
    {
        if (_picker is { IsDisposed: false, Visible: true })
        {
            bool sameMode = _picker.Mode == mode;
            _picker.Dismiss();
            if (sameMode) return; // the shortcut toggles it; a different mode reopens it
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (PresenterScreen() is not { } screen) return;
        PickerOverlay picker = EnsurePicker(screen);
        picker.Open(mode);
        Diag($"picker: shown {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The picker is built once and reused (NFR-08: building it costs more than the whole open
    /// budget). It's rebuilt only when the presenter's screen changes size, position or scaling.
    /// </summary>
    private PickerOverlay EnsurePicker(DisplayMonitor screen)
    {
        if (_picker is { IsDisposed: false } existing && SameLayout(existing.Screen, screen)) return existing;
        DiscardPicker();
        _picker = new PickerOverlay(this, screen);
        _picker.Dismissed += (_, _) => ScheduleTrim();
        return _picker;
    }

    private static bool SameLayout(DisplayMonitor a, DisplayMonitor b) =>
        a.IsSameScreen(b) && a.Bounds == b.Bounds && a.WorkArea == b.WorkArea && Math.Abs(a.Scale - b.Scale) < 0.001;

    private void DiscardPicker()
    {
        if (_picker is null) return;
        if (!_picker.IsDisposed) _picker.Dispose();
        _picker = null;
    }

    /// <summary>A few seconds after start, build the picker hidden so even the first open is instant.</summary>
    private void PreparePickerSoon()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (_quitting || PresenterScreen() is not { } screen) return;
            try
            {
                EnsurePicker(screen).Prepare();
                ScheduleTrim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex); // not fatal: it will be built on first use instead
            }
        };
        timer.Start();
    }

    /// <summary>Opt-in diagnostics for the stress harness (SCP_DIAG_LOG); does nothing otherwise.</summary>
    internal static void Diag(string message)
    {
        DiagLog.Write(message);
    }

    private static void BeginInvokeOnUi(Action action) =>
        (SynchronizationContext.Current ?? new SynchronizationContext()).Post(_ => action(), null);

    /// <summary>Command palette (FR-25); the shortcut toggles it.</summary>
    public void OpenPalette()
    {
        if (_palette is { IsDisposed: false })
        {
            _palette.Close();
            return;
        }
        if (PresenterScreen() is not { } screen) return;
        _palette = new CommandPalette(this, screen);
        _palette.FormClosed += (_, _) =>
        {
            _palette = null;
            ScheduleTrim();
        };
        _palette.Show();
        _palette.Activate();
    }

    /// <summary>Keyboard shortcuts page and cheat sheet (FR-26).</summary>
    /// <summary>Settings window (SRS 6.1), optionally at a section. One at a time.</summary>
    public void OpenSettings() => OpenSettings(SettingsForm.Section.General);

    public void OpenSettings(SettingsForm.Section section)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Show(section);
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(this, section);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            ScheduleTrim();
        };
        if (PresenterScreen() is { } screen)
        {
            // On the presenter's screen, never the TV.
            _settingsForm.StartPosition = FormStartPosition.Manual;
            Rectangle area = screen.WorkArea;
            Size size = new(Math.Min((int)(860 * screen.Scale), area.Width - 40), Math.Min((int)(640 * screen.Scale), area.Height - 40));
            _settingsForm.Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
        }
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    public void SetBlankMessage(string message)
    {
        string before = Settings.BlankMessage;
        Settings.BlankMessage = message;
        Settings.Normalize(); // trims, limits the length, restores the default when empty
        if (Settings.BlankMessage != before) Save();
    }

    /// <summary>Null = the default folder (Videos\Screen Recordings).</summary>
    public void SetRecordingFolder(string? folder)
    {
        Settings.RecordingFolder = folder;
        Save();
    }

    /// <summary>Null = the default folder (Pictures\Screenshots).</summary>
    public void SetScreenshotFolder(string? folder)
    {
        Settings.ScreenshotFolder = folder;
        Save();
    }

    public string SettingsFilePath => _store.FilePath;

    public string VersionText =>
        $"ScreenCast Presenter {typeof(ScpApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} · .NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";

    /// <summary>Opens Explorer at a file (selected) or a folder, creating the folder if needed.</summary>
    public void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) ShellOpen("explorer.exe", $"/select,\"{path}\"");
            else
            {
                Directory.CreateDirectory(path);
                ShellOpen(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Message(SavedFiles.Explain(ex), BadgeDot.Warning);
        }
    }

    public void OpenShortcuts()
    {
        if (_shortcutsForm is { IsDisposed: false })
        {
            if (_shortcutsForm.WindowState == FormWindowState.Minimized) _shortcutsForm.WindowState = FormWindowState.Normal;
            _shortcutsForm.Activate();
            return;
        }
        _shortcutsForm = new ShortcutsForm(this);
        _shortcutsForm.FormClosed += (_, _) =>
        {
            _shortcutsForm = null;
            ScheduleTrim();
        };
        if (PresenterScreen() is { } screen)
        {
            _shortcutsForm.StartPosition = FormStartPosition.Manual;
            Rectangle area = screen.WorkArea;
            Size size = new((int)(860 * screen.Scale), (int)(720 * screen.Scale));
            _shortcutsForm.Bounds = new Rectangle(area.Left + Math.Max(0, (area.Width - size.Width) / 2), area.Top + Math.Max(0, (area.Height - size.Height) / 2),
                Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));
        }
        _shortcutsForm.Show();
        _shortcutsForm.Activate();
    }

    /// <summary>While the user records a new shortcut, SCP's own shortcuts must not fire.</summary>
    public void SuspendShortcuts(bool suspend)
    {
        if (_shortcutsSuspended == suspend) return;
        _shortcutsSuspended = suspend;
        ApplyShortcuts(reportProblems: false);
    }

    public void RequestQuit() => _ = QuitAsync();

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _picker?.Dismiss();
        _palette?.Close();
        _shortcutsForm?.Close();
        _settingsForm?.Close();
        _firstRun?.Close();
        await _session.StopAsync();
        if (_recording.IsActive)
        {
            // FR-55: quitting finishes the recording properly. The helper would finish it on its
            // own anyway (FR-61), but waiting lets SCP confirm it was saved.
            Message("Saving the recording…", BadgeDot.Recording);
            _recording.Stop();
            await Task.WhenAny(_recording.Completion, Task.Delay(TimeSpan.FromSeconds(15)));
        }
        QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── state → UI ─────────────────────────────

    private void OnSessionChanged(bool reveal)
    {
        StopOutputRecordingIfOrphaned();
        RememberLastSource();
        UpdateWidget();
        if (_session.FloatingBounds is { } floating && Settings.FloatingWindow?.ToRectangle() != floating)
        {
            Settings.FloatingWindow = WindowBounds.From(floating); // reopen it where the user left it
            Save();
        }
        UpdateBadge(reveal);
        UpdateOutline();
        _tray.Refresh();
        ScheduleTrim();
    }

    private void UpdateBadge(bool reveal)
    {
        SessionController s = _session;
        var blank = new BadgeButton("Blank", false, () => _ = RunAsync("blank"));
        var showAgain = new BadgeButton("Show again", true, () => _ = RunAsync("blank"));
        var stop = new BadgeButton("Stop", false, () => _ = RunAsync("stop"));
        var pick = new BadgeButton("Pick…", true, OpenPicker);

        var goLive = new BadgeButton("Go live", true, () => _ = RunAsync("freeze"));
        bool keepsPicture = s.EngineName is { } engine && engine.StartsWith("Windows Graphics", StringComparison.Ordinal);

        BadgeContent? content = s.State switch
        {
            SessionState.Presenting when s.IsBlanked => new BadgeContent(BadgeDot.Hollow, $"{OutputNameCapitalised()} is blank", "Your audience sees black", [showAgain, stop]),
            SessionState.Presenting when s.IsFrozen => new BadgeContent(BadgeDot.Warning, $"{OutputNameCapitalised()} is frozen", "You can scroll and click privately", [goLive, stop]),
            SessionState.Presenting when s.IsSourceMinimized && keepsPicture => new BadgeContent(BadgeDot.Warning, "Window minimised", $"{OutputNameCapitalised()} keeps showing its last picture", [stop]),
            SessionState.Presenting when s.IsSourceMinimized => new BadgeContent(BadgeDot.Warning, "Window minimised", $"{OutputNameCapitalised()} is black until you restore it", [stop]),
            SessionState.Presenting => new BadgeContent(BadgeDot.Accent, ShowingText(frozen: false), $"{s.Source?.Title} · {s.Source?.AppName}", [blank, stop]),
            SessionState.IdleScreen => new BadgeContent(BadgeDot.Hollow, $"{OutputNameCapitalised()} shows a blank screen", "Pick something to show", [pick, stop]),
            _ => null,
        };

        // FR-54: the recording indicator rides on the badge; a recording alone gets its own badge.
        if (RecordingBadgeText() is { } recording)
        {
            var stopRecording = new BadgeButton("Stop recording", false, () => _recording.Stop());
            var pause = new BadgeButton(_recording.Status == RecordingStatus.Paused ? "Resume" : "Pause", false, () => _recording.TogglePause());
            content = content is null
                ? new BadgeContent(BadgeDot.Recording, "Recording", _recording.Description, _recording.Status is RecordingStatus.Recording or RecordingStatus.Paused ? [pause, stopRecording] : [], recording)
                : content with { Recording = recording };
        }
        _badge.SetStatus(Settings.ShowBadge || _recording.IsActive ? content : null, reveal);
    }

    private void UpdateOutline()
    {
        SessionController s = _session;
        bool show = Settings.ShowOutline && s.State == SessionState.Presenting && !s.IsBlanked && !s.IsSourceMinimized && s.Source is not null;
        // FR-17: around exactly what the audience sees: the window, the cropped part of it, or the screen.
        Rectangle? bounds = null;
        if (show && s.Source is { } source)
        {
            if (source.Screen is { } screen) bounds = source.Crop?.ToScreen(screen.Bounds) ?? screen.Bounds;
            else if (WindowEnumerator.IsAlive(source.Handle))
                bounds = source.Crop is { } crop ? crop.ToScreen(WindowEnumerator.ClientBoundsOnScreen(source.Handle)) : DwmHelpers.GetVisibleBounds((Windows.Win32.Foundation.HWND)source.Handle);
        }
        _outline.Update(bounds, ShowingText(s.IsFrozen), _monitors, s.FullScreenOutput);
    }

    /// <summary>"TV" for a TV-like output, else "Display 2".</summary>
    private string OutputNameCapitalised() => OutputName() is { Length: > 0 } name ? char.ToUpperInvariant(name[0]) + name[1..] : "";

    /// <summary>"Showing on TV", "Showing on the Zoom screen", "Showing in the window", "Frozen on TV"…</summary>
    private string ShowingText(bool frozen)
    {
        string verb = frozen ? "Frozen" : "Showing";
        bool window = _session.State != SessionState.Off ? _session.OutputIsFloating : Settings.OutputInWindow;
        return window ? $"{verb} in the window" : $"{verb} on {OutputName()}";
    }

    private string OutputName()
    {
        if (_session.State != SessionState.Off ? _session.OutputIsFloating : Settings.OutputInWindow) return "the window";
        DisplayMonitor? m = _session.OutputMonitor ?? ChosenOutput;
        if (m is null) return "the output";
        return m.IsVirtual ? "the Zoom screen" : m.IsTv ? "TV" : $"Display {m.Number}";
    }

    private DisplayMonitor? PresenterScreen() =>
        OutputChooser.PresenterScreen(_monitors, _session.FullScreenOutput, _monitors.FirstOrDefault(m => m.Bounds.Contains(Cursor.Position))); // cached list: no display-config queries on the picker path (NFR-08)

    private string StopHint() => ActiveShortcut("stop") is { } stop ? $"Press {stop} to stop." : "Use the tray icon to stop.";

    private void Message(string text, BadgeDot dot = BadgeDot.Accent)
    {
        Diag("app: " + text);
        _badge.ShowMessage(text, dot);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _widget?.Invalidate();
        UpdateOutline();
        UpdateBadge(reveal: false);
    }

    /// <summary>TV unplugged, resolution changed, screens rearranged (SRS 4.4).</summary>
    private async void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            _monitors = DisplayHelpers.GetMonitors();
            if (_picker is { Visible: false }) DiscardPicker(); // laid out for the old screens
            if (_session.FullScreenOutput is { } output) // a floating window is kept on screen by Windows itself
            {
                DisplayMonitor? now = _monitors.FirstOrDefault(m => m.IsSameScreen(output));
                if (now is null)
                {
                    await _session.StopAsync();
                    Message("The TV was disconnected, so SCP stopped showing. Reconnect it and pick again.");
                }
                else if (now.Bounds != output.Bounds)
                {
                    await _session.MoveOutputAsync(now, topMost: _monitors.Count > 1);
                }
            }
            if (_session.Source?.Screen is { } shown && !_monitors.Any(m => m.IsSameScreen(shown)))
            {
                await _session.StopAsync();
                Message("The screen you were showing was disconnected, so SCP stopped showing it.");
            }
            CheckDuplicateDisplay();
            OnSessionChanged(reveal: false);
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    private void CheckDuplicateDisplay()
    {
        bool duplicated;
        try
        {
            duplicated = DisplayHelpers.IsAnyScreenDuplicated();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            duplicated = false;
        }
        if (duplicated && !_duplicateWarned)
            _badge.ShowMessage("Your TV is copying this screen, so the audience sees everything. Press Win+P and choose Extend.", BadgeDot.Warning);
        _duplicateWarned = duplicated;
        IsDisplayDuplicated = duplicated;
    }

    /// <summary>Runs a UI callback; any exception becomes a message instead of a crash.</summary>
    public void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    public void HandleError(Exception ex)
    {
        Debug.WriteLine(ex);
        _badge.ShowMessage("Something went wrong, but SCP is still running.", BadgeDot.Warning);
    }

    private static bool TrySlot(string id, string prefix, out int slot)
    {
        slot = 0;
        return id.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out slot)
            && slot is >= 1 and <= AppSettings.SlotCount;
    }

    /// <summary>For the privacy audit (tests, stress harness): SCP windows that could be captured.</summary>
    public static List<nint> FindCaptureLeaks() => CaptureGuard.FindLeaks();

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Theme.Changed -= OnThemeChanged;
        _trimTimer.Dispose();
        _picker?.Dispose();
        _hotkeys.Dispose();
        _session.Dispose();
        _recording.Dispose();
        _outline.Dispose();
        _widget?.Dispose();
        _badge.Dispose();
        _tray.Dispose();
        _guard.Dispose();
        _store.Save();
    }
}
