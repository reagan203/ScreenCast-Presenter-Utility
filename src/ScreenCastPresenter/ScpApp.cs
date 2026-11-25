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
        _session.OutputDoubleClicked += (_, _) => Guard(() => GoToShown(moveToScreen: true));
        _foreground.Changed += (_, active) => Guard(() => UpdateStepBack(active));
        _titles.TitleChanged += (_, _) => Guard(CheckBrowserPage);
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
                return new OutputPlacement(host, Settings.FloatingOnTop, restored ?? OutputPlacement.DefaultWindow(host));
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
        _ = GuardAsync(OfferNewScreenAsync); // a TV plugged in before SCP started
        if (!Settings.FirstRunDone) OpenFirstRun();
        else if (!startedByWindows)
            Message($"ScreenCast Presenter is ready. Click its icon by the clock{OrPress("picker")} to show something.", BadgeDot.Accent, Button("Choose what to show", OpenPicker));
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
        if (!done) Message("Windows didn't allow ScreenCast Presenter to change its startup setting.", BadgeDot.Warning);
        else if (on && _startup.DisabledInTaskManager)
            Message("ScreenCast Presenter is switched off in Task Manager › Startup apps. Switch it on there too.", BadgeDot.Warning);
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

    /// <summary>
    /// The first-run window's last step: remember it's done, apply its two switches (keep the icon
    /// by the clock, add to the Start menu), then either open the picker straight away ("Show
    /// something now") or say where SCP is.
    /// </summary>
    public void CompleteFirstRun(bool showNow, bool install, bool pinIcon)
    {
        Settings.FirstRunDone = true;
        Save();
        if (pinIcon) PinTrayIconSoon();
        if (install) Install();
        if (showNow) OpenPicker();
        else Message($"All set. Click the icon by the clock{OrPress("picker")} whenever you want to show something.");
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
                case "stop": await StopShowingAsync(); break;
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
        if (_session.State == SessionState.Off) await OfferNewScreenAsync(); // plugged in since: show on it, not in the window
        OutputPlacement? placement = ChosenPlacement;
        if (placement is null)
        {
            Message("No screen was found to show on. Check the TV's cable, then try again.", BadgeDot.Warning);
            return;
        }
        DisplayMonitor output = _session.OutputIsFloating && _session.OutputMonitor is { } at ? at : placement.Monitor;
        if (source.Screen is { } screen)
        {
            // FR-14: a screen that holds a full-screen output would show itself, endlessly. A
            // floating window on that screen is fine: it becomes a private preview (hidden from
            // capture), like Teams' sharing preview.
            if (screen.IsSameScreen(output) && !placement.IsFloating)
            {
                Message($"{screen.Name} is where your presentation is shown, so it can't show itself.", BadgeDot.Accent,
                    Button("Show it in a window instead", async () =>
                    {
                        await SetOutputWindowAsync();
                        await PresentAsync(source);
                    }));
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
            Message("Showing the same part as last time.", BadgeDot.Accent, Button("Show all of it", RemoveCropAsync));
        if (onlyScreen)
            Message("Only one screen is connected, so your presentation covers it.", BadgeDot.Accent,
                Button("Show in a window", SetOutputWindowAsync), Button("Stop", StopShowingAsync, primary: false));
        if (_session.IsPrivatePreview)
        {
            if (!_guidedPreview)
            {
                _guidedPreview = true;
                Message("This window is a private preview: Teams, Zoom and recordings can't see it. Share your screen in your meeting as usual.");
            }
        }
        else if (wasOff) GuideZoomSharing();
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
            Message("Show something first, then choose part of it.");
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
        if (crop is not null) Message($"Showing part of “{Shorten(source.Title, 40)}”.", BadgeDot.Accent, Button("Show all of it", RemoveCropAsync));
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
            Message("All of it is already showing.");
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
                Message($"Recording “{Shorten(shown.Title, 40)}”. Showing something else won't change this recording.", BadgeDot.Recording);
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
            _recordingEndNote = "Moved to another screen, recording saved";
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
        string description = source.Screen is { } screen ? screen.Name : Shorten(source.Title, 40);
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
        UpdateQuickPanel();
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
    public static string DurationWords(TimeSpan t) => TrayMenuPlan.DurationWords(t);

    // ───────────────────────────── now-showing bar ─────────────────────────────

    public void SetShowWidget(bool show)
    {
        Settings.ShowWidget = show;
        Save();
        UpdateWidget();
        UpdateBadge(reveal: false); // the bar carries the status while it shows; the pill takes over when it's hidden
        if (!show) Message("Now-showing bar hidden.", BadgeDot.Accent, Button("Undo", () => SetShowWidget(true)));
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
            title = source.IsScreen && source.Screen is { } screen ? $"{screen.Name} (whole screen)" : source.Title.Length > 0 ? source.Title : source.AppName;
            if (!source.IsScreen)
            {
                thumb = source.Handle;
                if (source.Crop is { } c) crop = c.ToPixels(WindowEnumerator.ClientSize(source.Handle));
            }
            (dot, detail) = s switch
            {
                { IsBlanked: true } => (BadgeDot.Hollow, $"{OutputNameCapitalised()} is blank · audience sees black"),
                { IsAwayFromPage: true } => (BadgeDot.Warning, "On another page · audience sees black until you go back"),
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
            (dot, title, detail) = (BadgeDot.Hollow, "Nothing chosen", $"{OutputNameCapitalised()} shows a blank screen · click to choose");
            menu.Add(("Choose what to show…", OpenPicker));
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

    private void GoToShown() => GoToShown(moveToScreen: false);

    /// <summary>
    /// Clicking the bar, or double-clicking the output window: bring the presented (or recorded)
    /// window to the front. For a whole screen, a double-click moves the pointer to the middle of it.
    /// </summary>
    private void GoToShown(bool moveToScreen)
    {
        nint target = _session.State == SessionState.Presenting && _session.Source is { IsScreen: false } source ? source.Handle
            : _session.State == SessionState.Off && _recording.SourceHandle != 0 ? _recording.SourceHandle : 0;
        if (target != 0)
        {
            if (!WindowEnumerator.BringToFront(target)) Message("That window couldn't be brought to the front.");
            return;
        }
        if (_session.State == SessionState.IdleScreen) OpenPicker();
        else if (_session.Source?.Screen is { } screen)
        {
            DisplayMonitor? live = _monitors.FirstOrDefault(m => m.IsSameScreen(screen));
            if (moveToScreen && live is not null && !live.Bounds.Contains(Cursor.Position)) Cursor.Position = Centre(live.Bounds);
            else Message($"The whole of {screen.Name} is being shown.");
        }
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
            ? "From the next window you show, the pointer shows and a minimised window keeps its last picture."
            : "Back to automatic, which uses the least memory.");
    }

    public void SetShowCursor(bool show)
    {
        Settings.ShowCursor = show;
        Save();
        if (!show) Message("The mouse pointer won't show on the TV.");
        else if (Settings.PreferWgc) Message("The mouse pointer will show on the TV.");
        else Message("The mouse pointer will show on the TV when you show a whole screen.", BadgeDot.Accent,
            Button("Show it on windows too", () => SetCaptureEngine(true)));
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
        if (!(Settings.LastSource is { } last && _session.Source is { } now && FavouriteMatcher.FromSource(now).Matches(last)) && await PresentLastShownAsync())
            return;
        Message("There's no previous window to go back to.");
    }

    /// <summary>
    /// The tray's "Show “…” again" after stopping: exactly the last thing shown (FR-41), which after
    /// a stop isn't the same as "previous" (the one before it). False when it's gone.
    /// </summary>
    private async Task<bool> PresentLastShownAsync()
    {
        if (Settings.LastSource is not { } last) return false;
        if (last.ScreenDevice is { } device)
        {
            if (_monitors.FirstOrDefault(m => m.DeviceName == device) is not { } screen) return false;
            await PresentAsync(SourceWindow.ForScreen(screen));
            return true;
        }
        if (FavouriteMatcher.FindBest(last, WindowEnumerator.GetPresentableWindows()) is not { } match) return false;
        await PresentAsync(match);
        return true;
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
            Message("Showing in a window. In your meeting, share the window “ScreenCast Presenter output”, and don't minimise it.", BadgeDot.Accent,
                Button("Show me how", () => OpenShareGuide(ShareGuideKind.Window)));
        }
        else if (_session.FullScreenOutput is { IsVirtual: true } zoom && !_guidedZoom)
        {
            _guidedZoom = true;
            Message("Showing on the Zoom screen. In your meeting, share the screen whose preview shows your presentation.", BadgeDot.Accent,
                Button("Show me how", () => OpenShareGuide(ShareGuideKind.ZoomScreen)));
        }
    }

    private bool _guidedWindow;
    private bool _guidedPreview;

    /// <summary>The floating window stays above every other window while presenting.</summary>
    public void SetFloatingOnTop(bool on)
    {
        Settings.FloatingOnTop = on;
        Save();
        // Re-open the window with the new setting, keeping what it shows and where it is.
        if (_session.OutputIsFloating && ChosenPlacement is { } placement) _ = _session.ReopenOutputAsync(placement);
    }
    private bool _guidedZoom;

    /// <summary>
    /// "Show in Zoom or Teams" (FR-12): the Zoom screen if a virtual display exists, else the
    /// floating window, then the three-step guide for sharing it (which also offers to set up a
    /// Zoom screen for sharing a whole screen).
    /// </summary>
    public async Task ShowInZoomAsync()
    {
        if (_monitors.FirstOrDefault(m => m.IsVirtual) is { } zoom)
        {
            await SetOutputMonitorAsync(zoom);
            OpenShareGuide(ShareGuideKind.ZoomScreen);
            return;
        }
        await SetOutputWindowAsync();
        OpenShareGuide(ShareGuideKind.Window);
    }

    private async Task ShowFavouriteAsync(int slot)
    {
        if (Settings.Favourites[slot - 1] is not { } favourite)
        {
            Message($"Favourite {slot} is empty. Show something, then save it from the tray menu under Favourites.");
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
        string how = ActiveShortcut(ShortcutCatalog.ShowFavouriteId(slot)) is { } key ? $"Press {key} to show it." : "Find it in the tray menu under Favourites.";
        Message($"Saved “{Shorten(source.Title, 40)}” as favourite {slot}. {how}");
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

    private Task SetOutputMonitorAsync(DisplayMonitor monitor) => SetOutputMonitorAsync(monitor, quiet: false);

    private async Task SetOutputMonitorAsync(DisplayMonitor monitor, bool quiet)
    {
        Settings.OutputDevice = monitor.DeviceName;
        Settings.OutputMode = "Screen";
        Save();
        bool move = _session.State != SessionState.Off && (_session.OutputIsFloating || _session.FullScreenOutput is { } current && !current.IsSameScreen(monitor));
        if (move) await _session.MoveOutputAsync(monitor, topMost: _monitors.Count > 1);
        if (!quiet) Message($"Your presentation will show on {Describe(monitor)}.");
        if (move) GuideZoomSharing();
    }

    /// <summary>FR-11: show in a floating window on the presenter's screen.</summary>
    public void SetOutputWindow() => _ = SetOutputWindowAsync();

    private async Task SetOutputWindowAsync()
    {
        Settings.OutputMode = "Window";
        Settings.WindowModeScreens = [.. _monitors.Select(m => m.DeviceName)]; // a screen plugged in later is offered
        Save();
        if (_session.State != SessionState.Off && !_session.OutputIsFloating && ChosenPlacement is { } placement)
        {
            await _session.MoveOutputAsync(placement);
            GuideZoomSharing();
        }
        else if (_session.State == SessionState.Off)
        {
            Message("Your presentation will show in a window you can move, resize and share in your meeting.");
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

    /// <summary>Settings › Appearance › Accent colour.</summary>
    public void SetUseWindowsAccent(bool use)
    {
        Settings.UseWindowsAccent = use;
        Save();
        Theme.UseWindowsAccent = use;
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
        if (!show) Message("Status pill off. Messages still appear here.");
        else if (_widget is { IsDisposed: false, Visible: true }) Message("Status pill on. It shows what's on the TV whenever the now-showing bar is hidden.");
    }

    public void TogglePauseShortcuts()
    {
        ShortcutsPaused = !ShortcutsPaused;
        ApplyShortcuts(reportProblems: false);
        if (ShortcutsPaused) Message("Keyboard shortcuts paused.", BadgeDot.Accent, Button("Turn them back on", TogglePauseShortcuts));
        else Message("Keyboard shortcuts are on again.");
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
            _badge.ShowMessage($"{keys}{more} {(taken.Count == 1 ? "is" : "are")} used by another app, so {(taken.Count == 1 ? "it doesn't" : "they don't")} work here.", BadgeDot.Warning,
                [Button("Change shortcuts", OpenShortcuts, primary: false)]);
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
        _ = Motion.Enter(_palette, _palette.LogicalToDeviceUnits(10)); // fades and rises in, like Windows' own search
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
        _quickPanel?.Close();
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

    // ───────────────────────────── commands shared by the tray, help and Settings ─────────────────────────────

    /// <summary>
    /// Runs a tray or help command ("settings:output", "record-picker", "output:\\.\DISPLAY2"…);
    /// anything else is an action id (<see cref="RunAsync"/>). Never throws.
    /// </summary>
    public async Task RunCommandAsync(string command)
    {
        try
        {
            switch (command)
            {
                case "help": OpenHelp(); return;
                case "settings": OpenSettings(); return;
                case "settings:output": OpenSettings(SettingsForm.Section.Output); return;
                case "settings:appearance": OpenSettings(SettingsForm.Section.Appearance); return;
                case "quit": RequestQuit(); return;
                case "record-output": RecordOutput(); return;
                case "record-picker": OpenRecordPicker(); return;
                case "stop-recording": _recording.Stop(); return;
                case "pause-recording": TogglePauseRecording(); return;
                case "open-recordings": OpenRecordingsFolder(); return;
                case "output-window": await SetOutputWindowAsync(); return;
                case "zoom": await ShowInZoomAsync(); return;
                case "show-again":
                    if (!await PresentLastShownAsync()) Message($"“{Shorten(Settings.LastSource?.Title ?? "", 40)}” isn't open any more.");
                    return;
                case "stop-after:never": SetRecordingStopAfter(null); return;
            }
            if (command.StartsWith("stop-after:", StringComparison.Ordinal)
                && int.TryParse(command.AsSpan("stop-after:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minutes))
            {
                SetRecordingStopAfter(TimeSpan.FromMinutes(minutes));
                return;
            }
            if (command.StartsWith("output:", StringComparison.Ordinal))
            {
                string device = command["output:".Length..];
                if (_monitors.FirstOrDefault(m => m.DeviceName == device) is { } monitor) await SetOutputMonitorAsync(monitor);
                else Message("That screen isn't connected any more.");
                return;
            }
            await RunAsync(command);
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    /// <summary>"the TV", "the Zoom screen", "the window", "Screen 2": where the audience looks.</summary>
    public string AudienceScreenName => OutputName() is "TV" ? "the TV" : OutputName();

    /// <summary>Search and help (the palette), opened rather than toggled.</summary>
    public void OpenHelp()
    {
        if (_palette is { IsDisposed: false })
        {
            _palette.Activate();
            return;
        }
        OpenPalette();
    }

    // ───────────────────────────── browsers: keep to the page chosen ─────────────────────────────

    private readonly TitleWatcher _titles = new();
    private string? _chosenPage;
    private nint _chosenPageWindow;
    private bool _toldKeepPage;

    /// <summary>
    /// When a browser window is shown, the audience should keep seeing the page that was chosen:
    /// a browser window shows whichever tab is active, so switching tabs or going to another page
    /// cuts the picture to black (BrowserTab) until you go back. Watched only while a browser is shown.
    /// </summary>
    private void UpdateBrowserPage()
    {
        bool watch = Settings.KeepBrowserPage && !_quitting && _session.State == SessionState.Presenting
            && _session.Source is { IsScreen: false } source && BrowserTab.IsBrowser(source.AppName) && WindowEnumerator.IsAlive(source.Handle);
        if (!watch)
        {
            _titles.Stop();
            _chosenPageWindow = 0;
            _chosenPage = null;
            if (_session.IsAwayFromPage) _ = _session.ComeBackAsync();
            return;
        }

        SourceWindow shown = _session.Source!;
        if (shown.Handle != _chosenPageWindow)
        {
            // A new browser window: the page showing now is the one chosen.
            _chosenPageWindow = shown.Handle;
            _chosenPage = WindowEnumerator.Refresh(shown)?.Title ?? shown.Title;
            _titles.Watch(shown.Handle);
            if (!_toldKeepPage)
            {
                _toldKeepPage = true;
                Message("Keeping to this page: if you switch tabs or pages in this browser window, the audience sees black until you come back.",
                    BadgeDot.Accent, Button("Follow my tabs instead", () => SetKeepBrowserPage(false), primary: false));
            }
        }
        CheckBrowserPage(); // e.g. after Go live, the browser may be on another tab by now
    }

    /// <summary>The shown browser's title changed: still the chosen page, or not?</summary>
    private void CheckBrowserPage()
    {
        if (_chosenPageWindow == 0 || _session.Source is not { } source || source.Handle != _chosenPageWindow) return;
        if (_session.IsFrozen) return; // a frozen picture already stays put
        string now = WindowEnumerator.Refresh(source)?.Title ?? "";
        if (BrowserTab.SamePage(_chosenPage, now))
        {
            if (_session.IsAwayFromPage) _ = _session.ComeBackAsync();
            return;
        }
        if (_session.IsAwayFromPage) return;
        _session.CutAway();
        Message($"You moved to another tab or page, so the audience sees black. Go back to “{Shorten(BrowserTab.Normalize(_chosenPage), 40)}” to show it again.",
            BadgeDot.Warning, Button("Show this page instead", ShowCurrentBrowserPage));
    }

    /// <summary>The presenter wants the page they're on now to be the one shown.</summary>
    private async Task ShowCurrentBrowserPage()
    {
        if (_session.Source is not { } source || source.Handle != _chosenPageWindow) return;
        _chosenPage = WindowEnumerator.Refresh(source)?.Title ?? _chosenPage;
        await _session.ComeBackAsync();
    }

    /// <summary>Settings › Where it shows › Browsers.</summary>
    public void SetKeepBrowserPage(bool keep)
    {
        Settings.KeepBrowserPage = keep;
        Save();
        UpdateBrowserPage();
        if (!keep) Message("The audience now sees whichever tab or page you're on in the browser window.");
    }

    // ───────────────────────────── a screen plugged in while showing in a window ─────────────────────────────

    /// <summary>
    /// Set to show in a window, and a TV or screen has been connected since (an HDMI cable): put the
    /// presentation on it. When nothing is showing yet, that happens at once, with Undo; while
    /// showing in the window (maybe shared in a meeting), it asks. Asked once per new screen.
    /// </summary>
    private async Task OfferNewScreenAsync()
    {
        if (OutputChooser.NewScreenSinceWindowChosen(_monitors, Settings.WindowModeScreens, Settings.OutputInWindow) is not { } screen) return;
        string what = screen.IsTv ? "A TV" : "A screen";
        RememberWindowModeScreens(); // asked once for this screen, whatever the answer
        if (_session.State == SessionState.Off)
        {
            await SetOutputMonitorAsync(screen, quiet: true);
            Message($"{what} is connected ({screen.Name}), so your presentation will show there.", BadgeDot.Accent,
                Button("Keep it in a window", SetOutputWindowAsync, primary: false));
            return;
        }
        Message($"{what} was connected ({screen.Name}). Show your presentation on it instead of the window?", BadgeDot.Accent,
            Button("Show it there", () => SetOutputMonitorAsync(screen)), Button("Keep the window", () => { }, primary: false));
    }

    private void RememberWindowModeScreens()
    {
        Settings.WindowModeScreens = [.. _monitors.Select(m => m.DeviceName)];
        Save();
    }

    // ───────────────────────────── the floating window steps back ─────────────────────────────

    private readonly ForegroundWatcher _foreground = new();
    private nint _steppedBackOutput;
    private bool _steppedBack;
    private bool _toldStepBack;

    /// <summary>
    /// While the presenter works in what a private preview shows, the preview steps behind other
    /// windows; it comes back on top when they switch to something else. A floating window that a
    /// meeting could share never steps back (FloatingStepBack): the audience must only ever see the
    /// chosen source. Only watched while a private preview shows something.
    /// </summary>
    private void UpdateStepBack(nint? active)
    {
        if (_quitting || !_session.OutputIsFloating || _session.State != SessionState.Presenting || _session.Source is not { } source
            || !_session.IsPrivatePreview)
        {
            // Not a private preview: it may be shared, so it stays in front (bring it back if it stepped back).
            if (_steppedBack && _session.OutputIsFloating) _session.SetOutputSteppedBack(false);
            _foreground.Stop();
            _steppedBackOutput = 0;
            _steppedBack = false;
            return;
        }
        _foreground.Start();
        nint output = _session.OutputHandle;
        if (output != _steppedBackOutput)
        {
            _steppedBackOutput = output; // a new window starts in front
            _steppedBack = false;
        }

        var hwnd = (Windows.Win32.Foundation.HWND)(active ?? (nint)Windows.Win32.PInvoke.GetForegroundWindow());
        nint activeRoot = hwnd.IsNull ? 0 : (nint)Windows.Win32.PInvoke.GetAncestor(hwnd, Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        nint sourceRoot = source.IsScreen || !WindowEnumerator.IsAlive(source.Handle) ? 0
            : (nint)Windows.Win32.PInvoke.GetAncestor((Windows.Win32.Foundation.HWND)source.Handle, Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        if (!source.IsScreen && sourceRoot == 0) return; // the window is going away: leave it as it is
        bool onShownScreen = source.Screen is { } screen && !hwnd.IsNull && DisplayHelpers.FromWindow((nint)hwnd) is { } at && at.IsSameScreen(screen);
        bool back = FloatingStepBack.ShouldStepBack(shareable: !_session.IsPrivatePreview, activeRoot, sourceRoot, activeRoot == output && output != 0, onShownScreen);
        // On a switch between windows, apply it even if nothing seems to have changed: clicking the
        // output window raises it without telling us (our own windows aren't reported).
        if (back == _steppedBack && active is null) return;
        _steppedBack = back;
        _session.SetOutputSteppedBack(back);
        if (back && !_toldStepBack)
        {
            _toldStepBack = true;
            Message("The preview steps behind while you work on the screen it shows. Click it in the taskbar to see it again. Meetings can't see it.");
        }
    }

    // ───────────────────────────── quick-actions panel ─────────────────────────────

    private QuickPanel? _quickPanel;

    /// <summary>
    /// Left-click on the tray icon: the quick-actions panel above the clock, like Windows 11's
    /// Quick Settings. A second click closes it (the click that closed it by taking focus away
    /// doesn't reopen it).
    /// </summary>
    public void ToggleQuickPanel()
    {
        if (_quickPanel is { IsDisposed: false, Visible: true } open)
        {
            open.Close();
            return;
        }
        if (_lastQuickPanelClose is { } closed && DateTime.UtcNow - closed < TimeSpan.FromMilliseconds(400)) return;

        var panel = new QuickPanel { ShortcutHint = id => ActiveShortcut(id)?.ToString() };
        panel.Chosen += (_, tile) => _ = RunCommandAsync(tile.Command);
        panel.FormClosed += (_, _) =>
        {
            _lastQuickPanelClose = panel.ClosedAt == default ? DateTime.UtcNow : panel.ClosedAt;
            if (ReferenceEquals(_quickPanel, panel)) _quickPanel = null;
            ScheduleTrim();
        };
        _quickPanel = panel;
        panel.SetContent(QuickPanelContent(), QuickPanelHeaderContent());
        panel.Location = QuickPanelSpot(panel.Size);
        panel.Show();
        panel.Activate();
        _ = Motion.SlideIn(panel, panel.LogicalToDeviceUnits(14));
    }

    private DateTime? _lastQuickPanelClose;

    /// <summary>Keeps an open panel current (presenting, blank, recording time…).</summary>
    private void UpdateQuickPanel()
    {
        if (_quickPanel is { IsDisposed: false, Visible: true } panel) panel.SetContent(QuickPanelContent(), QuickPanelHeaderContent());
    }

    private QuickPanelState QuickPanelContent() => new()
    {
        Session = _session.State,
        Blanked = _session.IsBlanked,
        Frozen = _session.IsFrozen,
        Cropped = _session.Source?.Crop is not null,
        Recording = _recording.Status,
        Where = AudienceScreenName,
        ShowAgainTitle = _session.State == SessionState.Off ? Settings.LastSource?.Title : null,
    };

    private QuickPanelHeader QuickPanelHeaderContent()
    {
        string? recording = RecordingBadgeText();
        if (_session.State == SessionState.Presenting && _session.Source is { } source)
        {
            string title = source.Screen is { } screen ? $"{screen.Name} (whole screen)" : source.Title.Length > 0 ? source.Title : source.AppName;
            string detail = _session.IsBlanked ? $"{OutputNameCapitalised()} is blank" : _session.IsFrozen ? ShowingText(frozen: true) : ShowingText(frozen: false);
            Rectangle? crop = !source.IsScreen && source.Crop is { } c ? c.ToPixels(WindowEnumerator.ClientSize(source.Handle)) : null;
            return new QuickPanelHeader(_session.IsBlanked ? BadgeDot.Hollow : BadgeDot.Accent, title, detail, recording, source.IsScreen ? 0 : source.Handle, crop);
        }
        if (_session.State == SessionState.IdleScreen)
            return new QuickPanelHeader(BadgeDot.Hollow, "Nothing chosen yet", $"{OutputNameCapitalised()} shows a blank screen", recording, 0, null);
        if (_recording.IsActive)
            return new QuickPanelHeader(BadgeDot.Recording, _recording.Description.Length > 0 ? _recording.Description : "Recording", "Recording while you work", recording, _recording.SourceHandle, null);
        string where = Settings.OutputInWindow ? "in a window" : ChosenOutput is { } m ? $"on {Describe(m)}" : "on the TV";
        return new QuickPanelHeader(BadgeDot.Hollow, "Not showing anything", $"Ready to show {where}", null, 0, null);
    }

    /// <summary>
    /// Above the clock, like Windows' own flyouts: the corner of the screen's work area next to
    /// the taskbar, wherever the taskbar is. Never on the TV: if the click came from there, the
    /// presenter's screen is used instead.
    /// </summary>
    private Point QuickPanelSpot(Size size)
    {
        DisplayMonitor? screen = _monitors.FirstOrDefault(m => m.Bounds.Contains(Cursor.Position));
        if (screen is null || _session.FullScreenOutput is { } tv && _monitors.Count > 1 && screen.IsSameScreen(tv)) screen = PresenterScreen();
        Rectangle work = screen?.WorkArea ?? Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Rectangle bounds = screen?.Bounds ?? work;
        int margin = (int)Math.Round(12 * (screen?.Scale ?? 1));
        int x = work.Right - size.Width - margin, y = work.Bottom - size.Height - margin;
        if (work.Top > bounds.Top) y = work.Top + margin;        // taskbar at the top
        if (work.Left > bounds.Left) x = work.Left + margin;     // taskbar on the left
        return new Point(Math.Max(work.Left, x), Math.Max(work.Top, y));
    }

    // ───────────────────────────── text size ─────────────────────────────

    /// <summary>
    /// Settings › Appearance › Text size. The tray menu, badge and bar pick it up at once; the
    /// picker and palette are rebuilt next time they open; an open Settings window reopens at the
    /// same section so its rows are laid out for the new size.
    /// </summary>
    public void SetTextSize(float scale)
    {
        float next = TextSize.Clamp(scale);
        if (Math.Abs(Theme.TextScale - next) < 0.001f) return;
        Settings.TextSize = next;
        Save();
        Theme.TextScale = next;
        if (_picker is { Visible: false }) DiscardPicker(); // laid out with the old fonts
        _palette?.Close();
        if (_settingsForm is { IsDisposed: false } open)
        {
            SettingsForm.Section section = open.CurrentSection;
            // After the click that got us here has finished inside the old window.
            BeginInvokeOnUi(() =>
            {
                if (_settingsForm is { IsDisposed: false } still) still.Close();
                OpenSettings(section);
            });
        }
        _widget?.Invalidate();
        UpdateBadge(reveal: false);
        string label = TextSize.Choices.FirstOrDefault(c => Math.Abs(c.Scale - next) < 0.001f).Label ?? "Normal";
        Message($"Text size: {label}.");
    }

    // ───────────────────────────── sharing in meetings (FR-12) ─────────────────────────────

    private ShareGuideForm? _shareGuide;

    /// <summary>"Show me how": the three-step guide for sharing the window or the Zoom screen in a meeting.</summary>
    public void OpenShareGuide(ShareGuideKind kind)
    {
        if (kind == ShareGuideKind.Window) _guidedWindow = true;
        else _guidedZoom = true;
        if (_shareGuide is { IsDisposed: false } old) old.Close();
        var guide = new ShareGuideForm(this, kind, _monitors.FirstOrDefault(m => m.IsVirtual)?.Name);
        guide.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_shareGuide, guide)) _shareGuide = null;
            ScheduleTrim();
        };
        _shareGuide = guide;
        CentreOnPresenterScreen(guide);
        guide.Show();
        guide.Activate();
    }

    /// <summary>On the presenter's screen, never the TV (SRS 4.5).</summary>
    private void CentreOnPresenterScreen(Form form)
    {
        if (PresenterScreen() is not { } screen) return;
        form.StartPosition = FormStartPosition.Manual;
        Rectangle area = screen.WorkArea;
        Size size = form.Size;
        form.Location = new Point(area.Left + Math.Max(0, (area.Width - size.Width) / 2), area.Top + Math.Max(0, (area.Height - size.Height) / 2));
    }

    private bool _settingUpZoom;

    /// <summary>
    /// "Set up a Zoom screen": installs the virtual display driver through winget (after asking),
    /// waits for the new screen to appear, makes it the output and offers the share guide.
    /// </summary>
    public async Task SetUpZoomScreenAsync()
    {
        if (_monitors.FirstOrDefault(m => m.IsVirtual) is not null)
        {
            Message("A Zoom screen is already set up.", BadgeDot.Accent, Button("Show me how", () => OpenShareGuide(ShareGuideKind.ZoomScreen)));
            return;
        }
        if (_settingUpZoom)
        {
            Message("Still setting up the Zoom screen…");
            return;
        }
        string? winget = ZoomScreenSetup.FindWinget(File.Exists);
        if (winget is null)
        {
            if (ConfirmForm.Ask("Set up a Zoom screen",
                    "This PC doesn't have Windows' app installer. Open the free driver's download page instead? It explains how to install it.",
                    "Open the page"))
                ShellOpen(ZoomScreenSetup.ProjectPage);
            return;
        }
        if (!ConfirmForm.Ask("Set up a Zoom screen?",
                "This installs the free Virtual Display Driver. It adds a screen nobody looks at, so you can share a whole screen in meetings while yours stays private. Windows will ask your permission. It needs the internet and takes about a minute.",
                "Set it up"))
            return;

        _settingUpZoom = true;
        Message("Setting up the Zoom screen… Windows may ask your permission.");
        int code = -1;
        try
        {
            using Process? process = Process.Start(ZoomScreenSetup.StartInfo(winget));
            if (process is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    code = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    code = -2; // left running: it may be mid-install, and killing it could leave a half-installed driver
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _settingUpZoom = false;
        }
        Diag($"zoom setup: exit {code}");

        switch (ZoomScreenSetup.Interpret(code))
        {
            case ZoomSetupOutcome.Installed or ZoomSetupOutcome.AlreadyInstalled:
                await UseNewZoomScreenAsync();
                break;
            case ZoomSetupOutcome.Cancelled:
                Message("Zoom screen setup was cancelled.");
                break;
            default:
                Message("The Zoom screen couldn't be set up. Check the internet connection, then try again.", BadgeDot.Warning,
                    Button("Try again", SetUpZoomScreenAsync));
                break;
        }
    }

    /// <summary>The driver is in: wait for Windows to add the screen (a few seconds), then use it.</summary>
    private async Task UseNewZoomScreenAsync()
    {
        for (int i = 0; i < 15 && !_quitting; i++)
        {
            List<DisplayMonitor> monitors = DisplayHelpers.GetMonitors();
            if (monitors.FirstOrDefault(m => m.IsVirtual) is { } zoom)
            {
                _monitors = monitors;
                await SetOutputMonitorAsync(zoom);
                Message("The Zoom screen is ready.", BadgeDot.Accent, Button("Show me how", () => OpenShareGuide(ShareGuideKind.ZoomScreen)));
                return;
            }
            await Task.Delay(1000);
        }
        Message("The driver is installed. If no Zoom screen appears, restart the PC.");
    }

    // ───────────────────────────── finding SCP again: tray icon, Start menu ─────────────────────────────

    private readonly Installer _installer = new(Registry.CurrentUser, InstallLocations.ForCurrentUser(), new ShellLinkWriter());

    private static string Exe => Environment.ProcessPath ?? "";

    /// <summary>True: on the taskbar. False: hidden behind ^ (Windows 11). Null: Windows keeps no setting for it (Windows 10, or not shown yet).</summary>
    public bool? TrayIconPinned => TrayIconPin.IsPromoted(Registry.CurrentUser, Exe);

    /// <summary>
    /// Windows 11 can be told to keep the icon on the taskbar (Windows 10 has no such setting).
    /// Right after start Windows may not have written the icon's entry yet, so "unknown" counts as
    /// hidden: that's where Windows 11 puts every new icon.
    /// </summary>
    public bool CanPinTrayIcon => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && TrayIconPinned != true;

    /// <summary>Pins the icon, retrying once a few seconds later if Windows hasn't created its entry yet.</summary>
    private void PinTrayIconSoon()
    {
        if (TrayIconPin.Promote(Registry.CurrentUser, Exe)) return;
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            Guard(() => PinTrayIcon(quiet: true));
        };
        timer.Start();
    }

    public void PinTrayIcon(bool quiet = false)
    {
        if (TrayIconPin.Promote(Registry.CurrentUser, Exe))
        {
            if (!quiet) Message("The icon now stays next to the clock.");
        }
        else Message("Windows didn't allow it. Open the ^ arrow next to the clock and drag the icon onto the taskbar instead.");
    }

    /// <summary>A downloaded single-file exe that isn't (this version of) the installed copy.</summary>
    public bool CanInstall =>
        Installer.CanInstallFrom(Exe, Installer.IsSingleFile(Exe)) && !_installer.IsInstalledCopy(Exe) && _installer.InstalledCopyDiffers(Exe);

    /// <summary>A copy is in the Start menu (from here or from tools/install.ps1).</summary>
    public bool IsInstalled => _installer.IsInstalled;

    public void Install()
    {
        if (!CanInstall) return;
        InstallResult result = _installer.Install(Exe, typeof(ScpApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        Diag($"install: done={result.Done} {result.Problem}");
        if (result.Done) Message("Added to the Start menu and desktop. Search for “ScreenCast” in Start any time.");
        else Message("Couldn't add it to the Start menu. " + result.Problem, BadgeDot.Warning);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Set once the user confirmed uninstalling: Program runs it after the app has closed.</summary>
    public string? CleanupAfterExit { get; private set; }

    /// <summary>Settings › Uninstall…: asks, removes shortcuts and the Apps entry, then quits; the exe is deleted after exit.</summary>
    public void RequestUninstall()
    {
        if (!ConfirmForm.Ask("Remove ScreenCast Presenter?",
                "This removes it from the Start menu, the desktop and Settings › Apps, and closes it. Your recordings, screenshots and settings are kept.",
                "Remove it"))
            return;
        _installer.Uninstall();
        CleanupAfterExit = _installer.CleanupCommand();
        RequestQuit();
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
        UpdateQuickPanel();
        UpdateStepBack(null);
        UpdateBrowserPage();
        _tray.Refresh();
        ScheduleTrim();
    }

    private void UpdateBadge(bool reveal)
    {
        SessionController s = _session;
        var blank = new BadgeButton("Blank", false, () => _ = RunAsync("blank"));
        var showAgain = new BadgeButton("Show again", true, () => _ = RunAsync("blank"));
        var stop = new BadgeButton("Stop", false, () => _ = RunAsync("stop"));
        var pick = new BadgeButton("Choose…", true, OpenPicker);

        var goLive = new BadgeButton("Go live", true, () => _ = RunAsync("freeze"));
        bool keepsPicture = s.EngineName is { } engine && engine.StartsWith("Windows Graphics", StringComparison.Ordinal);

        BadgeContent? content = s.State switch
        {
            SessionState.Presenting when s.IsBlanked => new BadgeContent(BadgeDot.Hollow, $"{OutputNameCapitalised()} is blank", "Your audience sees black", [showAgain, stop]),
            SessionState.Presenting when s.IsAwayFromPage => new BadgeContent(BadgeDot.Warning, "On another page", "Your audience sees black until you go back",
                [Button("Show this page instead", ShowCurrentBrowserPage), stop]),
            SessionState.Presenting when s.IsFrozen => new BadgeContent(BadgeDot.Warning, $"{OutputNameCapitalised()} is frozen", "You can scroll and click privately", [goLive, stop]),
            SessionState.Presenting when s.IsSourceMinimized && keepsPicture => new BadgeContent(BadgeDot.Warning, "Window minimised", $"{OutputNameCapitalised()} keeps showing its last picture", [stop]),
            SessionState.Presenting when s.IsSourceMinimized => new BadgeContent(BadgeDot.Warning, "Window minimised", $"{OutputNameCapitalised()} is black until you restore it", [stop]),
            SessionState.Presenting => new BadgeContent(BadgeDot.Accent, ShowingText(frozen: false), $"{s.Source?.Title} · {s.Source?.AppName}", [blank, stop]),
            SessionState.IdleScreen => new BadgeContent(BadgeDot.Hollow, $"{OutputNameCapitalised()} shows a blank screen", "Choose something to show", [pick, stop]),
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
        // One status indicator at a time: the now-showing bar already shows what's on, where, and the
        // recording time (FR-54), so the pill only carries the status when the bar is hidden.
        // Messages still appear in the pill either way.
        bool barShowsStatus = _widget is { IsDisposed: false, Visible: true };
        _badge.SetStatus(!barShowsStatus && (Settings.ShowBadge || _recording.IsActive) ? content : null, reveal);
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
        // Around a window, the outline sits just above it, so other windows in front cover it too.
        _outline.Update(bounds, ShowingText(s.IsFrozen), _monitors, s.FullScreenOutput, s.Source is { IsScreen: false } window ? window.Handle : 0);
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
        if (m is null) return "the TV";
        return m.IsVirtual ? "the Zoom screen" : m.IsTv ? "TV" : m.Name;
    }

    private DisplayMonitor? PresenterScreen() =>
        OutputChooser.PresenterScreen(_monitors, _session.FullScreenOutput, _monitors.FirstOrDefault(m => m.Bounds.Contains(Cursor.Position))); // cached list: no display-config queries on the picker path (NFR-08)

    /// <summary>", or press Ctrl+Alt+P," when that shortcut works; nothing otherwise.</summary>
    private string OrPress(string actionId) => ActiveShortcut(actionId) is { } key ? $", or press {key}," : "";

    /// <summary>"the TV (Screen 2)", "the Zoom screen", "Screen 3".</summary>
    private static string Describe(DisplayMonitor m) => m.IsVirtual ? "the Zoom screen" : m.IsTv ? $"the TV ({m.Name})" : m.Name;

    /// <summary>
    /// A message for the presenter at the top of their screen. Buttons let them act on it right
    /// there (no shortcut to remember); the first one is the suggested action unless marked otherwise.
    /// </summary>
    private void Message(string text, BadgeDot dot = BadgeDot.Accent, params BadgeButton[] buttons)
    {
        Diag("app: " + text);
        _badge.ShowMessage(text, dot, buttons);
    }

    private BadgeButton Button(string label, Action run, bool primary = true) => new(label, primary, () => Guard(run));

    private BadgeButton Button(string label, Func<Task> run, bool primary = true) => new(label, primary, () => _ = GuardAsync(run));

    private async Task GuardAsync(Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    /// <summary>Stop (from anywhere), then offer to bring the same thing straight back.</summary>
    private async Task StopShowingAsync()
    {
        SourceWindow? shown = _session.State == SessionState.Presenting ? _session.Source : null;
        await _session.StopAsync();
        if (shown is null || _session.State != SessionState.Off || _quitting) return;
        string name = shown.Screen is { } screen ? screen.Name : Shorten(shown.Title.Length > 0 ? shown.Title : shown.AppName, 40);
        Message($"Stopped showing “{name}”.", BadgeDot.Hollow, Button("Show it again", () => ShowAgainAsync(shown)));
    }

    private async Task ShowAgainAsync(SourceWindow source)
    {
        if (source.Screen is not null)
        {
            await PresentAsync(source);
            return;
        }
        if (WindowEnumerator.Refresh(source) is { } alive) await PresentAsync(alive with { Crop = source.Crop });
        else Message($"“{Shorten(source.Title, 40)}” has closed.");
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
                    Message("The TV was disconnected, so showing stopped. Reconnect it, then choose what to show.", BadgeDot.Warning);
                }
                else if (now.Bounds != output.Bounds)
                {
                    await _session.MoveOutputAsync(now, topMost: _monitors.Count > 1);
                }
            }
            if (_session.Source?.Screen is { } shown && !_monitors.Any(m => m.IsSameScreen(shown)))
            {
                await _session.StopAsync();
                Message("The screen you were showing was disconnected, so showing stopped.", BadgeDot.Warning);
            }
            CheckDuplicateDisplay();
            OnSessionChanged(reveal: false);
            await OfferNewScreenAsync(); // an HDMI cable plugged in while set to show in a window
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
        _badge.ShowMessage("Something went wrong, but ScreenCast Presenter is still running.", BadgeDot.Warning);
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
        _foreground.Dispose();
        _titles.Dispose();
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
