using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// More privacy (SRS 3.12, rev 10): a whole screen shown stops short of its taskbar (FR-112), its
/// desktop icons are hidden (FR-113), it cuts to black while an Open or Save box is on it (FR-114),
/// the presenter is told when a meeting app shares a whole screen (FR-134), and a browser shown
/// shows only its page (FR-133). Every one of these only ever shows less.
/// </summary>
internal sealed partial class ScpApp
{
    // ───────────────────────────── the taskbar left out (FR-112) ─────────────────────────────

    /// <summary>
    /// A whole screen with its taskbar left out: the crop kept inside the part clear of it. Null when
    /// the part the presenter chose is all taskbar, so nothing of it may be shown. Anything else as it is.
    /// </summary>
    internal SourceWindow? LeaveOutTaskbar(SourceWindow source)
    {
        if (source.Screen is not { } screen) return source;
        CropRegion? chosen = source.ChosenCrop;
        CropRegion? clear = Settings.LeaveOutTaskbar ? Taskbars.ClearArea(screen) : null;
        (CropRegion? crop, bool empty) = TaskbarCrop.Inside(chosen, clear);
        if (empty) return null;
        return source with { Crop = crop, AutoCrop = clear };
    }

    /// <summary>A screen shown down to its taskbar as it is now, the presenter's part dropped (it was all taskbar).</summary>
    private SourceWindow ClearOfTaskbarNow(SourceWindow screen) =>
        LeaveOutTaskbar(screen with { Crop = null, AutoCrop = null }) ?? screen with { Crop = null, AutoCrop = null };

    /// <summary>The taskbar moved, started hiding itself, or the setting changed: what's shown follows.</summary>
    private async Task RefreshTaskbarCropsAsync()
    {
        if (_session.State == SessionState.Presenting && _session.Source is { IsScreen: true } shown)
        {
            // The part chosen is all taskbar now (the taskbar moved onto it): all that's clear of it now, never an old crop.
            SourceWindow next = LeaveOutTaskbar(shown) ?? ClearOfTaskbarNow(shown);
            if (next.Crop != shown.Crop || next.AutoCrop != shown.AutoCrop) await _session.SetAutoCropAsync(next.Crop, next.AutoCrop, fade: true);
        }
        if (_inset is { IsScreen: true } inset && (LeaveOutTaskbar(inset) ?? ClearOfTaskbarNow(inset)) is var small && small.Crop != inset.Crop)
        {
            _inset = small;
            SendOtherHiddenParts();
            _session.Effects?.Inset(small, InsetLayout.ParseCorner(Settings.InsetCorner), Settings.InsetSize);
        }
        bool splitChanged = false;
        for (int i = 0; i < _split.Count; i++)
        {
            if (_split[i] is not { IsScreen: true } pane || (LeaveOutTaskbar(pane) ?? ClearOfTaskbarNow(pane)) is var next && next.Crop == pane.Crop) continue;
            _split[i] = next;
            splitChanged = true;
        }
        if (splitChanged)
        {
            SendOtherHiddenParts();
            _session.Effects?.Split(SplitLayoutNow, _split);
        }
        if (_second is { State: SessionState.Presenting, Source: { IsScreen: true } secondShown } second && (LeaveOutTaskbar(secondShown) ?? ClearOfTaskbarNow(secondShown)) is var secondNext
            && (secondNext.Crop != secondShown.Crop || secondNext.AutoCrop != secondShown.AutoCrop))
            await second.SetAutoCropAsync(secondNext.Crop, secondNext.AutoCrop, fade: true);
        UpdateOutline();
    }

    /// <summary>Settings › Privacy › Leave out the taskbar.</summary>
    public void SetLeaveOutTaskbar(bool on)
    {
        Settings.LeaveOutTaskbar = on;
        Save();
        _ = GuardAsync(RefreshTaskbarCropsAsync);
    }

    /// <summary>Windows says something in its settings changed: the taskbar may have moved or started hiding itself.</summary>
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop or Microsoft.Win32.UserPreferenceCategory.General)
            BeginInvokeOnUi(() => _ = GuardAsync(RefreshTaskbarCropsAsync));
    }

    // ───────────────────────────── desktop icons (FR-113) ─────────────────────────────

    private DesktopIconGuard? _desktopIcons;
    private readonly System.Windows.Forms.Timer _iconCheck = new() { Interval = 2000 };

    /// <summary>
    /// A whole screen is on a TV somewhere: the main picture, beside it, small in a corner, or the
    /// second TV. Not a private preview (a floating window showing the screen it's on, hidden from
    /// capture): nobody else sees it, so the presenter's own desktop isn't changed for it.
    /// </summary>
    private bool ScreenShownAnywhere =>
        _session.State == SessionState.Presenting && !_session.IsPrivatePreview
            && (_session.Source is { IsScreen: true } || _inset is { IsScreen: true } || _split.Any(s => s.IsScreen))
        || _second is { State: SessionState.Presenting, Source.IsScreen: true };

    private DesktopIconGuard DesktopIconGuard => _desktopIcons ??= new DesktopIconGuard(DesktopIcons.Instance, hidden =>
    {
        Settings.DesktopIconsHidden = hidden;
        Save();
    });

    private void UpdateDesktopIcons()
    {
        bool hide = !_quitting && Settings.HideDesktopIcons && ScreenShownAnywhere;
        DesktopIconGuard.Update(hide);
        if (hide == _iconCheck.Enabled) return;
        if (hide) _iconCheck.Start();
        else _iconCheck.Stop();
    }

    /// <summary>Settings › Privacy › Hide desktop icons.</summary>
    public void SetHideDesktopIcons(bool on)
    {
        Settings.HideDesktopIcons = on;
        Save();
        UpdateDesktopIcons();
    }

    /// <summary>At start: the last run hid the icons and ended before showing them again; or left words in Windows' speech dictionary.</summary>
    private void RepairDesktopIcons()
    {
        if (Settings.CaptionWordsInWindows.Count > 0)
        {
            Capture.CaptionsProcess.RemoveWords(Settings.CaptionWordsInWindows); // FR-128: Windows is left as it was
            Settings.CaptionWordsInWindows = [];
            Save();
        }
        if (!Settings.DesktopIconsHidden) return;
        if (DesktopIconGuard.RepairAfterCrash(DesktopIcons.Instance, hiddenLastRun: true)) Diag("desktop icons: shown again after the last run");
        Settings.DesktopIconsHidden = false;
        Save();
    }

    // ───────────────────────────── Open and Save boxes (FR-114) ─────────────────────────────

    private string? _fileBoxShown;

    /// <summary>An Open or Save box on the part of a screen being shown (the main picture, small, or beside it); null when there's none.</summary>
    private VisibleWindow? FileBoxOn(SourceWindow source, IReadOnlyList<VisibleWindow>? windows = null)
    {
        if (!Settings.HoldForFileBoxes) return null;
        Rectangle? main = ShownScreenArea(source), small = _inset is { } inset ? ShownScreenArea(inset) : null;
        List<Rectangle> beside = [.. SplitScreenAreas()];
        if (main is null && small is null && beside.Count == 0) return null;
        List<VisibleWindow> boxes = FileBoxFinder.Find(windows ?? WindowEnumerator.GetVisibleWindows([]));
        if (boxes.Count == 0) return null;
        return (main is { } m ? FileBoxes.FindOnScreen(boxes, m) : null)
            ?? (small is { } s ? FileBoxes.FindOnScreen(boxes, s) : null)
            ?? beside.Select(area => FileBoxes.FindOnScreen(boxes, area)).FirstOrDefault(found => found is not null);
    }

    /// <summary>A file box turned up on what's shown: black at once (no fade) until it closes or moves away.</summary>
    private void CheckFileBoxes(SourceWindow source)
    {
        if (FileBoxOn(source) is { } found)
        {
            if (!_session.IsHeldFor(HoldReason.FileBox)) _session.CutAway(HoldReason.FileBox);
            if (_fileBoxShown != found.AppName)
            {
                _fileBoxShown = found.AppName;
                Message($"An Open or Save box is on {(source.Screen ?? _inset?.Screen)?.Name ?? "the screen"}, so the audience sees black until it closes. Folder and file names stay private.", BadgeDot.Accent,
                    Button("Change this", () => OpenSettings(SettingsForm.Section.Privacy), primary: false));
                UpdateBadge(reveal: false);
                UpdateWidget();
            }
            return;
        }
        _fileBoxShown = null;
        if (_session.IsHeldFor(HoldReason.FileBox)) _ = _session.ComeBackAsync(HoldReason.FileBox);
    }

    /// <summary>An Open or Save box on the part of a screen the second TV is about to show (checked before it's seen).</summary>
    private bool SecondFileBoxOn(SourceWindow source) =>
        Settings.HoldForFileBoxes && ShownScreenArea(source) is { } area && FileBoxes.FindOnScreen(FileBoxFinder.Find(WindowEnumerator.GetVisibleWindows([])), area) is not null;

    /// <summary>The second TV, the same way.</summary>
    private void CheckSecondFileBoxes()
    {
        if (_second is not { State: SessionState.Presenting, Source: { } source } second) return;
        Rectangle? area = Settings.HoldForFileBoxes ? ShownScreenArea(source) : null;
        VisibleWindow? found = area is { } a ? FileBoxes.FindOnScreen(FileBoxFinder.Find(WindowEnumerator.GetVisibleWindows([])), a) : null;
        if (found is not null)
        {
            if (!second.IsHeldFor(HoldReason.FileBox)) second.CutAway(HoldReason.FileBox);
            return;
        }
        if (second.IsHeldFor(HoldReason.FileBox)) _ = second.ComeBackAsync(HoldReason.FileBox);
    }

    /// <summary>Settings › Privacy › Open and Save boxes.</summary>
    public void SetHoldForFileBoxes(bool on)
    {
        Settings.HoldForFileBoxes = on;
        Save();
        UpdatePrivateAppGuard();
    }

    // ───────────────────────────── a meeting sharing a whole screen (FR-134) ─────────────────────────────

    private readonly System.Windows.Forms.Timer _shareWatch = new() { Interval = 3000 };
    private List<WholeScreenShare> _sharesSeen = [];

    private void UpdateShareWatch()
    {
        bool watch = !_quitting && Settings.WarnWholeScreenShare && _session.State != SessionState.Off;
        if (watch == _shareWatch.Enabled) return;
        if (watch)
        {
            _shareWatch.Start();
            CheckShares();
        }
        else
        {
            _shareWatch.Stop();
            _sharesSeen = [];
        }
    }

    /// <summary>Screens are safe to share when only the chosen source is on them: the Zoom screen, or a TV showing it full screen.</summary>
    private List<WatchedScreen> WatchedScreens()
    {
        DisplayMonitor? tv = _session.FullScreenOutput, secondTv = _second?.FullScreenOutput;
        return [.. _monitors.Select(m => new WatchedScreen(m.DeviceName, m.Name, m.Bounds,
            Safe: tv is not null && m.IsSameScreen(tv) || secondTv is not null && m.IsSameScreen(secondTv)))];
    }

    private void CheckShares()
    {
        List<WholeScreenShare> now = ShareWatch.Find(ShareWindows.Describe(), WatchedScreens());
        foreach (WholeScreenShare share in ShareWatch.New(_sharesSeen, now))
        {
            Diag($"share watch: {share.App} shares all of {share.ScreenName}");
            bool zoomScreen = _monitors.Any(m => m.IsVirtual);
            Message($"{share.App} is sharing all of {share.ScreenName}, so the meeting sees everything on it, not only what you're showing. "
                    + (zoomScreen ? "Share the Zoom screen instead." : "Share the ScreenCast Presenter window instead."), BadgeDot.Warning,
                Button("Show me how", () => OpenShareGuide(zoomScreen ? ShareGuideKind.ZoomScreen : ShareGuideKind.Window)),
                Button("Don't warn me", () => SetWarnWholeScreenShare(false), primary: false));
        }
        _sharesSeen = now;
    }

    /// <summary>Settings › Privacy › Meetings sharing a whole screen.</summary>
    public void SetWarnWholeScreenShare(bool on)
    {
        Settings.WarnWholeScreenShare = on;
        Save();
        UpdateShareWatch();
        if (!on) Message("You won't be told when a meeting shares a whole screen. Switch it back on in Settings › Privacy.");
    }

    // ───────────────────────────── a browser's page only (FR-133) ─────────────────────────────

    private BrowserPageWatcher? _pageWatcher;

    /// <summary>Shown the first time: a browser window with only its page; null when it can't tell (it's shown whole, as before).</summary>
    private async Task<SourceWindow> ShowOnlyThePageAsync(SourceWindow source)
    {
        if (!Settings.BrowserPageOnly || !source.IsWindow || source.ChosenCrop is not null || !BrowserTab.IsBrowser(source.AppName)) return source;
        nint window = source.Handle;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan budget = TimeSpan.FromMilliseconds(900);
        try
        {
            // A page still loading may have no page window yet: asked again shortly, within the same
            // 0.9 s, so it's shown with only its page from the start rather than whole and then cut.
            for (TimeSpan left = budget; left > TimeSpan.Zero; left = budget - clock.Elapsed)
            {
                (CropRegion? page, bool sure) = await Task.Run(() => BrowserPageWatcher.AskNow(window)).WaitAsync(left);
                if (sure) return page is null ? source : source with { Crop = page, AutoCrop = page };
                if (clock.Elapsed + TimeSpan.FromMilliseconds(150) >= budget) break;
                await Task.Delay(150);
            }
        }
        catch (TimeoutException)
        {
        }
        Diag("browser page: no answer in time; shown whole until it answers");
        return source;
    }

    /// <summary>Watched only while a browser is shown with nothing the presenter cropped.</summary>
    private void UpdatePageWatch()
    {
        bool watch = !_quitting && Settings.BrowserPageOnly && _session.State == SessionState.Presenting
            && _session.Source is { IsWindow: true } source && source.ChosenCrop is null && BrowserTab.IsBrowser(source.AppName);
        if (!watch)
        {
            _pageWatcher?.Watch(0);
            return;
        }
        _pageWatcher ??= new BrowserPageWatcher((window, crop) => Guard(() => OnPageArea(window, crop)));
        _pageWatcher.Watch(_session.Source!.Handle, _session.Source.AutoCrop);
    }

    /// <summary>The browser's page moved or changed size (the window was resized, the bookmarks bar shown): the picture follows, without a fade.</summary>
    private void OnPageArea(nint window, CropRegion? page)
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { IsWindow: true } source || source.Handle != window || source.ChosenCrop is not null) return;
        if (BrowserPage.Same(source.AutoCrop, page) && BrowserPage.Same(source.Crop, page)) return;
        _ = GuardAsync(() => _session.SetAutoCropAsync(page, page, fade: false));
    }

    /// <summary>Settings › Privacy › Browsers: only the page.</summary>
    public void SetBrowserPageOnly(bool on)
    {
        Settings.BrowserPageOnly = on;
        Save();
        if (!on && _session.Source is { IsWindow: true, AutoCrop: not null } source && source.ChosenCrop is null)
            _ = GuardAsync(() => _session.SetAutoCropAsync(null, null, fade: true));
        UpdatePageWatch();
    }

    // ───────────────────────────── shared ─────────────────────────────

    /// <summary>"(the taskbar is left out)", "(only the page shows)": why "all of it" isn't quite all of it.</summary>
    private static string AutoCropWords(SourceWindow source) =>
        source.AutoCrop is null ? "" : source.IsScreen ? " (the taskbar is left out)" : " (only the page shows)";

    private void UpdatePrivacyExtras()
    {
        UpdateDesktopIcons();
        UpdateShareWatch();
        UpdatePageWatch();
    }

    private void StartPrivacyExtras()
    {
        _iconCheck.Tick += (_, _) => Guard(() => DesktopIconGuard.Check());
        _shareWatch.Tick += (_, _) => Guard(CheckShares);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void DisposePrivacyExtras()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _desktopIcons?.Update(false); // the icons come back as the app goes
        _iconCheck.Dispose();
        _shareWatch.Dispose();
        _pageWatcher?.Dispose();
        _pageWatcher = null;
    }
}
