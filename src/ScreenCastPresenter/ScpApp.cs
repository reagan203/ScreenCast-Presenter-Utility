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
internal sealed partial class ScpApp : IDisposable
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
    private string? _lastSaveProblem;
    private bool _quitting;

    public ScpApp(SettingsStore store)
    {
        _store = store;
        _guard = new CaptureGuard();
        _monitors = DisplayHelpers.GetMonitors();
        _session = new SessionController(new Win32SessionPlatform(() => store.Current, () => MarksWanted)) { BlankMessage = store.Current.BlankMessage };
        _hotkeys = new HotkeyManager();
        _badge = new PresentingBadge(PresenterScreen);
        _outline = new SourceOutline();
        _recording = new RecordingController(() => new RecordingHelperProcess());
        _tray = new TrayIcon(this);
        _commands = new CommandServer(HandleCommandLineAsync);

        _session.StateChanged += (_, _) => OnSessionChanged(reveal: true);
        _session.SourceGeometryChanged += (_, _) => OnSessionChanged(reveal: false);
        _session.OutputDoubleClicked += (_, _) => Guard(() => GoToShown(moveToScreen: true));
        _foreground.Changed += (_, active) => Guard(() => UpdateStepBack(active));
        _titles.TitleChanged += (_, _) => Guard(CheckBrowserPage);
        _windowChanges.Changed += (_, _) => Guard(CheckPrivateApps);
        _pointerTimer.Tick += (_, _) => Guard(OnPointerTick);
        _clock.Tick += (_, _) => Guard(OnClockTick);
        _slideshowTimer.Tick += (_, _) => _ = GuardAsync(OnSlideshowTickAsync);
        _updateTimer.Tick += (_, _) => _ = GuardAsync(OnUpdateTimerAsync);
        _arrivals = new ScreenArrivals(_monitors.Select(m => m.DeviceName));
        _session.EngineChanged += (_, _) => Guard(OnEngineChanged);
        _session.HoldCheck = source => PrivateAppOn(source) is not null ? HoldReason.PrivateApp : FileBoxOn(source) is not null ? HoldReason.FileBox : null;
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
        StartStage();
        StartExtras();
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
    /// <param name="commandLine">Started by a command-line request (FR-43): do it once ready.</param>
    public void Start(bool startedByWindows = false, string[]? commandLine = null)
    {
        _ = _session.SetDimAsync(Settings.Dim);
        RepairDesktopIcons(); // a crash last time left the desktop's icons hidden
        ApplyShortcuts(reportProblems: true);
        if (_store.LoadProblem is { } problem) _badge.ShowMessage(problem, BadgeDot.Warning);
        CheckDuplicateDisplay();
        PreparePickerSoon();
        TryRegistry(() => _startup.RepairPath()); // portable exe moved since "start with Windows" was set
        _ = GuardAsync(OfferNewScreenAsync); // a TV plugged in before SCP started
        _ = GuardAsync(_commands.RunAsync);  // later command-line launches come here
        ScheduleUpdateCheck();
        if (!Settings.FirstRunDone) OpenFirstRun();
        else if (!startedByWindows && commandLine is null)
            Message($"ScreenCast Presenter is ready. Click its icon by the clock{OrPress("picker")} to show something.", BadgeDot.Accent, Button("Choose what to show", OpenPicker));
        if (commandLine is not null) _ = GuardAsync(() => HandleCommandLineAsync(commandLine));
    }

    // ───────────────────────────── command line (FR-43) ─────────────────────────────

    private readonly CommandServer _commands;

    /// <summary>Messages shown while a command-line request runs, to explain a failure to the script.</summary>
    private List<string>? _commandMessages;

    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>A command line from another launch (through the pipe) or from this one's own start.</summary>
    private async Task<CliReply> HandleCommandLineAsync(string[] args)
    {
        Diag("command line: " + string.Join(' ', args));
        (CliRequest? request, string? error) = CommandLine.Parse(args);
        if (request is null) return Told(new CliReply(false, error ?? "That command wasn't understood."));
        if (request.Help) return new CliReply(true, CommandLine.Usage);
        if (_quitting) return new CliReply(false, "ScreenCast Presenter is closing.");
        await _commandGate.WaitAsync(); // the command it started with and one from the pipe never overlap
        List<string> shown = _commandMessages = [];
        try
        {
            CliReply reply = await RunCommandLineAsync(request);
            if (request.Actions.Any(a => a.StartsWith("cli:is:", StringComparison.Ordinal))) return reply; // "off" is an answer, not a problem to show
            return shown.Contains(reply.Message) ? reply : Told(reply);
        }
        catch (Exception ex)
        {
            HandleError(ex);
            return new CliReply(false, "Something went wrong, but ScreenCast Presenter is still running.");
        }
        finally
        {
            _commandMessages = null;
            _commandGate.Release();
        }

        // A button deck or desktop shortcut has no console: a failure is also said on screen.
        CliReply Told(CliReply reply)
        {
            if (!reply.Ok) Message(reply.Message, BadgeDot.Warning);
            return reply;
        }
    }

    /// <summary>The output first, then what to show, then the actions in order. Stops at the first step that can't be done.</summary>
    private async Task<CliReply> RunCommandLineAsync(CliRequest request)
    {
        if (request.Output is { } output && await ApplyCommandOutputAsync(output) is { } outputProblem) return new CliReply(false, outputProblem);
        if (request.Show is { } show && await ShowFromCommandLineAsync(show) is { } showProblem) return new CliReply(false, showProblem);

        bool status = false;
        foreach (string action in request.Actions)
        {
            switch (action)
            {
                // rev 10 (FR-129 and the new sources)
                case "cli:status":
                    status = true; // answered last, after everything else asked for
                    break;
                case string ask when ask.StartsWith("cli:is:", StringComparison.Ordinal):
                {
                    bool on = IsSwitchOn(ask["cli:is:".Length..]);
                    return new CliReply(on, on ? "on" : "off");
                }
                case string pdf when pdf.StartsWith("cli:pdf:", StringComparison.Ordinal):
                    if (!await ShowDocumentAsync(pdf["cli:pdf:".Length..], 1)) return new CliReply(false, LastCommandMessage("The PDF couldn't be shown."));
                    break;
                case string pdfPage when pdfPage.StartsWith("cli:pdf-page:", StringComparison.Ordinal):
                {
                    string rest = pdfPage["cli:pdf-page:".Length..];
                    int colon = rest.IndexOf(':', StringComparison.Ordinal);
                    int page = int.Parse(rest[..colon], System.Globalization.CultureInfo.InvariantCulture);
                    if (!await ShowDocumentAsync(rest[(colon + 1)..], page)) return new CliReply(false, LastCommandMessage("The PDF couldn't be shown."));
                    break;
                }
                case string video when video.StartsWith("cli:video:", StringComparison.Ordinal):
                    if (!await ShowVideoAsync(video["cli:video:".Length..])) return new CliReply(false, LastCommandMessage("The video couldn't be shown."));
                    break;
                case string gif when gif.StartsWith("cli:gif:", StringComparison.Ordinal):
                    _ = GuardAsync(() => MakeGifAsync(gif["cli:gif:".Length..])); // takes a while: said when it's done
                    break;
                case "cli:timer-off":
                    StopTimer(null);
                    break;
                case string timer when timer.StartsWith("timer:", StringComparison.Ordinal):
                    if (_session.State == SessionState.Off) return new CliReply(false, "Show something first, then put a timer on it.");
                    await RunCommandAsync(timer);
                    break;
                case string night when night.StartsWith("night:", StringComparison.Ordinal):
                    await RunCommandAsync(night);
                    break;
                case "copy-text" or "show-whiteboard" or "room-sign":
                    await RunCommandAsync(action);
                    break;
                case "cli:blank-on" or "cli:blank-off":
                    if (_session.State == SessionState.Off) return new CliReply(false, "Nothing is being shown right now.");
                    if (_session.IsBlanked != (action == "cli:blank-on")) await _session.ToggleBlankAsync(withMessage: false);
                    break;
                case "cli:freeze-on" or "cli:freeze-off":
                    if (_session.State != SessionState.Presenting) return new CliReply(false, "Nothing is being shown right now.");
                    if (_session.IsFrozen != (action == "cli:freeze-on")) await _session.ToggleFreezeAsync();
                    if (_session.IsFrozen != (action == "cli:freeze-on")) return new CliReply(false, LastCommandMessage("Couldn't freeze what is shown."));
                    break;
                case "cli:record-on":
                    if (!_recording.IsActive) ToggleRecording();
                    break;
                case "stop-recording":
                    if (_recording.IsActive) _recording.Stop();
                    break;
                case "pick-window" or "crop" or "hide-part" or "get-ready" or "second-tv":
                    _ = RunCommandAsync(action); // waits for the presenter's click: don't hold up the command line meanwhile
                    break;
                case "save-room" or "qr":
                    _ = RunCommandAsync(action); // asks the presenter something first: don't hold up the command line
                    break;
                case "caption" when Settings.CaptionName.Length == 0 && !_captionOn:
                    _ = RunCommandAsync(action); // asks for the name first
                    break;
                case "cli:qr-off":
                    if (_qrText is not null) SetQr(null);
                    break;
                case "cli:split-off":
                    StopSplit(null);
                    break;
                case string picture when picture.StartsWith("cli:picture:", StringComparison.Ordinal):
                    if (!await ShowPictureAsync(picture["cli:picture:".Length..])) return new CliReply(false, LastCommandMessage("The picture couldn't be shown."));
                    break;
                case "cli:tv:on" or "cli:tv:off":
                    if (!await SetTvPowerAsync(action == "cli:tv:on")) return new CliReply(false, LastCommandMessage("The TV didn't answer."));
                    break;
                case string input when input.StartsWith("cli:tv-input:", StringComparison.Ordinal):
                    if (!uint.TryParse(input["cli:tv-input:".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint code)
                        || !await SetTvInputAsync(code)) return new CliReply(false, LastCommandMessage("The TV didn't answer."));
                    break;
                case string export when export.StartsWith("cli:export-settings:", StringComparison.Ordinal):
                    if (ExportSettingsTo(export["cli:export-settings:".Length..]) is { } exportProblem) return new CliReply(false, exportProblem);
                    break;
                case string import when import.StartsWith("cli:import-settings:", StringComparison.Ordinal):
                    if (ImportSettingsFrom(import["cli:import-settings:".Length..]) is { } importProblem) return new CliReply(false, importProblem);
                    break;
                case string card when card.StartsWith("cli:card:", StringComparison.Ordinal):
                    if (!await ShowCardAsync(card["cli:card:".Length..].Replace("\\n", "\n", StringComparison.Ordinal))) return new CliReply(false, LastCommandMessage("The card couldn't be shown."));
                    break;
                case string split when split.StartsWith("cli:split:", StringComparison.Ordinal):
                    if (await SplitFromCommandLineAsync(split["cli:split:".Length..]) is { } splitProblem) return new CliReply(false, splitProblem);
                    break;
                case string layout when layout.StartsWith("cli:layout:", StringComparison.Ordinal):
                    SetSplitLayout(SplitScreen.Parse(layout["cli:layout:".Length..]));
                    break;
                case string sw when sw.StartsWith("cli:on:", StringComparison.Ordinal) || sw.StartsWith("cli:off:", StringComparison.Ordinal):
                {
                    bool on = sw.StartsWith("cli:on:", StringComparison.Ordinal);
                    if (await SwitchAsync(sw[(on ? "cli:on:" : "cli:off:").Length..], on) is { } problem) return new CliReply(false, problem);
                    break;
                }
                case string qr when qr.StartsWith("cli:qr:", StringComparison.Ordinal):
                    if (_session.State != SessionState.Presenting) return new CliReply(false, "Show something first, then put a QR code on it.");
                    SetQr(qr["cli:qr:".Length..]);
                    break;
                case string room when room.StartsWith("room:", StringComparison.Ordinal):
                    if (!await ApplyRoomAsync(room["room:".Length..])) return new CliReply(false, LastCommandMessage("That room couldn't be set up."));
                    break;
                default:
                    await RunCommandAsync(action);
                    break;
            }
        }
        return new CliReply(true, status ? StatusJson() : CommandStatus());
    }

    /// <summary>"--output 2 | window | zoom". Null when done, else why not.</summary>
    private async Task<string?> ApplyCommandOutputAsync(CliOutput output)
    {
        if (output.Window)
        {
            await SetOutputWindowAsync();
            return null;
        }
        if (output.Zoom)
        {
            if (_monitors.FirstOrDefault(m => m.IsVirtual) is not { } zoom) return "No Zoom screen is set up. Use --output window, or set one up in Settings › Where it shows.";
            await SetOutputMonitorAsync(zoom, quiet: true);
            return null;
        }
        if (_monitors.FirstOrDefault(m => m.Number == output.Screen) is not { } screen)
            return $"Screen {output.Screen} isn't connected. Connected: {string.Join(", ", _monitors.Select(m => m.Name))}.";
        await SetOutputMonitorAsync(screen, quiet: true);
        return null;
    }

    /// <summary>"--present", "--screen", "--camera", "--favourite", "--again". Null when it's showing, else why not.</summary>
    private async Task<string?> ShowFromCommandLineAsync(CliShow show)
    {
        SourceWindow? wanted = null;
        switch (show.Kind)
        {
            case CliShowKind.Window:
                wanted = CommandLine.FindWindow(WindowEnumerator.GetPresentableWindows(), show.Value);
                if (wanted is null) return $"No open window matches “{show.Value}”. Open it first, or check the name.";
                await PresentAsync(wanted);
                break;
            case CliShowKind.Screen:
                if (_monitors.FirstOrDefault(m => m.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == show.Value) is not { } screen)
                    return $"Screen {show.Value} isn't connected.";
                wanted = SourceWindow.ForScreen(screen);
                await PresentAsync(wanted);
                break;
            case CliShowKind.Camera:
                return await ShowCameraFromCommandLineAsync(show.Value);
            case CliShowKind.Favourite:
                int slot = int.Parse(show.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (Settings.Favourites[slot - 1] is not { } favourite) return $"Favourite {slot} is empty.";
                await ShowFavouriteAsync(slot);
                return ShowsNow(favourite) ? null : LastCommandMessage($"Favourite {slot} couldn't be shown.");
            case CliShowKind.Again:
                if (Settings.LastSource is not { } last || !await PresentLastShownAsync()) return "There's nothing to show again: the last thing shown isn't open.";
                return ShowsNow(last) ? null : LastCommandMessage("It couldn't be shown again.");
        }
        bool showing = _session.State == SessionState.Presenting && (wanted is null || wanted.IsSameSource(_session.Source));
        return showing ? null : LastCommandMessage("It couldn't be shown.");

        // What's on now is that favourite (not whatever was showing before the command).
        bool ShowsNow(Favourite expected) =>
            _session.State == SessionState.Presenting && _session.Source is { } now && FavouriteMatcher.FromSource(now).Matches(expected);
    }

    /// <summary>"--camera Document": the camera whose name contains the text.</summary>
    private async Task<string?> ShowCameraFromCommandLineAsync(string name)
    {
        List<CameraDevice> cameras = await Cameras.ListAsync(fresh: true);
        if (Cameras.Find(cameras, name) is not { } camera)
            return cameras.Count == 0 ? "No camera is connected." : $"No camera matches “{name}”. Connected: {string.Join(", ", cameras.Select(c => c.Name))}.";
        SourceWindow wanted = SourceWindow.ForCamera(camera);
        await PresentAsync(wanted);
        return _session.State == SessionState.Presenting && wanted.IsSameSource(_session.Source) ? null : LastCommandMessage("The camera couldn't be shown.");
    }

    /// <summary>Why the last step failed, in the words shown to the presenter.</summary>
    private string LastCommandMessage(string fallback) => _commandMessages is { Count: > 0 } m ? m[^1] : fallback;

    /// <summary>What's on now, for the script's console.</summary>
    private string CommandStatus() => _session.State switch
    {
        SessionState.Presenting when _session.Source is { } s => $"{(_session.IsBlanked ? $"{OutputNameCapitalised()} is blank" : ShowingText(_session.IsFrozen))}: {(s.Title.Length > 0 ? s.Title : s.AppName)}",
        SessionState.IdleScreen => $"{OutputNameCapitalised()} shows a blank screen.",
        _ => _recording.IsActive ? "Recording." : "Not showing anything.",
    };

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
                case "pick-window": await PickWindowAsync(); break;
                case "previous-source": await PresentPreviousAsync(); break;
                case "next-window": await CycleAsync(+1); break;
                case "previous-window": await CycleAsync(-1); break;
                case "stop": await StopShowingAsync(); break;
                case "blank": await _session.ToggleBlankAsync(withMessage: false); break;
                case "blank-message": await _session.ToggleBlankAsync(withMessage: true); break;
                case "blank-countdown":
                    if (_countdownEnd is not null && _session.IsBlanked) await _session.ToggleBlankAsync(withMessage: false); // the key again: show again
                    else await StartCountdownAsync(Settings.CountdownMinutes);
                    break;
                case "restart-timer": RestartTalkTimer(); break;
                case "slideshow": await ToggleSlideshowAsync(); break;
                case "check-updates": await CheckForUpdatesAsync(manual: true); break;
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
                case "zoom-in": await ToggleZoomAsync(); break;
                case "zoom-in-more": await ZoomMoreAsync(); break;
                case "laser": SetLaser(!_laserOn); break;
                case "draw": ToggleDrawing(); break;
                case "inset": ToggleInset(); break;
                case "split-screen": ToggleSplit(); break;
                case "show-picture": await ChoosePictureAsync(); break;
                case "check-before-showing": CheckBeforeShowing(); break;
                case "save-replay": SaveReplay(); break;
                case "tv-on": await SetTvPowerAsync(on: true); break;
                case "tv-off": await SetTvPowerAsync(on: false); break;
                case "voice-commands": SetVoiceCommands(!Settings.VoiceCommands); break;
                case "notes": ToggleNotes(); break;
                case "edit-notes": EditNotes(); break;
                case "replay": SetReplay(!Settings.ReplayOn); break;
                case "show-card": await AskForCardAsync(); break;
                case "split-add": AddToSplit(); break;
                case "split-layout": NextSplitLayout(); break;
                case "hide-part": await HidePartAsync(); break;
                case "show-hidden": await ShowHiddenPartsAsync(); break;
                case "audience-view": ToggleAudienceView(); break;
                case "get-ready": ChooseNext(); break;
                case "go": await GoAsync(); break;
                case "spotlight": SetSpotlight(!_spotlightOn); break;
                case "caption": ToggleCaption(); break;
                case "qr": ToggleQr(); break;
                case "live-captions": ToggleLiveCaptions(); break;
                case "second-tv": await ToggleSecondTvAsync(); break;
                case "save-room": SaveRoom(); break;
                case "save-scene": SaveScene(); break;
                case "next-slide": SendSlide(forward: true); break;
                case "previous-slide": SendSlide(forward: false); break;
                case "stop-playlist": StopPlaylists("The playlist stopped."); break;
                case "phone-remote": OpenPairing(); break;
                case "auto-hide": ToggleAutoHide(); break;
                case "highlight-clicks": SetHighlightClicks(!Settings.HighlightClicks); break;
                case "show-keys": SetShowKeys(!Settings.ShowKeys); break;
                case "zoom-follows": SetZoomFollows((ZoomFollows)(((int)Settings.ZoomFollowsMode + 1) % 3)); break;
                case "pointer-ring": SetPointerRing(!Settings.PointerRing); break;
                case "crop": await CropCurrentAsync(); break;
                case "remove-crop": await RemoveCropAsync(); break;
                case "screenshot": await TakeScreenshotAsync(); break;
                case "record": ToggleRecording(); break;
                case "pause-recording": TogglePauseRecording(); break;
                // rev 10
                case "follow-me": ToggleFollowMe(); break;
                case "timer": AskTimer(); break;
                case "night-filter": CycleNightFilter(); break;
                case "inset-shape": SetInsetShape((InsetShape)(((int)Settings.InsetShapeMode + 1) % 3)); break;
                case "speaker-screen": ToggleSpeakerScreen(); break;
                case "rehearse": ToggleRehearsal(); break;
                case "cast": CastToWirelessTv(); break;
                case "copy-text": await CopyTextAsync(); break;
                case "make-gif": await ChooseGifSourceAsync(); break;
                case "show-pdf": await ChooseDocumentAsync(); break;
                case "show-video": await ChooseVideoAsync(); break;
                case "show-whiteboard": await ToggleWhiteboardAsync(); break;
                case "room-sign": await ShowRoomSignAsync(); break;
                case "clicker-keys": SetClickerKeys(!Settings.ClickerKeys, Settings.ClickerArrows); Message(Settings.ClickerKeys ? "Page Down and Page Up now go to what's shown while you present, wherever you're working." : "Page Down and Page Up work as usual again."); break;
                default:
                    if (TrySlot(id, "scene-", out int scene)) await ShowSceneAsync(scene);
                    else if (TrySlot(id, "save-favourite-", out int save)) SaveFavourite(save);
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
        if (SlideshowOn && !_slideshowSwitching) StopSlideshow("Slideshow stopped: you chose something to show.");
        PresenterChose(PlaylistScreen.Main);
        if (IsNeverShown(source))
        {
            Message($"“{source.AppName}” is on your never-show list, so it isn't shown.", BadgeDot.Warning,
                Button("Change the list", () => OpenSettings(SettingsForm.Section.Privacy), primary: false));
            return;
        }
        if (!await EnsureTvIsSeparateAsync()) return; // a TV copying this screen would show everything
        if (!await CheckFirstTimeAsync(source)) return; // "Check before showing" (FR-102): the presenter said not to
        WakeTvForShowing();
        if (source.Screen is { } secondScreen && _second?.FullScreenOutput is { } secondTv && secondScreen.IsSameScreen(secondTv))
        {
            Message($"{secondScreen.Name} is the second TV, so it can't be shown.");
            return;
        }
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
        else if (source.IsWindow && !WindowEnumerator.IsAlive(source.Handle))
        {
            Message($"“{source.Title}” has closed.");
            return;
        }

        // FR-04: apply the crop last used for this app.
        bool savedCrop = false;
        if (source.IsWindow && source.Crop is null && Settings.SavedCrops.TryGetValue(source.AppName, out CropRegion? crop))
        {
            source = source with { Crop = crop };
            savedCrop = true;
        }

        if (source.IsScreen)
        {
            // FR-112: the taskbar is left out, whatever part was chosen.
            if (LeaveOutTaskbar(source) is not { } clear)
            {
                Message("That part of the screen is its taskbar, which is never shown. Choose another part.");
                return;
            }
            source = clear;
        }
        else if (!savedCrop) source = await ShowOnlyThePageAsync(source); // FR-133: a browser shows only its page

        CheckDuplicateDisplay();
        bool onlyScreen = _monitors.Count < 2 && !placement.IsFloating;
        bool wasOff = _session.State == SessionState.Off;
        // The slideshow's own switch is skipped if the TV went blank, frozen or black meanwhile (checked as it runs).
        await _session.PresentAsync(source, placement, onlyIfLive: _slideshowSwitching || _playlistSwitching);
        if (_session.State != SessionState.Presenting) return;
        AfterPresenterShowed(source); // Follow me learns the app (FR-119); time budgets start (FR-121)
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

        if (present) await PresentAsync(source with { Crop = crop, AutoCrop = null });
        else if (source.IsScreen)
        {
            // FR-112: the part chosen, kept clear of the taskbar.
            if (LeaveOutTaskbar(source with { Crop = crop }) is not { } clear)
            {
                Message("That part is the screen's taskbar, which is never shown. Choose another part.");
                return;
            }
            await _session.SetAutoCropAsync(clear.Crop, clear.AutoCrop, fade: true);
        }
        else await _session.SetAutoCropAsync(crop, null, fade: true); // a crop of their own: a browser's page is left to it
        UpdatePageWatch();
        if (crop is not null) Message($"Showing part of “{Shorten(source.Title, 40)}”.", BadgeDot.Accent, Button("Show all of it", RemoveCropAsync));
    }

    private sealed record PartChoice(CropRegion? Crop);

    /// <summary>
    /// Lets the user drag a part of a window or screen. Null when cancelled or impossible (the
    /// reason is shown); a null crop inside means "all of it".
    /// </summary>
    private async Task<PartChoice?> ChoosePartAsync(SourceWindow source, string hint, bool startFromCrop = true)
    {
        if (source.IsPicture)
        {
            Message($"A picture isn't on your screen to choose part of. Zoom in instead{OrPress("zoom-in")}.");
            return null;
        }
        if (source.IsCamera)
        {
            Message($"A camera's picture isn't on your screen to choose part of. Zoom in instead{OrPress("zoom-in")} or point the camera.");
            return null;
        }
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

        Rectangle? initial = startFromCrop ? source.Crop?.ToScreen(client) : null;
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
        if (source.IsWindow && Settings.SavedCrops.Remove(source.AppName)) Save();
        if (source.ChosenCrop is null)
        {
            Message("All of it is already showing" + AutoCropWords(source) + ".");
            return;
        }
        if (source.IsScreen)
        {
            SourceWindow whole = LeaveOutTaskbar(source with { Crop = null }) ?? source with { Crop = null };
            await _session.SetAutoCropAsync(whole.Crop, whole.AutoCrop, fade: true);
        }
        else
        {
            SourceWindow whole = await ShowOnlyThePageAsync(source with { Crop = null, AutoCrop = null });
            await _session.SetAutoCropAsync(whole.Crop, whole.AutoCrop, fade: true);
        }
        UpdatePageWatch();
        Message($"Showing all of “{source.Title}”{AutoCropWords(_session.Source ?? source)}.");
    }

    /// <summary>
    /// FR-50/51: screenshot of what's being presented, or — when nothing is — of an area you drag
    /// on your screen. Saved to Pictures\Screenshots and copied to the clipboard.
    /// </summary>
    private async Task TakeScreenshotAsync()
    {
        if (Blocked(_policy.ScreenshotsAllowed, "screenshots")) return;
        SourceWindow? source = _session.State == SessionState.Presenting ? _session.Source : null;
        if (source is { IsOffScreen: true })
        {
            // The camera's (or picture's) picture is on the TV: take the TV's screen, which shows exactly it.
            if (_session.FullScreenOutput is not { } tv)
            {
                Message("A screenshot of a camera needs it shown full screen. Show it on the TV, then try again.");
                return;
            }
            source = SourceWindow.ForScreen(tv);
        }
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
            saved = await CaptureHost.CaptureStillToFileAsync(source, Settings.ShowCursor && source.IsScreen, path, Redactions.For(Settings, source), Settings.AutoHideKinds, Settings.AutoHideWords);
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
        Bitmap? thumbnail = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Image image = Image.FromStream(file);
            copied = TryCopyToClipboard(image);
            thumbnail = ScreenshotPreview.Thumbnail(image, ScreenshotPreview.ThumbnailSide * 2); // sharp on a high-DPI screen, still small
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
        {
            copied = false;
        }
        string title = copied ? "Screenshot saved and copied" : "Screenshot saved";
        if (thumbnail is not null && PresenterScreen() is { } previewOn) ShowScreenshotPreview(thumbnail, title, path, previewOn);
        else
        {
            thumbnail?.Dispose();
            _badge.ShowActions(title, Path.GetFileName(path),
            [
                new BadgeButton("Open", true, () => ShellOpen(path)),
                new BadgeButton("Show in folder", false, () => ShellOpen("explorer.exe", $"/select,\"{path}\"")),
            ]);
        }
        ScheduleTrim();
    }

    private ScreenshotPreview? _shotPreview;

    /// <summary>FR-51: the preview with Open, Copy and Show in folder; a newer screenshot replaces it.</summary>
    private void ShowScreenshotPreview(Bitmap thumbnail, string title, string path, DisplayMonitor screen)
    {
        _shotPreview?.Close();
        var preview = new ScreenshotPreview(thumbnail, title, Path.GetFileName(path),
            open: () => ShellOpen(path),
            copy: () => CopyScreenshotAgain(path),
            showInFolder: () => ShellOpen("explorer.exe", $"/select,\"{path}\""));
        preview.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_shotPreview, preview)) _shotPreview = null;
            ScheduleTrim();
        };
        _shotPreview = preview;
        preview.ShowAt(screen.WorkArea);
    }

    private void CopyScreenshotAgain(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Image image = Image.FromStream(file);
            Message(TryCopyToClipboard(image) ? "Screenshot copied." : "The screenshot couldn't be copied.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Message("The screenshot couldn't be copied: " + SavedFiles.Explain(ex), BadgeDot.Warning);
        }
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
            if (_session.Source is { IsOffScreen: true })
            {
                Message("To record a camera or a picture, show it full screen (on the TV or the Zoom screen), then record what it shows.");
                return;
            }
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
        if (source.IsOffScreen)
        {
            Message("To record a camera or a picture, show it full screen, then record what the TV shows.");
            return;
        }
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
        if (Blocked(_policy.RecordingAllowed, "recording")) return;
        string folder = Settings.RecordingFolder ?? SavedFiles.DefaultRecordingFolder;
        string path = SavedFiles.NextPath(folder, "Screen Recording", DateTime.Now, ".mp4", File.Exists);
        // Hidden parts (FR-74) are covered in a recording of the window or screen too (the TV's own picture already has them).
        var request = new RecordingRequest(source, Settings.ShowCursor, Settings.MeetingSound, Settings.RecordMicrophone, path, Redactions.For(Settings, source), Settings.AutoHideKinds, Settings.AutoHideWords);
        if (_recording.Start(request, description, followsOutput) is { } problem)
        {
            Message("Recording didn't start: " + problem, BadgeDot.Warning);
            return;
        }
        Diag($"recording: start {description} sound={Settings.MeetingSound} mic={Settings.RecordMicrophone}");
        StartChapters();
        UpdateBadge(reveal: true);
        if (!followsOutput && source.Screen is { } recordedScreen) MaybeRemindDoNotDisturb(recordedScreen);
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

    /// <summary>FR-59: stop by itself when the clock reaches this (local time); null for none.</summary>
    public DateTime? RecordingStopAt { get; private set; }

    /// <summary>"Stop automatically › At a set time…": asks for the time, then stops then.</summary>
    private void AskRecordingStopAt()
    {
        if (!_recording.IsActive)
        {
            Message("Nothing is being recorded.");
            return;
        }
        DateTime suggestion = DateTime.Now.AddHours(1);
        using var form = new FieldsForm("Stop recording at a set time", "The recording stops and is saved when this PC's clock reaches the time (today, or tomorrow if it's passed).", "Set it");
        (_, TextBox time) = form.AddField("Time (24-hour clock)", suggestion.ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture), 5, "17:30");
        form.Check = () => ScheduleEntry.ParseTime(time.Text) is null ? "Type the time like 17:30." : null;
        if (!form.Ask()) return;
        SetRecordingStopAt(RecordingStop.Next(ScheduleEntry.ParseTime(time.Text)!.Value, DateTime.Now));
    }

    public void SetRecordingStopAt(DateTime? at)
    {
        RecordingStopAt = at;
        if (at is { } t && _recording.IsActive) Message($"The recording stops by itself {RecordingStop.Words(t, DateTime.Now)}.", BadgeDot.Recording);
        StopRecordingIfDue();
        _tray.Refresh();
    }

    public void SetRecordingStopAfter(TimeSpan? limit)
    {
        RecordingStopAfter = limit;
        if (limit is null) RecordingStopAt = null; // "only when I stop it" clears both
        if (limit is { } l && _recording.IsActive)
            Message($"The recording stops by itself after {DurationWords(l)}.", BadgeDot.Recording);
        StopRecordingIfDue();
    }

    private void StopRecordingIfDue()
    {
        if (!_recording.IsActive || _recording.Status == RecordingStatus.Stopping) return;
        if (RecordingStopAt is { } at && DateTime.Now >= at)
        {
            _recordingEndNote = $"Stopped at {at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)} as planned, recording saved";
            _recording.Stop();
            return;
        }
        if (RecordingStopAfter is not { } limit || _recording.Elapsed < limit) return;
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
        SaveChapters(result);
        Diag($"recording: finished {result.End} {result.Length} {result.Bytes} bytes saved={result.Saved} {result.Detail}");
        UpdateBadge(reveal: false);
        _tray.Refresh();
        if (!result.Saved)
        {
            _recordingEndNote = null;
            _recordedOutput = null;
            RecordingStopAfter = null;
            RecordingStopAt = null;
            Message(result.Detail.Length > 0 ? "Recording not saved: " + result.Detail : "The recording didn't start.", BadgeDot.Warning);
            ScheduleTrim();
            return;
        }

        string? note = _recordingEndNote;
        _recordingEndNote = null;
        _recordedOutput = null;
        RecordingStopAfter = null; // a time limit is for one recording
        RecordingStopAt = null;
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
            title = source.IsScreen && source.Screen is { } screen ? $"{screen.Name} (whole screen)"
                : source.IsCamera ? $"{source.Title} (camera)"
                : source.IsDocument ? source.Title
                : source.IsBoard ? "the whiteboard"
                : source.IsRoomSign ? "the room sign"
                : source.IsVideo ? $"{source.Title} (video)"
                : source.IsPicture ? $"{source.Title} (picture)"
                : source.Title.Length > 0 ? source.Title : source.AppName;
            if (source.IsWindow)
            {
                thumb = source.Handle;
                if (source.Crop is { } c) crop = c.ToPixels(WindowEnumerator.ClientSize(source.Handle));
            }
            (dot, detail) = s switch
            {
                { IsBlanked: true } when _countdownEnd is { } end => (BadgeDot.Hollow, end > DateTime.UtcNow
                    ? $"Blank · back in {BlankCountdown.Clock(end - DateTime.UtcNow)}" : "Blank · the break is over"),
                { IsBlanked: true } => (BadgeDot.Hollow, $"{OutputNameCapitalised()} is blank · audience sees black"),
                _ when s.IsHeldFor(HoldReason.PrivateApp) => (BadgeDot.Warning, $"“{_privateAppShown ?? "A never-show app"}” is open here · audience sees black"),
                { IsAwayFromPage: true } => (BadgeDot.Warning, "On another page · audience sees black until you go back"),
                { IsFrozen: true } => (BadgeDot.Warning, ShowingText(frozen: true) + " · click to go to it"),
                { IsSourceMinimized: true } => (BadgeDot.Warning, "Minimised · click to restore it"),
                _ => (BadgeDot.Accent, ShowingText(frozen: false) + (source.Crop is null ? "" : " · part of it")),
            };
            if (TalkText() is { Length: > 0 } talk) detail += " · " + talk;
            if (_slideshowSlot is { } slide) detail = $"Slideshow, favourite {slide} · {detail}";
            if (source.IsWindow) menu.Add(("Go to this window", GoToShown));
            menu.Add((s.IsBlanked ? "Show again" : "Blank the TV", () => _ = RunAsync("blank")));
            menu.Add((s.IsFrozen ? "Go live" : "Freeze the TV", () => _ = RunAsync("freeze")));
            menu.Add(("Show something else…", OpenPicker));
            if (SlideshowOn) menu.Add(("Stop the slideshow", () => StopSlideshow("Slideshow stopped.")));
            if (_inset is not null) menu.Add(("Take the small picture off", () => RemoveInset("The small picture is off.")));
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
        if (_session.State == SessionState.Presenting && _session.Source is { IsOffScreen: true } camera)
        {
            Message(camera.IsPicture ? $"The picture “{camera.Title}” is being shown." : $"The camera “{camera.Title}” is being shown.");
            return;
        }
        nint target = _session.State == SessionState.Presenting && _session.Source is { IsWindow: true } source ? source.Handle
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
        _ = GuardAsync(ReshowAsync);
        Message(preferWgc
            ? "A minimised window now keeps its last picture on the TV."
            : Settings.ShowCursor ? "Back to automatic." : "Back to automatic, which uses the least memory.");
    }

    public void SetShowCursor(bool show)
    {
        Settings.ShowCursor = show;
        Save();
        _ = GuardAsync(ReshowAsync); // takes effect now, not from the next thing shown
        Message(show ? "Your mouse pointer shows on the TV." : "Your mouse pointer no longer shows on the TV.");
    }

    /// <summary>
    /// Shows what's shown again with a newly chosen engine (a quick fade), so a changed way of
    /// showing it applies at once. (Presenting the same thing again would keep the engine it has.)
    /// </summary>
    private Task ReshowAsync() => _session.RestartEngineAsync();

    private async Task PresentActiveWindowAsync()
    {
        if (WindowEnumerator.GetActiveWindow() is { } window) await PresentAsync(window);
        else Message("Click into the window you want to show, then press the shortcut again.");
    }

    /// <summary>
    /// FR-05: point at a window on your own screen and click it. The shortcut again (or Esc)
    /// cancels. The click layer and outline cover only the presenter's screens, never the TV.
    /// </summary>
    private async Task PickWindowAsync()
    {
        if (WindowPicker.Open is { } open)
        {
            open.Cancel(); // the shortcut toggles it
            return;
        }
        _picker?.Dismiss();
        List<DisplayMonitor> screens = PresenterScreens();
        if (screens.Count == 0) return;
        SourceWindow? chosen = await WindowPicker.PickAsync(screens, IsNeverShown);
        if (chosen is null || _quitting) return;
        await PresentAsync(chosen);
    }

    /// <summary>
    /// The screens that are the presenter's own: all of them except the TV (the screen the
    /// presentation fills, or will fill). With only one screen, that one.
    /// </summary>
    private List<DisplayMonitor> PresenterScreens()
    {
        DisplayMonitor? tv = _session.State != SessionState.Off ? _session.FullScreenOutput : ChosenPlacement is { IsFloating: false } p ? p.Monitor : null;
        if (_monitors.Count < 2 || tv is null) return [.. _monitors];
        return [.. _monitors.Where(m => !m.IsSameScreen(tv))];
    }

    private async Task PresentPreviousAsync()
    {
        if (_session.PreviousSource is { } previous && (previous.IsWindow ? WindowEnumerator.Refresh(previous) : previous) is { } alive)
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
        if (last.CameraId is { } cameraId)
        {
            if ((await Cameras.ListAsync()).FirstOrDefault(c => c.Id == cameraId) is not { } camera) return false;
            await PresentAsync(SourceWindow.ForCamera(camera));
            return true;
        }
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
        List<SourceWindow> windows = [.. WindowEnumerator.GetPresentableWindows().Where(w => !IsNeverShown(w))];
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
        try
        {
            await ShowFavouriteCoreAsync(slot);
        }
        finally
        {
            _pendingSection = null; // shown or not, its budget never sticks to whatever is shown next
        }
    }

    private async Task ShowFavouriteCoreAsync(int slot)
    {
        if (Settings.Favourites[slot - 1] is not { } favourite)
        {
            Message($"Favourite {slot} is empty. Show something, then save it from the tray menu under Favourites.");
            return;
        }
        _pendingSection = ($"favourite:{slot}", TrayMenuPlan.Shorten(favourite.Title.Length > 0 ? favourite.Title : favourite.AppName, 40), favourite.Minutes);
        if (favourite.PicturePath is not null || favourite.VideoPath is not null || favourite.DocumentPath is not null || favourite.Special is not null)
        {
            // A picture, a video or a PDF (FR-103, FR-115, FR-116): shown from its file.
            SourceWindow? file = favourite.PicturePath is { } picture ? PictureSource.Problem(picture) is null ? SourceWindow.ForPicture(picture, favourite.Title) : null : await ResolveFileAsync(favourite);
            if (file is null) Message($"Favourite {slot} (“{favourite.Title}”) can't be shown: its file can't be found.");
            else await PresentAsync(file);
            _pendingSection = null;
            return;
        }
        if (favourite.ScreenDevice is { } device)
        {
            if (_monitors.FirstOrDefault(m => m.DeviceName == device) is { } screen) await PresentAsync(SourceWindow.ForScreen(screen));
            else Message($"The screen saved as favourite {slot} isn't connected.");
            return;
        }
        if (favourite.CameraId is { } cameraId)
        {
            if ((await Cameras.ListAsync()).FirstOrDefault(c => c.Id == cameraId) is { } camera) await PresentAsync(SourceWindow.ForCamera(camera));
            else Message($"The camera saved as favourite {slot} (“{favourite.Title}”) isn't connected.");
            return;
        }
        SourceWindow? match = FavouriteMatcher.FindBest(favourite, WindowEnumerator.GetPresentableWindows());
        if (match is null) Message($"{favourite.AppName} isn't open, so favourite {slot} can't be shown.");
        else await PresentAsync(match);
        _pendingSection = null;
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
        if (_second is { } second) second.BlankMessage = Settings.BlankMessage;
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
        _studio?.Close();
        StopLiveCaptions(null);
        if (_second is { } second) await second.StopAsync();
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
                case "settings:privacy": OpenSettings(SettingsForm.Section.Privacy); return;
                case "settings:rooms": OpenSettings(SettingsForm.Section.Rooms); return;
                case "stop-second-tv": await StopSecondTvAsync(); return;
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
                case "stop-at": AskRecordingStopAt(); return;
            }
            if (command.StartsWith("timer:", StringComparison.Ordinal)
                && int.TryParse(command.AsSpan("timer:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int timerMinutes))
            {
                StartTimer(timerMinutes);
                return;
            }
            if (command.StartsWith("night:", StringComparison.Ordinal))
            {
                SetNightFilter(NightFilter.Parse(command["night:".Length..]));
                return;
            }
            if (command.StartsWith("inset-shape:", StringComparison.Ordinal))
            {
                SetInsetShape(InsetShapes.Parse(command["inset-shape:".Length..]));
                return;
            }
            if (command.StartsWith("countdown:", StringComparison.Ordinal)
                && int.TryParse(command.AsSpan("countdown:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int countdown))
            {
                await StartCountdownAsync(countdown);
                return;
            }
            if (command.StartsWith("stop-after:", StringComparison.Ordinal)
                && int.TryParse(command.AsSpan("stop-after:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minutes))
            {
                SetRecordingStopAfter(TimeSpan.FromMinutes(minutes));
                return;
            }
            if (command.StartsWith("playlist:", StringComparison.Ordinal))
            {
                await StartPlaylistAsync(command["playlist:".Length..]);
                return;
            }
            if (command.StartsWith("room:", StringComparison.Ordinal))
            {
                await ApplyRoomAsync(command["room:".Length..]);
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

    // ───────────────────────────── zoom, click highlights, pointer ring (FR-64, FR-65) ─────────────────────────────

    /// <summary>About 60 times a second while zoomed in or showing clicks or the pointer ring; stopped otherwise.</summary>
    private readonly System.Windows.Forms.Timer _pointerTimer = new() { Interval = 16 };

    /// <summary>The laser pointer is on (FR-71): a red dot with a fading trail stands in for the pointer on the TV.</summary>
    private bool _laserOn;

    public bool IsLaserOn => _laserOn;
    public bool IsDrawing => _drawing is not null;
    public bool IsZoomed => _zoom > 0;

    /// <summary>Drawing on the TV is on (FR-71); null otherwise.</summary>
    private InkDrawing? _drawing;
    private DrawingOverlay? _drawingOverlay;

    /// <summary>What was shown when drawing started: anything else shown ends the drawing.</summary>
    private SourceWindow? _drawingSource;

    /// <summary>0 when not zoomed in; otherwise how much closer (1.5–4).</summary>
    private double _zoom;

    /// <summary>What was shown (with its crop) when zooming in: anything else shown, or a new crop, ends it.</summary>
    private SourceWindow? _zoomSource;
    private PointF _zoomCentre;
    private (bool Primary, bool Secondary) _buttons;
    private PointF? _sentPointer;
    private PointF? _sentLaser;
    private long _outlineTicks;

    /// <summary>Marks on the TV are on, so what is shown must go through the helper, which can draw them.</summary>
    private bool MarksWanted => PointerWanted || StageWantsHelper || ExtrasWantHelper;

    /// <summary>Marks that follow the pointer, so the pointer check must run (about 60 times a second).</summary>
    private bool PointerWanted => Settings.HighlightClicks || Settings.PointerRing || _laserOn || _spotlightOn || _drawing is not null || _inset is not null;

    /// <summary>Runs the pointer check only while something needs it (NFR-04: no work while presenting plainly).</summary>
    private void UpdatePointerWork()
    {
        if (_zoom > 0 && (_session.State != SessionState.Presenting || _session.Source is not { } shown || _zoomSource is null
                          || !shown.IsSameSource(_zoomSource) || shown.Crop != _zoomSource.Crop))
        {
            _zoom = 0; // something else is shown, or the crop changed: zooming in ended
            _zoomSource = null;
        }
        if (_laserOn && _session.State == SessionState.Off) _laserOn = false; // stopped showing: the laser goes too
        UpdateCaretTracker();
        UpdateKeyWatch();
        bool want = !_quitting && _session.State == SessionState.Presenting && (_zoom > 0 || PointerWanted);
        if (want == _pointerTimer.Enabled) return;
        if (want)
        {
            _buttons = MouseButtonsNow(); // a button already held down isn't a click
            _pointerTimer.Start();
        }
        else
        {
            _pointerTimer.Stop();
            _sentPointer = null;
            _sentLaser = null;
        }
    }

    /// <summary>A new engine took over: it starts without marks, so tell it what should be on.</summary>
    private void OnEngineChanged()
    {
        _sentPointer = null;
        _sentLaser = null;
        if (_session.Effects is not { } effects) return;
        _pictureCursorSent = null;
        ApplyPictureCursor(); // a new engine starts with the pointer as the setting says: hide it if it shouldn't be there
        _drawing?.Replay(effects);
        effects.InsetFailed -= OnInsetFailed;
        effects.InsetFailed += OnInsetFailed;
        ReplaySplit(effects); // the other pictures' hidden parts first, then the panes
        if (_inset is { } inset) effects.Inset(inset, InsetLayout.ParseCorner(Settings.InsetCorner), Settings.InsetSize);
        ReplayStage(effects);
    }

    private static (bool Primary, bool Secondary) MouseButtonsNow()
    {
        bool left = (Windows.Win32.PInvoke.GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
        bool right = (Windows.Win32.PInvoke.GetAsyncKeyState((int)Keys.RButton) & 0x8000) != 0;
        return PointerInput.Buttons(left, right, SystemInformation.MouseButtonsSwapped);
    }

    /// <summary>
    /// Where the pointer is on what's shown, as a fraction of its whole area, and whether it is
    /// really over it (not over another window in front of it). Null when it can't be placed.
    /// </summary>
    private (PointF At, bool Over)? PointerOnShown(SourceWindow source)
    {
        if (source.IsOffScreen) return null; // its picture isn't on the presenter's screen
        Rectangle area = source.Screen is { } screen ? screen.Bounds
            : WindowEnumerator.IsMinimized(source.Handle) ? Rectangle.Empty : WindowEnumerator.ClientBoundsOnScreen(source.Handle);
        if (area.Width <= 0 || area.Height <= 0) return null;
        Point pointer = Cursor.Position;
        bool over = area.Contains(pointer) && (source.IsScreen || WindowEnumerator.RootAt(pointer) == source.Handle);
        return (PointerInput.Fraction(pointer, area), over);
    }

    private void OnPointerTick()
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { } source)
        {
            UpdatePointerWork();
            return;
        }
        (PointF At, bool Over)? pointer = PointerOnShown(source);
        bool frozen = _session.IsFrozen;

        // Zoomed in: the view follows the pointer while it's over what's shown, or where the
        // presenter types (FR-99); not while frozen: the TV holds still.
        if (_zoom > 0 && !frozen && _drawing is null && ZoomTarget(source, pointer) is { } p)
        {
            _zoomCentre = ZoomView.Ease(_zoomCentre, p);
            _session.SetView(ZoomView.View(_zoomCentre, _zoom, source.Crop));
            long now = Environment.TickCount64;
            if (now - _outlineTicks > 50)
            {
                _outlineTicks = now;
                UpdateOutline();
            }
        }

        (bool Primary, bool Secondary) buttons = MouseButtonsNow();
        (bool Primary, bool Secondary) pressed = PointerInput.Pressed(_buttons, buttons);
        _buttons = buttons;
        if (_session.Effects is not { } effects) return;

        // While frozen the presenter may be working privately: no clicks, ring or laser go to the TV.
        // While it's black (blank or held) there's nothing to mark, and the helper can rest.
        bool hidden = frozen || _session.IsBlanked || _session.IsHeld;
        PointF? at = !hidden && pointer is { Over: true } q ? q.At : null;
        if (Settings.HighlightClicks && at is { } click)
        {
            if (pressed.Primary) effects.Click(click, secondary: false);
            if (pressed.Secondary) effects.Click(click, secondary: true);
        }
        PointF? ring = Settings.PointerRing ? at : null;
        if (ring != _sentPointer)
        {
            _sentPointer = ring;
            effects.Pointer(ring);
        }
        PointF? laser = _laserOn ? at : null;
        if (laser != _sentLaser)
        {
            _sentLaser = laser;
            effects.Laser(laser);
        }
        SendSpotlight(effects, at);
    }

    /// <summary>Ctrl+Alt+Z: zoom in around the pointer, or back out. Through black, like a crop.</summary>
    private async Task ToggleZoomAsync()
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { } source)
        {
            Message("Show something first, then zoom in on it.");
            return;
        }
        if (_zoom > 0)
        {
            await ZoomOutAsync();
            return;
        }
        double level = ZoomView.Normalize(Settings.ZoomLevel);
        _zoomCentre = PointerOnShown(source) is { Over: true } p ? p.At : ZoomView.Middle(source.Crop);
        _zoom = level;
        _zoomSource = source;
        await _session.SetViewAsync(ZoomView.View(_zoomCentre, level, source.Crop));
        if (_session.View is null)
        {
            _zoom = 0;
            _zoomSource = null;
            return;
        }
        UpdatePointerWork();
        UpdateOutline();
        Message($"Zoomed in {level:0.#}×. The TV follows your pointer over what you show.", BadgeDot.Accent,
            Button("Zoom out", ZoomOutAsync), Button("Closer", ZoomMoreAsync, primary: false));
    }

    private async Task ZoomOutAsync()
    {
        if (_zoom <= 0) return;
        _zoom = 0;
        _zoomSource = null;
        await _session.SetViewAsync(null);
        UpdatePointerWork();
        UpdateOutline();
    }

    /// <summary>Ctrl+Alt+Shift+Z: zoom in, or closer still (1.5× → 2× → 3× → 4× → 1.5×). Remembered for next time.</summary>
    private async Task ZoomMoreAsync()
    {
        if (_zoom <= 0)
        {
            await ToggleZoomAsync();
            return;
        }
        if (_session.Source is not { } source) return;
        _zoom = ZoomView.Next(_zoom);
        Settings.ZoomLevel = _zoom;
        Save();
        _session.SetView(ZoomView.View(_zoomCentre, _zoom, source.Crop));
        UpdateOutline();
        _drawingOverlay?.UpdateGeometry(); // drawing: the layer covers the newly shown part
        Message($"Zoomed in {_zoom:0.#}×.");
    }

    /// <summary>
    /// The laser pointer (FR-71): a red dot with a short fading trail follows the mouse over what
    /// is shown, and the real pointer is hidden on the TV meanwhile. It stays on through switches,
    /// and goes off when showing stops.
    /// </summary>
    private void SetLaser(bool on)
    {
        if (on && _session.State != SessionState.Presenting)
        {
            Message("Show something first, then use the laser pointer.");
            return;
        }
        if (on == _laserOn) return;
        _laserOn = on;
        ApplyMarksChange();
        ApplyPictureCursor(force: true); // the laser stands in for the pointer while it's on
        if (!on)
        {
            _session.Effects?.Laser(null);
            _sentLaser = null;
        }
        Message(on ? "Laser pointer on: a red dot follows your mouse over what you show." : "Laser pointer off.", BadgeDot.Accent,
            Button(on ? "Turn it off" : "Turn it on", () => SetLaser(!on), primary: false));
    }

    /// <summary>
    /// Ctrl+Alt+Shift+D: draw on what is shown, for the TV (FR-71). A see-through layer covers the
    /// part the audience sees, on your own screen; lines appear on the TV as you draw. Done or Esc
    /// ends it and clears the drawing from the TV.
    /// </summary>
    private void ToggleDrawing()
    {
        if (_drawing is not null)
        {
            StopDrawing();
            return;
        }
        if (_session.State != SessionState.Presenting || _session.Source is not { } source)
        {
            Message("Show something first, then draw on it.");
            return;
        }
        if (source.IsOffScreen && !(source.IsBoard && BoardOut))
        {
            Message("Drawing works on windows, screens and the whiteboard: a camera's picture, a picture file or a video isn't on your screen to draw over.");
            return;
        }
        if (source.IsWindow && WindowEnumerator.IsMinimized(source.Handle))
        {
            Message($"Restore “{Shorten(source.Title, 40)}” first, then draw on it.");
            return;
        }
        if (DrawingGeometry() is null)
        {
            Message("Move the window onto your own screen first, then draw on it.");
            return;
        }
        if (_laserOn) SetLaser(false);
        _drawing = source.IsBoard ? _boardInk ??= new InkDrawing() : new InkDrawing(); // the board keeps its drawing between sessions
        _drawingSource = source;
        ApplyMarksChange(); // shown the light way: shown again through the helper, which can draw
        var overlay = new DrawingOverlay(_drawing, () => _session.Effects, DrawingGeometry, PresenterScreen()?.Scale ?? 1);
        overlay.Closed += (_, _) => Guard(StopDrawing);
        overlay.LaserChosen += (_, _) => Guard(() => SetLaser(true));
        _drawingOverlay = overlay;
        overlay.Show();
        if (_drawing is null) return; // it closed at once (nothing left to draw on)
        _board?.ShowDrawing(true);
        if (source.IsBoard) return; // the board says how it works itself
        Message($"Draw on what you show: the audience sees it as you draw. Press Esc or Done to stop{OrPress("draw")}; that clears the drawing.", BadgeDot.Accent);
    }

    /// <summary>
    /// Where to draw: the content's whole area on screen (what the lines are measured against) and
    /// the part of it the audience sees (crop or zoom), kept to the presenter's own screens.
    /// </summary>
    private (Rectangle Area, Rectangle Shown)? DrawingGeometry()
    {
        if (_session.State == SessionState.Presenting && _session.Source is { IsBoard: true })
            return _board?.BoardOnScreen is { Width: > 0, Height: > 0 } board ? (board, board) : null; // the whiteboard: its window is the board
        if (_session.State != SessionState.Presenting || _session.Source is not { } source || source.IsOffScreen) return null;
        Rectangle area = source.Screen is { } screen ? screen.Bounds
            : WindowEnumerator.IsAlive(source.Handle) && !WindowEnumerator.IsMinimized(source.Handle) ? WindowEnumerator.ClientBoundsOnScreen(source.Handle) : Rectangle.Empty;
        if (area.Width <= 0 || area.Height <= 0) return null;
        Rectangle shown = (_session.View ?? source.Crop)?.ToScreen(area) ?? area;
        Rectangle onMine = PresenterScreens().Select(m => Rectangle.Intersect(m.Bounds, shown)).OrderByDescending(r => (long)r.Width * r.Height).FirstOrDefault();
        return onMine.Width > 0 && onMine.Height > 0 ? (area, onMine) : null;
    }

    /// <summary>Something else shown, stopped or minimised: the drawing ends. Moved or resized: the layer follows.</summary>
    private void UpdateDrawing()
    {
        if (_drawing is null) return;
        if (_session.State != SessionState.Presenting || _session.Source is not { } source || !source.IsSameSource(_drawingSource)
            || source.IsWindow && WindowEnumerator.IsMinimized(source.Handle))
        {
            StopDrawing();
            return;
        }
        _drawingOverlay?.UpdateGeometry();
    }

    private void StopDrawing()
    {
        if (_drawing is null) return;
        bool board = _boardInk is not null && ReferenceEquals(_drawing, _boardInk) && _session.Source is { IsBoard: true };
        _drawing = null;
        _drawingSource = null;
        DrawingOverlay? overlay = _drawingOverlay;
        _drawingOverlay = null;
        overlay?.Close();
        overlay?.Dispose();
        if (board) _board?.ShowDrawing(false); // the board keeps its drawing on the TV until it's cleared or put away
        else _session.Effects?.InkClear(); // the drawing goes from the TV too
        UpdatePointerWork();
    }

    /// <summary>Settings › Where it shows › Zoom.</summary>
    public void SetZoomLevel(double level)
    {
        Settings.ZoomLevel = ZoomView.Normalize(level);
        Save();
        if (_zoom > 0 && _session.Source is { } source)
        {
            _zoom = Settings.ZoomLevel;
            _session.SetView(ZoomView.View(_zoomCentre, _zoom, source.Crop));
            UpdateOutline();
            _drawingOverlay?.UpdateGeometry();
        }
    }

    public void SetHighlightClicks(bool on)
    {
        Settings.HighlightClicks = on;
        Save();
        ApplyMarksChange();
        Message(on ? "Your clicks show as rings on the TV." : "Clicks no longer show on the TV.", BadgeDot.Accent,
            Button("Undo", () => SetHighlightClicks(!on), primary: false));
    }

    public void SetPointerRing(bool on)
    {
        Settings.PointerRing = on;
        Save();
        ApplyMarksChange();
        Message(on ? "A ring follows your pointer on the TV, so the room can see it." : "The ring around your pointer is off.", BadgeDot.Accent,
            Button("Undo", () => SetPointerRing(!on), primary: false));
    }

    /// <summary>
    /// Marks were switched on or off: shown with the light way (which can't draw them), it is shown
    /// again through the helper, with a quick fade. The pointer check starts or stops.
    /// </summary>
    private void ApplyMarksChange()
    {
        if (MarksWanted && _session.State == SessionState.Presenting && _session.Effects is null && !_session.IsFrozen) _ = GuardAsync(ReshowAsync);
        UpdatePointerWork();
    }

    // ───────────────────────────── countdown and talk timer (FR-66, FR-67) ─────────────────────────────

    /// <summary>Four times a second while something is shown and a countdown or the talk timer needs it.</summary>
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 250 };
    private readonly TalkTimer _talk = new();
    private DateTime? _countdownEnd;
    private bool _toldBreakOver;
    private string? _lastTalkText;

    /// <summary>The talk timer starts with showing and resets when it stops; the clock runs only while needed.</summary>
    private void UpdateClock()
    {
        if (_session.State == SessionState.Off)
        {
            _talk.Reset();
            StopCountdown();
        }
        else _talk.Start(DateTime.UtcNow);
        bool want = !_quitting && _session.State != SessionState.Off && (_countdownEnd is not null || Settings.ShowTalkTimer || Settings.TalkMinutes > 0);
        if (want == _clock.Enabled) return;
        if (want) _clock.Start();
        else _clock.Stop();
    }

    private void OnClockTick()
    {
        DateTime now = DateTime.UtcNow;
        if (_countdownEnd is { } end) TickCountdown(now, end);
        switch (_talk.Check(now, Settings.TalkMinutes))
        {
            case TalkAlert.TwoMinutesLeft:
                Message("Two minutes left in your talk.");
                break;
            case TalkAlert.TimeUp:
                Message($"Time's up: you've talked for {Settings.TalkMinutes} minutes.", BadgeDot.Warning, Button("Restart the timer", RestartTalkTimer, primary: false));
                break;
        }
        // The bar shows the talk time and the countdown: redraw it when either has moved on a second.
        string talk = TalkText() + (_countdownEnd is { } shownEnd ? "|" + BlankCountdown.Clock(shownEnd - now) : "");
        if (talk != _lastTalkText)
        {
            _lastTalkText = talk;
            UpdateWidget(); // once a second: only the bar on the presenter's screen changes
        }
    }

    /// <summary>"12:04" or "12:04 of 20:00" for the now-showing bar; empty when switched off or not showing.</summary>
    private string TalkText()
    {
        string talk = Settings.ShowTalkTimer && _session.State != SessionState.Off && _talk.Started is not null ? _talk.Text(DateTime.UtcNow, Settings.TalkMinutes) : "";
        string section = SectionText(); // FR-121: the part shown, against its budget
        return section.Length == 0 ? talk : talk.Length == 0 ? section : $"{talk} · {section}";
    }

    private void RestartTalkTimer()
    {
        if (_session.State == SessionState.Off)
        {
            Message("The talk timer starts when you show something.");
            return;
        }
        _talk.Reset();
        _talk.Start(DateTime.UtcNow);
        _sections.Reset(); // the parts' times start again too
        if (_session.Source is { } shown) _sections.Show(CaptureProtocol.CoverKey(shown), SourceName(shown), TimeSpan.Zero, DateTime.UtcNow);
        _lastTalkText = null;
        Message("The talk timer starts again from 0:00.");
    }

    /// <summary>
    /// Blank with a countdown (FR-66): the TV shows the blank message and the time left. At the end
    /// it says the break is over but stays blank, because what is shown may have changed during
    /// the break: only the presenter brings it back.
    /// </summary>
    private async Task StartCountdownAsync(int minutes)
    {
        if (_session.State == SessionState.Off)
        {
            Message("Show something first, then blank it with a countdown.");
            return;
        }
        minutes = BlankCountdown.Normalize(minutes);
        TimeSpan length = TimeSpan.FromMinutes(minutes);
        _countdownEnd = DateTime.UtcNow + length;
        _toldBreakOver = false;
        await _session.BlankWithMessageAsync(BlankCountdown.Text(Settings.BlankMessage, length));
        if (!_session.IsBlanked)
        {
            _countdownEnd = null;
            return;
        }
        UpdateClock();
        UpdateWidget();
        Message($"The TV counts down {minutes} minute{(minutes == 1 ? "" : "s")}. When it ends, it stays blank until you show it again.", BadgeDot.Accent,
            Button("Show again now", () => _ = RunAsync("blank"), primary: false));
    }

    private void TickCountdown(DateTime now, DateTime end)
    {
        if (!_session.IsBlanked || _session.State == SessionState.Off)
        {
            StopCountdown(); // shown again, stopped, or the window closed
            return;
        }
        TimeSpan left = end - now;
        _session.UpdateIdleMessage(BlankCountdown.Text(Settings.BlankMessage, left));
        if (left <= TimeSpan.Zero && !_toldBreakOver)
        {
            _toldBreakOver = true;
            Message("The break is over. The TV stays blank until you show it again.", BadgeDot.Accent, Button("Show again", () => _ = RunAsync("blank")));
            UpdateWidget();
        }
    }

    private void StopCountdown()
    {
        if (_countdownEnd is null) return;
        _countdownEnd = null;
        // A blank screen left behind (the window closed meanwhile) keeps its message, without a time that no longer counts.
        _session.UpdateIdleMessage(Settings.BlankMessage);
        UpdateWidget();
    }

    /// <summary>Settings › Where it shows › Countdown.</summary>
    public void SetCountdownMinutes(int minutes)
    {
        Settings.CountdownMinutes = BlankCountdown.Normalize(minutes);
        Save();
    }

    /// <summary>Settings › General › Talk timer.</summary>
    public void SetShowTalkTimer(bool show)
    {
        Settings.ShowTalkTimer = show;
        Save();
        _lastTalkText = null;
        UpdateClock();
        UpdateWidget();
    }

    /// <summary>Settings › General › Talk length (0 = none).</summary>
    public void SetTalkMinutes(int minutes)
    {
        Settings.TalkMinutes = TalkTimer.Normalize(minutes);
        Save();
        UpdateClock();
    }

    // ───────────────────────────── picture-in-picture (FR-72) ─────────────────────────────

    /// <summary>What the small picture in a corner of the TV shows; null when there's none.</summary>
    private SourceWindow? _inset;

    public bool HasInset => _inset is not null;

    /// <summary>The "small picture" action: takes it off, or opens the picker to choose one.</summary>
    private void ToggleInset()
    {
        if (_inset is not null)
        {
            RemoveInset("The small picture is off.");
            return;
        }
        if (_session.State != SessionState.Presenting)
        {
            Message("Show something first, then add a small picture to it, like you on a webcam.");
            return;
        }
        OpenPicker(PickerMode.Inset);
    }

    /// <summary>Shows <paramref name="source"/> small in a corner of the TV, over what's shown.</summary>
    public async Task SetInsetAsync(SourceWindow source)
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { } main)
        {
            Message("Show something first, then add a small picture to it.");
            return;
        }
        if (source.IsSameSource(main))
        {
            Message("That's already the main picture. Choose something else to show small.");
            return;
        }
        if (IsNeverShown(source))
        {
            Message($"“{source.AppName}” is on your never-show list, so it isn't shown.", BadgeDot.Warning);
            return;
        }
        if (source.Screen is { } screen && _session.FullScreenOutput is { } tv && screen.IsSameScreen(tv))
        {
            Message($"{screen.Name} is where your presentation is shown, so it can't be shown small on itself.");
            return;
        }
        if (source.IsWindow && !WindowEnumerator.IsAlive(source.Handle))
        {
            Message($"“{Shorten(source.Title, 40)}” has closed.");
            return;
        }
        if (_split.Count > 0) StopSplit(null); // one or the other
        if (LeaveOutTaskbar(source) is not { } clear) return; // FR-112: a screen shown small without its taskbar
        source = clear;
        _inset = source;
        SendOtherHiddenParts(); // its hidden parts are covered from its first frame
        UpdatePrivateAppGuard(); // a never-show app on a screen shown small: black before the small picture is sent
        if (_session.Effects is null) await ReshowAsync(); // shown the light way: again through the helper, which sends the small picture with it
        else _session.Effects.Inset(source, InsetLayout.ParseCorner(Settings.InsetCorner), Settings.InsetSize);
        if (_qrText is { } qrNow) _session.Effects?.Qr(qrNow, QrCorner()); // the QR code moves out of the small picture's corner
        UpdatePointerWork();
        UpdateWidget();
        string name = source.Screen is { } s ? s.Name : Shorten(source.Title.Length > 0 ? source.Title : source.AppName, 40);
        Message($"“{name}” shows small in the {InsetLayout.Words(InsetLayout.ParseCorner(Settings.InsetCorner))} corner.", BadgeDot.Accent,
            Button("Take it off", () => RemoveInset(null), primary: false));
    }

    private void RemoveInset(string? why)
    {
        if (_inset is null) return;
        _inset = null;
        _session.Effects?.Inset(null, InsetCorner.BottomRight, Settings.InsetSize);
        if (_qrText is { } qrNow) _session.Effects?.Qr(qrNow, QrCorner());
        UpdatePrivateAppGuard();
        UpdatePointerWork();
        UpdateWidget();
        if (why is not null) Message(why);
    }

    /// <summary>The small picture's window closed or its camera was unplugged: it's already off in the helper.</summary>
    private void OnInsetFailed(object? sender, string reason)
    {
        if (!ReferenceEquals(sender, _session.Effects)) return;
        if (_inset is null)
        {
            if (_split.Count > 0) OnPaneFailed(reason);
            return;
        }
        _inset = null;
        UpdatePrivateAppGuard();
        UpdateWidget();
        Message(reason, BadgeDot.Warning);
    }

    /// <summary>Stopped showing: the small picture goes. What it showed became the main picture: it goes too.</summary>
    private void UpdateInset()
    {
        if (_inset is null) return;
        if (_session.State != SessionState.Presenting) RemoveInset(null); // stopped, or the shown window closed: it goes with the picture
        else if (_session.Source is { } main && main.IsSameSource(_inset)) RemoveInset("That's the main picture now, so the small one is off.");
    }

    /// <summary>Settings › Where it shows › Small picture.</summary>
    public void SetInsetPlace(InsetCorner corner, int percent)
    {
        Settings.InsetCorner = corner.ToString();
        Settings.InsetSize = InsetLayout.Normalize(percent);
        Save();
        if (_inset is { } inset) _session.Effects?.Inset(inset, corner, Settings.InsetSize);
        if (_qrText is { } qrNow) _session.Effects?.Qr(qrNow, QrCorner());
    }

    // ───────────────────────────── slideshow of favourites (FR-69) ─────────────────────────────

    private readonly System.Windows.Forms.Timer _slideshowTimer = new();

    /// <summary>The favourite the slideshow is showing; null when it's off.</summary>
    private int? _slideshowSlot;

    /// <summary>The slideshow itself is choosing what to show (so that doesn't count as the presenter choosing).</summary>
    private bool _slideshowSwitching;
    private bool _slideshowTicking;

    public bool SlideshowOn => _slideshowSlot is not null;

    /// <summary>Ctrl+Alt+L: play the favourites one after another, or stop.</summary>
    private async Task ToggleSlideshowAsync()
    {
        if (SlideshowOn)
        {
            StopSlideshow("Slideshow stopped.");
            return;
        }
        int? first = Slideshow.Next(Settings.Favourites, null, await FavouriteAvailabilityAsync());
        if (first is not { } slot)
        {
            Message(Settings.Favourites.Any(f => f is not null)
                ? "None of your favourites can be shown right now. Open them first, then try again."
                : "Save a few favourites first (show something, then save it from the tray menu under Favourites), then play them one after another.");
            return;
        }
        await ShowSlideAsync(slot);
        if (!SlideshowOn) return;
        Message($"Slideshow: your favourites one after another, {Slideshow.Words(Settings.SlideshowSeconds)} each. Choosing something else stops it.", BadgeDot.Accent,
            Button("Stop the slideshow", () => StopSlideshow("Slideshow stopped."), primary: false));
    }

    /// <summary>Which favourites can be shown now: the app open (and not never-shown), the screen connected and not the TV, the camera plugged in.</summary>
    private async Task<Func<Favourite, bool>> FavouriteAvailabilityAsync()
    {
        List<SourceWindow> windows = WindowEnumerator.GetPresentableWindows();
        List<CameraDevice> cameras = Settings.Favourites.Any(f => f?.CameraId is not null) ? await Cameras.ListAsync() : [];
        DisplayMonitor? tv = _session.FullScreenOutput ?? (ChosenPlacement is { IsFloating: false } p && _monitors.Count > 1 ? p.Monitor : null);
        return f => f.ScreenDevice is { } device ? _monitors.Any(m => m.DeviceName == device) && tv?.DeviceName != device
            : f.CameraId is { } id ? cameras.Any(c => c.Id == id)
            : FavouriteMatcher.FindBest(f, windows) is { } w && !IsNeverShown(w);
    }

    private async Task ShowSlideAsync(int slot)
    {
        _slideshowSwitching = true;
        try
        {
            await ShowFavouriteAsync(slot);
        }
        finally
        {
            _slideshowSwitching = false;
        }
        if (_session.State != SessionState.Presenting)
        {
            StopSlideshow(null);
            return;
        }
        _slideshowSlot = slot;
        _slideshowTimer.Stop();
        _slideshowTimer.Interval = Settings.SlideshowSeconds * 1000;
        _slideshowTimer.Start();
        UpdateWidget();
    }

    private async Task OnSlideshowTickAsync()
    {
        if (!SlideshowOn || _slideshowTicking) return;
        // Blank, frozen or held black: the slideshow waits, so it never changes what's behind the black.
        if (_session.State != SessionState.Presenting || _session.IsBlanked || _session.IsFrozen || _session.IsHeld) return;
        _slideshowTicking = true;
        try
        {
            int? next = Slideshow.Next(Settings.Favourites, _slideshowSlot, await FavouriteAvailabilityAsync());
            if (!SlideshowOn) return;
            if (next is not { } slot)
            {
                StopSlideshow("None of your favourites can be shown any more, so the slideshow stopped.");
                return;
            }
            if (slot == _slideshowSlot) return; // only one can be shown: it stays
            await ShowSlideAsync(slot);
        }
        finally
        {
            _slideshowTicking = false;
        }
    }

    private void StopSlideshow(string? why)
    {
        if (!SlideshowOn) return;
        _slideshowSlot = null;
        _slideshowTimer.Stop();
        if (why is not null) Message(why);
        UpdateWidget();
    }

    /// <summary>Settings › Where it shows › Slideshow.</summary>
    public void SetSlideshowSeconds(int seconds)
    {
        Settings.SlideshowSeconds = Slideshow.Normalize(seconds);
        Save();
        if (SlideshowOn)
        {
            _slideshowTimer.Interval = Settings.SlideshowSeconds * 1000;
        }
    }

    // ───────────────────────────── update check (FR-73) ─────────────────────────────

    /// <summary>The first look 30 seconds after start (never slowing it), then every few hours whether a day has passed.</summary>
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private bool _checkingForUpdates;

    private void ScheduleUpdateCheck()
    {
        _updateTimer.Stop();
        if (!Settings.CheckForUpdates || _quitting) return;
        _updateTimer.Interval = 30_000;
        _updateTimer.Start();
    }

    private async Task OnUpdateTimerAsync()
    {
        _updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        if (Settings.CheckForUpdates && UpdateCheck.IsDue(Settings.LastUpdateCheckUtc, DateTime.UtcNow)) await CheckForUpdatesAsync(manual: false);
    }

    /// <summary>
    /// Asks (through the helper) whether a newer version is out, and says so with a link to its
    /// release page. Asked by hand, it also says when it's up to date or couldn't tell; a version
    /// skipped earlier is told about again then.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates)
        {
            if (manual) Message("Already checking for updates…");
            return;
        }
        _checkingForUpdates = true;
        UpdateResult result;
        try
        {
            result = await RunUpdateHelperAsync();
        }
        finally
        {
            _checkingForUpdates = false;
        }
        Settings.LastUpdateCheckUtc = DateTime.UtcNow;
        Save();
        Diag($"update check: {UpdateCheck.Reply(result)}");
        Version current = typeof(ScpApp).Assembly.GetName().Version ?? new Version(1, 0, 0);
        if (result.Version is { } latest && UpdateCheck.IsNewer(latest, current, manual ? null : Settings.SkippedVersion) && UpdateCheck.ReleasePage(result.Tag) is { } page)
        {
            string tag = result.Tag!;
            Message($"ScreenCast Presenter {tag} is out (you have {current.ToString(3)}).", BadgeDot.Accent,
                Button("See what's new", () => ShellOpen(page)), Button("Skip this version", () => SkipVersion(tag), primary: false));
            return;
        }
        if (manual)
            Message(result.Problem is { } problem ? $"Couldn't check for updates. {problem}" : $"You have the latest version ({current.ToString(3)}).",
                result.Problem is null ? BadgeDot.Accent : BadgeDot.Warning);
    }

    private void SkipVersion(string tag)
    {
        Settings.SkippedVersion = tag;
        Save();
        Message($"You won't be told about {tag} again. A newer version will still be announced.");
    }

    /// <summary>The helper does the asking (the app itself never touches the network); one line comes back.</summary>
    private static async Task<UpdateResult> RunUpdateHelperAsync()
    {
        try
        {
            var info = new ProcessStartInfo(Exe, UpdateCheck.Switch)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            };
            using Process? helper = Process.Start(info);
            if (helper is null) return new UpdateResult(null, null, "The check couldn't start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                string? line = await helper.StandardOutput.ReadLineAsync(timeout.Token);
                await helper.WaitForExitAsync(timeout.Token);
                return UpdateCheck.ParseReply(line);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    helper.Kill();
                }
                catch (InvalidOperationException)
                {
                    // it ended just now
                }
                return new UpdateResult(null, null, "GitHub took too long to answer.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine(ex);
            return new UpdateResult(null, null, "The check couldn't start.");
        }
    }

    /// <summary>Settings › Advanced › Check for updates. Switched on, it looks straight away and says what it found.</summary>
    public void SetCheckForUpdates(bool on)
    {
        if (on && Blocked(_policy.UpdateCheckAllowed, "checking for updates")) return;
        Settings.CheckForUpdates = on;
        Save();
        ScheduleUpdateCheck();
        if (on) _ = GuardAsync(() => CheckForUpdatesAsync(manual: true));
    }

    // ───────────────────────────── a TV plugged in (FR-70) ─────────────────────────────

    private readonly ScreenArrivals _arrivals;

    /// <summary>
    /// A TV or projector was just plugged in and nothing is shown: offer to show the last thing on
    /// it (or to choose something). Not for a Zoom screen, not while set to show in a window (that
    /// has its own offer), and not while a copying TV is being made separate (that says so itself).
    /// </summary>
    private void OfferArrivedScreen(List<string> arrived)
    {
        if (arrived.Count == 0 || !Settings.OfferNewScreen || _quitting || _session.State != SessionState.Off || Settings.OutputInWindow || _forcingExtend is not null) return;
        if (_monitors.Where(m => arrived.Contains(m.DeviceName, StringComparer.OrdinalIgnoreCase) && !m.IsVirtual && !m.IsPrimary)
                .OrderBy(m => m.IsTv ? 0 : 1).FirstOrDefault() is not { } screen) return;
        string what = screen.IsTv ? "A TV" : "A screen";
        if (Settings.LastSource is { Title.Length: > 0 } last)
        {
            Message($"{what} is connected ({screen.Name}). Show “{Shorten(last.Title, 40)}” on it?", BadgeDot.Accent,
                Button("Show it there", () => ShowOnArrivedScreenAsync(screen, again: true)),
                Button("Choose something else", () => ShowOnArrivedScreenAsync(screen, again: false), primary: false));
            return;
        }
        Message($"{what} is connected ({screen.Name}). Show something on it?", BadgeDot.Accent,
            Button("Choose what to show", () => ShowOnArrivedScreenAsync(screen, again: false)));
    }

    private async Task ShowOnArrivedScreenAsync(DisplayMonitor screen, bool again)
    {
        if (_monitors.FirstOrDefault(m => m.IsSameScreen(screen)) is not { } connected)
        {
            Message("That screen isn't connected any more.");
            return;
        }
        await SetOutputMonitorAsync(connected, quiet: true);
        if (again && await PresentLastShownAsync()) return;
        OpenPicker();
    }

    /// <summary>Settings › Where it shows › When a TV is plugged in.</summary>
    public void SetOfferNewScreen(bool offer)
    {
        Settings.OfferNewScreen = offer;
        Save();
    }

    // ───────────────────────────── never-show apps ─────────────────────────────

    private readonly WindowChangeWatcher _windowChanges = new();

    /// <summary>The never-show app holding a whole screen black right now (named in messages).</summary>
    private string? _privateAppShown;

    /// <summary>A window of an app on the never-show list (a whole screen is checked as it's shown instead).</summary>
    public bool IsNeverShown(SourceWindow source) => source.IsWindow && NeverShow.IsBlocked(Settings.NeverShowApps, source.AppName);

    /// <summary>The area of the screen the audience sees, for a whole-screen source; null for a window.</summary>
    private static Rectangle? ShownScreenArea(SourceWindow source) =>
        source.Screen is { } screen ? source.Crop?.ToScreen(screen.Bounds) ?? screen.Bounds : null;

    /// <summary>A never-show app's window on the part of a screen being shown: the main picture, or a whole screen shown small (FR-72).</summary>
    private VisibleWindow? PrivateAppOn(SourceWindow source)
    {
        Rectangle? main = ShownScreenArea(source), small = _inset is { } inset ? ShownScreenArea(inset) : null;
        List<Rectangle> beside = [.. SplitScreenAreas()];
        if (main is null && small is null && beside.Count == 0 || Settings.NeverShowApps.Count == 0) return null;
        // Names are looked up afresh each time: Windows reuses process ids, so a cached name could
        // belong to a closed app while a never-show app now has its id.
        List<VisibleWindow> windows = WindowEnumerator.GetVisibleWindows([]);
        return (main is { } m ? NeverShow.FindOnScreen(windows, m, Settings.NeverShowApps) : null)
            ?? (small is { } s ? NeverShow.FindOnScreen(windows, s, Settings.NeverShowApps) : null)
            ?? beside.Select(area => NeverShow.FindOnScreen(windows, area, Settings.NeverShowApps)).FirstOrDefault(found => found is not null);
    }

    /// <summary>
    /// While a whole screen is shown, watch for never-show apps on it: the picture cuts to black the
    /// moment one appears there and fades back once it's minimised, closed or moved away.
    /// </summary>
    private void UpdatePrivateAppGuard()
    {
        UpdateDesktopIcons(); // a screen shown small or beside counts too (FR-113)
        bool watch = !_quitting && (Settings.NeverShowApps.Count > 0 || Settings.HoldForFileBoxes)
            && (_session.State == SessionState.Presenting && (_session.Source is { IsScreen: true } || _inset is { IsScreen: true } || _split.Any(s => s.IsScreen))
                || _second is { State: SessionState.Presenting, Source.IsScreen: true });
        if (!watch)
        {
            _windowChanges.Stop();
            _privateAppShown = null;
            _fileBoxShown = null;
            if (_session.IsHeldFor(HoldReason.PrivateApp)) _ = _session.ComeBackAsync(HoldReason.PrivateApp);
            if (_session.IsHeldFor(HoldReason.FileBox)) _ = _session.ComeBackAsync(HoldReason.FileBox);
            if (_second?.IsHeldFor(HoldReason.FileBox) == true) _ = _second.ComeBackAsync(HoldReason.FileBox);
            return;
        }
        _windowChanges.Start();
        CheckPrivateApps();
    }

    private void CheckPrivateApps()
    {
        CheckSecondPrivateApps();
        CheckSecondFileBoxes();
        if (_session.State != SessionState.Presenting || _session.Source is not { } source) return;
        CheckFileBoxes(source);
        string screenName = (source.Screen ?? _inset?.Screen)?.Name ?? "the screen";
        if (PrivateAppOn(source) is { } found)
        {
            if (!_session.IsHeldFor(HoldReason.PrivateApp)) _session.CutAway(HoldReason.PrivateApp);
            if (!string.Equals(_privateAppShown, found.AppName, StringComparison.OrdinalIgnoreCase))
            {
                _privateAppShown = found.AppName;
                Message($"“{found.AppName}” is open on {screenName}, so the audience sees black. Minimise it or move it to another screen.", BadgeDot.Warning,
                    Button("Change the list", () => OpenSettings(SettingsForm.Section.Privacy), primary: false));
                UpdateBadge(reveal: false);
                UpdateWidget();
            }
            return;
        }
        _privateAppShown = null;
        if (_session.IsHeldFor(HoldReason.PrivateApp)) _ = _session.ComeBackAsync(HoldReason.PrivateApp);
    }

    /// <summary>Settings › Privacy: add an app (by program name) to the never-show list.</summary>
    public void AddNeverShow(string appName)
    {
        List<string> next = NeverShow.Normalize([.. Settings.NeverShowApps, appName]);
        if (next.Count == Settings.NeverShowApps.Count) return;
        Settings.NeverShowApps = next;
        Save();
        Message($"“{next[^1]}” is never shown now.");
        EnforceNeverShow();
    }

    /// <summary>The list just grew: whatever it now covers stops at once, shown big or small.</summary>
    private void EnforceNeverShow()
    {
        if (_inset is { } small && IsNeverShown(small)) RemoveInset($"“{small.AppName}” is on your never-show list, so the small picture is off.");
        EnforceNeverShowInSplit();
        if (_session.State == SessionState.Presenting && _session.Source is { } shown && IsNeverShown(shown)) _ = GuardAsync(StopShowingAsync);
        UpdatePrivateAppGuard();
    }

    public void RemoveNeverShow(string appName)
    {
        if (_policy.KeepsNeverShow(appName))
        {
            Message($"Your organisation keeps “{appName}” on the never-show list.", BadgeDot.Warning);
            return;
        }
        if (Settings.NeverShowApps.RemoveAll(a => string.Equals(a, appName, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Save();
        UpdatePrivateAppGuard();
    }

    /// <summary>Puts the usual password managers back on the list, keeping the user's own additions.</summary>
    public void RestoreNeverShowDefaults()
    {
        Settings.NeverShowApps = NeverShow.Normalize([.. Settings.NeverShowApps, .. NeverShow.Defaults]);
        Save();
        EnforceNeverShow();
    }

    // ───────────────────────────── notifications: Do not disturb ─────────────────────────────

    private bool _remindedDoNotDisturb;

    /// <summary>
    /// Pop-up notifications appear on the main screen, so showing or recording all of it would
    /// include them. Windows doesn't let apps switch on Do not disturb, so say so once per run,
    /// with a button to the setting, and only when Windows says pop-ups would show now.
    /// </summary>
    private void MaybeRemindDoNotDisturb(DisplayMonitor screen)
    {
        if (!Settings.RemindDoNotDisturb || _remindedDoNotDisturb || !screen.IsPrimary) return;
        bool privatePreview = _session.IsPrivatePreview && _session.Source?.Screen?.IsSameScreen(screen) == true; // nobody else sees it
        if (!NotificationReminder.ShouldRemind(true, false, NotificationReminder.PopUpsWouldShow(), true, privatePreview)) return;
        _remindedDoNotDisturb = true;
        string uri = NotificationReminder.SettingsUri(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
        Message($"Windows notifications can pop up on {screen.Name} while all of it is shown. Turn on Do not disturb so they don't.", BadgeDot.Accent,
            Button("Open Do not disturb", () => ShellOpen(uri)), Button("Don't remind me", () => SetRemindDoNotDisturb(false), primary: false));
    }

    /// <summary>Settings › Privacy › Notifications.</summary>
    public void SetRemindDoNotDisturb(bool remind)
    {
        Settings.RemindDoNotDisturb = remind;
        Save();
        if (!remind) Message("You won't be reminded about notifications. Switch it back on in Settings › Privacy.");
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
            Message("The preview stays behind the window you're working in, so it never covers your work. Click it to bring it to the front. Meetings can't see it.");
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
        Zoomed = IsZoomed,
        Drawing = IsDrawing,
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
        UpdatePrivateAppGuard();
        UpdateDrawing();
        UpdateInset();
        UpdateSplit();
        NoteShown();
        UpdateReplay();
        UpdateVoice();
        UpdateNotes();
        UpdatePointerWork();
        UpdateClock();
        UpdateStage();
        if (_session.State == SessionState.Off) StopSlideshow(null);
        if (_session.State == SessionState.Presenting && _session.Source?.Screen is { } shownScreen) MaybeRemindDoNotDisturb(shownScreen);
        UpdateExtras();
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
            SessionState.Presenting when s.IsHeldFor(HoldReason.PrivateApp) => new BadgeContent(BadgeDot.Warning, $"“{_privateAppShown ?? "A never-show app"}” is open here",
                "Your audience sees black until you minimise it or move it away", [stop]),
            SessionState.Presenting when s.IsHeldFor(HoldReason.FileBox) => new BadgeContent(BadgeDot.Accent, "An Open or Save box is open here",
                "Your audience sees black until it closes or moves away", [stop]),
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
            CropRegion? part = s.View ?? source.Crop; // zoomed in: exactly the part on the TV
            if (source.Screen is { } screen) bounds = part?.ToScreen(screen.Bounds) ?? screen.Bounds;
            else if (WindowEnumerator.IsAlive(source.Handle))
                bounds = part is { } crop ? crop.ToScreen(WindowEnumerator.ClientBoundsOnScreen(source.Handle)) : DwmHelpers.GetVisibleBounds((Windows.Win32.Foundation.HWND)source.Handle);
        }
        // Around a window, the outline sits just above it, so other windows in front cover it too.
        _outline.Update(bounds, ShowingText(s.IsFrozen), _monitors, s.FullScreenOutput, s.Source is { IsScreen: false } window ? window.Handle : 0);
        UpdateSecondOutline();
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
        _commandMessages?.Add(text);
        _remoteMessages?.Add(text);
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
        if (!source.IsWindow)
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
            NotePlaylistBeforeScreensChange();
            _monitors = DisplayHelpers.GetMonitors();
            List<string> arrived = _arrivals.Update(_monitors.Select(m => m.DeviceName), DateTime.UtcNow);
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
            await RefreshTaskbarCropsAsync(); // a screen's taskbar may have moved with it
            OnSessionChanged(reveal: false);
            await OfferNewScreenAsync(); // an HDMI cable plugged in while set to show in a window
            OfferArrivedScreen(arrived);
            await ResumePlaylistAsync(); // a lobby screen carries on when its TV is back
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
        IsDisplayDuplicated = duplicated;
        // Privacy first, enforced: a TV that copies the screen is made a separate screen straight
        // away, at start-up, when it's plugged in, or whenever Windows is switched back to Duplicate.
        // If Windows refuses, try again after a while rather than over and over.
        if (duplicated && _forcingExtend is null && DateTime.UtcNow - _extendFailedUtc > TimeSpan.FromSeconds(30))
            _ = GuardAsync(ForceTvSeparateAsync);
    }

    private Task<bool>? _forcingExtend;
    private DateTime _extendFailedUtc = DateTime.MinValue;

    /// <summary>
    /// Before anything is shown: the TV must not be copying the presenter's screen (Windows'
    /// "Duplicate"), because then it shows every window whatever the app does. It's switched to
    /// "Extend" without asking; if that fails, nothing is shown. False when showing must not go ahead.
    /// </summary>
    private async Task<bool> EnsureTvIsSeparateAsync()
    {
        CheckDuplicateDisplay();
        if (_forcingExtend is { } running) return await running;
        if (!IsDisplayDuplicated) return true;
        return await ForceTvSeparateAsync(); // an earlier attempt failed: the presenter wants to show something, so try again now
    }

    /// <summary>One switch to "Extend" at a time; everyone waiting gets its result.</summary>
    private async Task<bool> ForceTvSeparateAsync()
    {
        if (_forcingExtend is { } running) return await running;
        var done = new TaskCompletionSource<bool>();
        _forcingExtend = done.Task;
        bool separate = false;
        try
        {
            separate = await MakeTvSeparateAsync();
        }
        finally
        {
            if (!separate) _extendFailedUtc = DateTime.UtcNow;
            _forcingExtend = null;
            done.TrySetResult(separate);
        }
        return separate;
    }

    /// <summary>Switches Windows to "Extend" and puts the presentation full screen on the TV. True when the TV no longer copies.</summary>
    private async Task<bool> MakeTvSeparateAsync()
    {
        var before = _monitors.Select(m => m.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool asked = DisplayHelpers.ExtendDisplays();
        // Windows takes a moment to switch (the screens flicker); wait up to six seconds.
        for (int i = 0; asked && i < 40; i++)
        {
            await Task.Delay(150);
            List<DisplayMonitor> now = DisplayHelpers.GetMonitors();
            if (now.Count > before.Count && !DisplayHelpers.IsAnyScreenDuplicated())
            {
                _monitors = now;
                break;
            }
        }
        CheckDuplicateDisplay();
        if (!asked || IsDisplayDuplicated || OutputChooser.SeparatedTv(_monitors, before) is not { } tv)
        {
            Message("Your TV is copying your whole screen and Windows didn't let it be changed, so nothing is shown. Press Win+P and choose Extend.", BadgeDot.Warning,
                Button("Open display settings", () => ShellOpen("ms-settings:display")));
            return false;
        }
        RememberWindowModeScreens(); // the TV isn't "new" any more: don't offer it twice
        _arrivals.Know(tv.DeviceName);
        await SetOutputMonitorAsync(tv, quiet: true);
        Message($"Your TV was copying your whole screen. It's now a separate screen ({tv.Name}) that shows only what you choose.", BadgeDot.Accent);
        return true;
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
        _commands.Dispose();
        _commandGate.Dispose();
        _shotPreview?.Dispose();
        DisposeStage();
        _trimTimer.Dispose();
        DisposeExtras();
        _drawingOverlay?.Dispose();
        _pointerTimer.Dispose();
        _clock.Dispose();
        _slideshowTimer.Dispose();
        _updateTimer.Dispose();
        _foreground.Dispose();
        _titles.Dispose();
        _windowChanges.Dispose();
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
