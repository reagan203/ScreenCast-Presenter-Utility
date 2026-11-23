using System.Diagnostics;
using Microsoft.Win32;
using ScreenCastPresenter.Capture;
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
        _session = new SessionController(new Win32SessionPlatform(() => store.Current)) { BlankMessage = store.Current.BlankMessage };
        _hotkeys = new HotkeyManager();
        _badge = new PresentingBadge(PresenterScreen);
        _outline = new SourceOutline();
        _tray = new TrayIcon(this);

        _session.StateChanged += (_, _) => OnSessionChanged(reveal: true);
        _session.SourceGeometryChanged += (_, _) => OnSessionChanged(reveal: false);
        _session.Message += (_, message) =>
        {
            Diag("session: " + message);
            _badge.ShowMessage(message);
        };
        _hotkeys.Pressed += (_, id) =>
        {
            Diag("shortcut: " + id);
            _ = RunAsync(id);
        };
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
                case "freeze": await _session.ToggleFreezeAsync(); break;
                case "crop": await CropCurrentAsync(); break;
                case "remove-crop": await RemoveCropAsync(); break;
                case "screenshot": await TakeScreenshotAsync(); break;
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
        if (source.Screen is { } screen)
        {
            // FR-14: a screen that holds the output would show itself, endlessly.
            if (screen.IsSameScreen(output))
            {
                Message($"Display {screen.Number} is where the output is, so it can't be shown on itself. Pick another screen or move the output.");
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
        bool onlyScreen = _monitors.Count < 2;
        await _session.PresentAsync(source, output, topMost: !onlyScreen);
        if (_session.State != SessionState.Presenting) return;
        if (savedCrop)
            Message($"Cropped like last time. {ActiveShortcut("remove-crop")?.ToString() ?? "Remove the crop"} shows all of it.");
        if (onlyScreen)
            Message($"Only one screen is connected, so the output covers it. {StopHint()}");
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
        Rectangle client;
        if (source.Screen is { } screen) client = screen.Bounds;
        else
        {
            SourceWindow? alive = WindowEnumerator.Refresh(source);
            if (alive is null)
            {
                Message($"“{source.Title}” has closed.");
                return;
            }
            if (alive.IsMinimized)
            {
                Message($"Restore “{source.Title}” first, then crop it.");
                return;
            }
            client = WindowEnumerator.ClientBoundsOnScreen(source.Handle);
        }

        // Only on the presenter's screens: never draw the selection on the TV (SRS 4.5).
        DisplayMonitor? output = _session.OutputMonitor ?? ChosenOutput;
        DisplayMonitor? host = _monitors
            .Where(m => _monitors.Count < 2 || output is null || !m.IsSameScreen(output))
            .OrderByDescending(m => Area(Rectangle.Intersect(m.Bounds, client)))
            .FirstOrDefault();
        Rectangle area = host is null ? Rectangle.Empty : Rectangle.Intersect(host.Bounds, client);
        if (host is null || Area(area) < Area(client) / 3)
        {
            Message("Move the window onto your own screen to crop it.");
            return;
        }

        Rectangle? initial = source.Crop?.ToScreen(client);
        Rectangle? chosen = await RegionSelector.SelectAsync(area, initial, "Drag to choose what to show", host.Scale);
        if (chosen is not { } selection) return;

        CropRegion? crop = CropRegion.FromScreen(selection, client);
        if (crop is { IsFull: true }) crop = null;
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

        var goLive = new BadgeButton("Go live", true, () => _ = RunAsync("freeze"));
        bool keepsPicture = s.EngineName is { } engine && engine.StartsWith("Windows Graphics", StringComparison.Ordinal);

        BadgeContent? content = s.State switch
        {
            SessionState.Presenting when s.IsBlanked => new BadgeContent(BadgeDot.Hollow, $"{OutputName()} is blank", "Your audience sees black", [showAgain, stop]),
            SessionState.Presenting when s.IsFrozen => new BadgeContent(BadgeDot.Warning, $"{OutputName()} is frozen", "You can scroll and click privately", [goLive, stop]),
            SessionState.Presenting when s.IsSourceMinimized && keepsPicture => new BadgeContent(BadgeDot.Warning, "Window minimised", $"{OutputName()} keeps showing its last picture", [stop]),
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
        // FR-17: around exactly what the audience sees: the window, the cropped part of it, or the screen.
        Rectangle? bounds = null;
        if (show && s.Source is { } source)
        {
            if (source.Screen is { } screen) bounds = source.Crop?.ToScreen(screen.Bounds) ?? screen.Bounds;
            else if (WindowEnumerator.IsAlive(source.Handle))
                bounds = source.Crop is { } crop ? crop.ToScreen(WindowEnumerator.ClientBoundsOnScreen(source.Handle)) : DwmHelpers.GetVisibleBounds((Windows.Win32.Foundation.HWND)source.Handle);
        }
        _outline.Update(bounds, s.IsFrozen ? $"Frozen on {OutputName()}" : $"Showing on {OutputName()}", _monitors, s.OutputMonitor);
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

    private void Message(string text, BadgeDot dot = BadgeDot.Accent)
    {
        Diag("app: " + text);
        _badge.ShowMessage(text, dot);
    }

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
