using System.Diagnostics;
using Microsoft.Win32;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
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
    private readonly System.Windows.Forms.Timer _trimTimer = new() { Interval = 3000 };
    private List<DisplayMonitor> _monitors;
    private PickerOverlay? _picker;
    private CommandPalette? _palette;
    private ShortcutsForm? _shortcutsForm;
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
        _session = new SessionController(new Win32SessionPlatform()) { BlankMessage = store.Current.BlankMessage };
        _hotkeys = new HotkeyManager();
        _badge = new PresentingBadge(PresenterScreen);
        _outline = new SourceOutline();
        _tray = new TrayIcon(this);

        _session.StateChanged += (_, _) => OnSessionChanged(reveal: true);
        _session.SourceGeometryChanged += (_, _) => OnSessionChanged(reveal: false);
        _session.Message += (_, message) => _badge.ShowMessage(message);
        _hotkeys.Pressed += (_, id) => _ = RunAsync(id);
        Theme.Changed += OnThemeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
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
    public IReadOnlyList<DisplayMonitor> Monitors => _monitors;
    public IReadOnlyList<ShortcutBinding> Shortcuts => _hotkeys.Bindings;
    public bool ShortcutsPaused { get; private set; }

    /// <summary>Windows "Duplicate" mode (checked at start and on display changes): the TV shows everything.</summary>
    public bool IsDisplayDuplicated { get; private set; }

    /// <summary>The screen the output goes to next time: the saved one if connected, else the TV.</summary>
    public DisplayMonitor? ChosenOutput => _session.OutputMonitor is { } current
        ? _monitors.FirstOrDefault(m => m.IsSameScreen(current)) ?? current
        : OutputChooser.Choose(_monitors, Settings.OutputDevice);

    public void Start()
    {
        _ = _session.SetDimAsync(Settings.Dim);
        ApplyShortcuts(reportProblems: true);
        if (_store.LoadProblem is { } problem) _badge.ShowMessage(problem, BadgeDot.Warning);
        CheckDuplicateDisplay();
        PreparePickerSoon();
        if (!Settings.FirstRunDone) OpenFirstRun();
        else Message($"ScreenCast Presenter is running. Press {ActiveShortcut("picker")?.ToString() ?? "the tray icon"} to choose what to show.");
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
                case "toggle-outline": SetShowOutline(!Settings.ShowOutline); break;
                case "toggle-badge": SetShowBadge(!Settings.ShowBadge); break;
                case "cheat-sheet": OpenShortcuts(); break;
                case ShortcutCatalog.PauseShortcutsId: TogglePauseShortcuts(); break;
                case "toggle-theme": SetTheme(Theme.Opposite); break;
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
        DisplayMonitor? output = ChosenOutput;
        if (output is null)
        {
            Message("SCP can't find a screen to show on.");
            return;
        }
        if (!WindowEnumerator.IsAlive(source.Handle))
        {
            Message($"“{source.Title}” has closed.");
            return;
        }
        CheckDuplicateDisplay();
        bool onlyScreen = _monitors.Count < 2;
        await _session.PresentAsync(source, output, topMost: !onlyScreen);
        if (onlyScreen && _session.State == SessionState.Presenting)
            Message($"Only one screen is connected, so the output covers it. {StopHint()}");
    }

    private async Task PresentActiveWindowAsync()
    {
        if (WindowEnumerator.GetActiveWindow() is { } window) await PresentAsync(window);
        else Message("Click into the window you want to show, then press the shortcut again.");
    }

    private async Task PresentPreviousAsync()
    {
        if (_session.PreviousSource is { } previous && WindowEnumerator.Refresh(previous) is { } alive) await PresentAsync(alive);
        else Message("There's no previous window to go back to.");
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
        if (OutputChooser.Next(_monitors, ChosenOutput) is { } next) await SetOutputMonitorAsync(next);
    }

    private async Task ShowFavouriteAsync(int slot)
    {
        if (Settings.Favourites[slot - 1] is not { } favourite)
        {
            Message($"Favourite {slot} is empty. Show a window, then press {ActiveShortcut(ShortcutCatalog.SaveFavouriteId(slot))?.ToString() ?? "save favourite"} to save it.");
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
        Save();
        if (_session.State != SessionState.Off && _session.OutputMonitor is { } current && !current.IsSameScreen(monitor))
            await _session.MoveOutputAsync(monitor, topMost: _monitors.Count > 1);
        Message($"Output: {monitor.Label}.");
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

    private void Save()
    {
        _session.BlankMessage = Settings.BlankMessage;
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
    public void OpenPicker()
    {
        if (_picker is { IsDisposed: false, Visible: true })
        {
            _picker.Dismiss();
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (PresenterScreen() is not { } screen) return;
        PickerOverlay picker = EnsurePicker(screen);
        picker.Open();
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
        if (Environment.GetEnvironmentVariable("SCP_DIAG_LOG") is not { Length: > 0 } log) return;
        try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {message}\n"); } catch (IOException) { }
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
        _firstRun?.Close();
        await _session.StopAsync();
        QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── state → UI ─────────────────────────────

    private void OnSessionChanged(bool reveal)
    {
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

        BadgeContent? content = s.State switch
        {
            SessionState.Presenting when s.IsBlanked => new BadgeContent(BadgeDot.Hollow, $"{OutputName()} is blank", "Your audience sees black", [showAgain, stop]),
            SessionState.Presenting when s.IsSourceMinimized => new BadgeContent(BadgeDot.Warning, "Window minimised", $"{OutputName()} is black until you restore it", [stop]),
            SessionState.Presenting => new BadgeContent(BadgeDot.Accent, $"Showing on {OutputName()}", $"{s.Source?.Title} · {s.Source?.AppName}", [blank, stop]),
            SessionState.IdleScreen => new BadgeContent(BadgeDot.Hollow, $"{OutputName()} shows a blank screen", "Pick something to show", [pick, stop]),
            _ => null,
        };
        _badge.SetStatus(Settings.ShowBadge ? content : null, reveal);
    }

    private void UpdateOutline()
    {
        SessionController s = _session;
        bool show = Settings.ShowOutline && s.State == SessionState.Presenting && !s.IsBlanked && !s.IsSourceMinimized && s.Source is not null;
        _outline.Update(s.Source?.Handle ?? 0, show, $"Showing on {OutputName()}", _monitors, s.OutputMonitor);
    }

    /// <summary>"TV" for a TV-like output, else "Display 2".</summary>
    private string OutputName()
    {
        DisplayMonitor? m = _session.OutputMonitor ?? ChosenOutput;
        if (m is null) return "the output";
        return m.Connection is "HDMI" or "Wireless" ? "TV" : $"Display {m.Number}";
    }

    private DisplayMonitor? PresenterScreen() =>
        OutputChooser.PresenterScreen(_monitors, _session.OutputMonitor, _monitors.FirstOrDefault(m => m.Bounds.Contains(Cursor.Position))); // cached list: no display-config queries on the picker path (NFR-08)

    private string StopHint() => ActiveShortcut("stop") is { } stop ? $"Press {stop} to stop." : "Use the tray icon to stop.";

    private void Message(string text) => _badge.ShowMessage(text);

    private void OnThemeChanged(object? sender, EventArgs e)
    {
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
            if (_session.OutputMonitor is { } output)
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
        Theme.Changed -= OnThemeChanged;
        _trimTimer.Dispose();
        _picker?.Dispose();
        _hotkeys.Dispose();
        _session.Dispose();
        _outline.Dispose();
        _badge.Dispose();
        _tray.Dispose();
        _guard.Dispose();
        _store.Save();
    }
}
