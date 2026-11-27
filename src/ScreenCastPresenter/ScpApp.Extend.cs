using System.Diagnostics;
using System.Text.Json.Nodes;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Remote;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// Running the room (SRS 3.9, FR-86 to FR-90): scenes, playlists for lobby screens, automatic
/// hiding of personal details, transcripts and caption sizes, and the phone remote. Each costs
/// nothing until it's used: timers run only while something plays, text recognition and the
/// remote run in helper processes only while switched on.
/// </summary>
internal sealed partial class ScpApp
{
    // ───────────────────────────── scenes (FR-86) ─────────────────────────────

    public IReadOnlyList<Scene?> Scenes => Settings.Scenes;

    /// <summary>"Save this as a scene": a name and a slot (1–9), then what's on now is kept.</summary>
    public void SaveScene()
    {
        if (_session.State != SessionState.Presenting || _session.Source is null)
        {
            Message("Show something first, then save it as a scene with everything around it.");
            return;
        }
        int free = Array.FindIndex(Settings.Scenes, s => s is null);
        using var form = new FieldsForm("Save this as a scene",
            "A scene keeps what's shown and everything around it: the part shown, the small picture, your name, a QR code for a link, the spotlight and dimming. Show it again with one key.", "Save the scene");
        (_, TextBox name) = form.AddField("Name", _session.Source.Title.Length > 0 ? TrayMenuPlan.Shorten(_session.Source.Title, Scene.MaxNameLength) : "", Scene.MaxNameLength, "Demo");
        ComboBox slot = form.AddList("Key", [.. Enumerable.Range(1, Scene.Slots).Select(i => SceneSlotWords(i))], free >= 0 ? free : 0);
        if (!form.Ask()) return;
        SaveScene(slot.SelectedIndex + 1, name.Text);
    }

    private string SceneSlotWords(int slot) =>
        $"Scene {slot}{(ActiveShortcut(ShortcutCatalog.SceneId(slot)) is { } key ? $" ({key})" : "")}{(Settings.Scenes[slot - 1] is { } old ? $": replaces “{old.Title(slot)}”" : "")}";

    /// <summary>Keeps what's on now as scene <paramref name="slot"/>.</summary>
    public void SaveScene(int slot, string name)
    {
        if (slot is < 1 or > Scene.Slots || _session.Source is not { } source) return;
        Settings.Scenes[slot - 1] = new Scene
        {
            Name = InkStroke.TidyText(name, Scene.MaxNameLength),
            Source = FavouriteMatcher.FromSource(source),
            Crop = source.ChosenCrop, // the taskbar and a browser's page are worked out again when it's shown
            Inset = _inset is { } inset ? FavouriteMatcher.FromSource(inset) : null,
            InsetShape = Settings.InsetShape,
            Night = Settings.NightFilter,
            SplitWith = [.. _split.Select(FavouriteMatcher.FromSource)],
            SplitLayout = SplitLayoutNow.ToString(),
            InsetCorner = Settings.InsetCorner,
            InsetSize = Settings.InsetSize,
            Caption = _captionOn,
            QrLink = Scene.KeepableQr(_qrText),
            Spotlight = _spotlightOn,
            Dim = _session.Dim,
            LiveCaptions = LiveCaptionsOn,
        };
        Save();
        _tray.Refresh();
        Message($"Saved as scene {slot}{(ActiveShortcut(ShortcutCatalog.SceneId(slot)) is { } key ? $". Press {key} to show it" : "")}.", BadgeDot.Accent);
    }

    /// <summary>Shows scene <paramref name="slot"/>: its source through black, then everything around it.</summary>
    private async Task ShowSceneAsync(int slot)
    {
        if (slot is < 1 or > Scene.Slots || Settings.Scenes[slot - 1] is not { Source: { } favourite } scene)
        {
            Message($"Scene {slot} is empty. Show something, set it up, then save it from the tray menu.");
            return;
        }
        if (await ResolveAsync(favourite) is not { } source)
        {
            Message($"{scene.Title(slot)} can't be shown: {Missing(favourite)}.");
            return;
        }
        _pendingSection = ($"scene:{slot}", scene.Title(slot), favourite.Minutes);
        try
        {
            await PresentAsync(scene.Crop is { } crop ? source with { Crop = crop } : source);
        }
        finally
        {
            _pendingSection = null;
        }
        if (_session.State != SessionState.Presenting || _session.Source is not { } shown || !shown.IsSameSource(source)) return;

        Settings.InsetCorner = scene.InsetCorner;
        Settings.InsetSize = scene.InsetSize;
        if (scene.Inset is { } small && await ResolveAsync(small) is { } inset && !inset.IsSameSource(shown)) await SetInsetAsync(inset);
        else if (_inset is not null) RemoveInset(null);
        StopSplit(null);
        if (scene.SplitWith.Count > 0)
        {
            Settings.SplitLayout = SplitScreen.Parse(scene.SplitLayout).ToString();
            foreach (Favourite beside in scene.SplitWith)
                if (await ResolveAsync(beside) is { } other && SplitProblem(other) is null) await AddSplitAsync(other, quiet: true);
        }
        if (scene.Caption != _captionOn && (Settings.CaptionName.Length > 0 || !scene.Caption)) SetCaption(scene.Caption);
        if (scene.QrLink != _qrText) SetQr(scene.QrLink);
        if (scene.Spotlight != _spotlightOn) SetSpotlight(scene.Spotlight);
        if (Math.Abs(scene.Dim - _session.Dim) > 0.001) await _session.SetDimAsync(scene.Dim);
        if (scene.LiveCaptions is { } captions && captions != LiveCaptionsOn) ToggleLiveCaptions();
        if (InsetShapes.Parse(scene.InsetShape) != Settings.InsetShapeMode) SetInsetShape(InsetShapes.Parse(scene.InsetShape));
        if (scene.Night is { } night && NightFilter.Parse(night) != Settings.Night) SetNightFilter(NightFilter.Parse(night));
        Message($"Scene {slot}: {scene.Title(slot)}.", BadgeDot.Accent);
    }

    /// <summary>Finds what a saved source means now: the window, the screen or the camera; null when it isn't there.</summary>
    private async Task<SourceWindow?> ResolveAsync(Favourite f)
    {
        if (f.ScreenDevice is { } device) return _monitors.FirstOrDefault(m => m.DeviceName == device) is { } screen ? SourceWindow.ForScreen(screen) : null;
        if (f.CameraId is { } id) return (await Cameras.ListAsync()).FirstOrDefault(c => c.Id == id) is { } camera ? SourceWindow.ForCamera(camera) : null;
        if (f.PicturePath is { } picture) return PictureSource.Problem(picture) is null ? SourceWindow.ForPicture(picture, f.Title) : null;
        if (f.VideoPath is not null || f.DocumentPath is not null || f.Special is not null) return await ResolveFileAsync(f);
        return FavouriteMatcher.FindBest(f, WindowEnumerator.GetPresentableWindows()) is { } w && !IsNeverShown(w) ? w : null;
    }

    private static string Missing(Favourite f) =>
        f.ScreenDevice is not null ? "its screen isn't connected" : f.CameraId is not null ? "its camera isn't connected"
        : f.PicturePath is not null ? "its picture can't be found"
        : f.VideoPath is not null ? "its video can't be found" : f.DocumentPath is not null ? "its PDF can't be found" : $"{f.AppName} isn't open";

    /// <summary>Settings: forget a scene.</summary>
    public void DeleteScene(int slot)
    {
        if (slot is < 1 or > Scene.Slots || Settings.Scenes[slot - 1] is null) return;
        Settings.Scenes[slot - 1] = null;
        foreach (Playlist p in Settings.Playlists) p.Items.RemoveAll(i => i.IsScene && i.Slot == slot);
        Settings.Normalize(); // a playlist left empty goes
        Save();
        _tray.Refresh();
    }

    // ───────────────────────────── playlists (FR-87) ─────────────────────────────

    /// <summary>A playlist playing on one screen.</summary>
    private sealed class PlaylistRun(Playlist playlist, PlaylistScreen screen) : IDisposable
    {
        public Playlist Playlist { get; } = playlist;
        public PlaylistScreen Screen { get; } = screen;
        public System.Windows.Forms.Timer Timer { get; } = new();
        public int? Index { get; set; }
        public bool Ticking { get; set; }

        public void Dispose() => Timer.Dispose();
    }

    private readonly Dictionary<PlaylistScreen, PlaylistRun> _playing = [];

    /// <summary>The order of shuffled playlists (not for anything secret).</summary>
    private readonly Random _shuffle = new();

    /// <summary>The playlist itself is choosing what to show (so that doesn't count as the presenter choosing).</summary>
    private bool _playlistSwitching;

    /// <summary>A playlist the TV going away interrupted: started again when it's back (a lobby screen runs itself).</summary>
    private string? _interruptedPlaylist;

    public string? PlayingOn(PlaylistScreen screen) => _playing.TryGetValue(screen, out PlaylistRun? run) ? run.Playlist.Name : null;

    /// <summary>Plays the playlist called <paramref name="name"/> on its screen, from its first item that can be shown.</summary>
    public async Task<bool> StartPlaylistAsync(string name)
    {
        if (Playlist.Find(Settings.Playlists, name) is not { } playlist)
        {
            Message($"There's no playlist called “{TrayMenuPlan.Shorten(name, 40)}”. Make one in Settings › Rooms, scenes & playlists.", BadgeDot.Warning);
            return false;
        }
        PlaylistScreen screen = playlist.Where;
        if (screen == PlaylistScreen.Second && SecondTvCandidates().Count == 0 && !SecondTvOn)
        {
            Message($"“{playlist.Name}” plays on the second TV, and none is connected.", BadgeDot.Warning);
            return false;
        }
        StopPlaylist(screen, null);
        if (screen == PlaylistScreen.Main) StopSlideshow(null);
        var run = new PlaylistRun(playlist, screen);
        run.Timer.Tick += (_, _) => _ = GuardAsync(() => OnPlaylistTickAsync(run));
        _playing[screen] = run;
        if (!await ShowNextAsync(run))
        {
            StopPlaylist(screen, $"Nothing in “{playlist.Name}” can be shown right now.");
            return false;
        }
        _interruptedPlaylist = null;
        _tray.Refresh();
        Message($"Playing “{playlist.Name}” on {(screen == PlaylistScreen.Main ? OutputName() : "the second TV")}. Choosing something else there stops it.", BadgeDot.Accent,
            Button("Stop it", () => StopPlaylist(screen, "The playlist stopped."), primary: false));
        return true;
    }

    /// <summary>Shows the next item that can be shown; false when none can.</summary>
    private async Task<bool> ShowNextAsync(PlaylistRun run)
    {
        List<PlaylistItem> steps = PlaylistPlayer.Expand(run.Playlist.Items, PdfPageCount); // a folder's pictures and a PDF's pages as they are now
        var resolved = new Dictionary<PlaylistItem, SourceWindow?>();
        // A PDF's pages are made only when one is shown (not every page on every turn): until then, the PDF being there is enough.
        foreach (PlaylistItem item in steps.Distinct())
            resolved[item] = item.IsDocument ? item.Path is { } pdf && PdfPages.Problem(pdf) is null ? SourceWindow.ForPicture(pdf) : null : await ResolveItemAsync(item, run.Screen);
        if (!_playing.TryGetValue(run.Screen, out PlaylistRun? still) || !ReferenceEquals(still, run)) return false; // stopped meanwhile
        int? next = PlaylistPlayer.Next(steps, run.Index is { } at && at < steps.Count ? at : null, item => resolved[item] is not null, run.Playlist.Shuffle, _shuffle);
        if (next is not { } index) return false;
        if (index == run.Index && run.Index is not null && steps.Count(s => resolved[s] is not null) == 1)
        {
            // Only one can be shown: it stays. A video that ended plays again (a lobby loop of one video).
            if (steps[index].IsVideo && run.Screen == PlaylistScreen.Main && _session.Source is { IsVideo: true }) _session.Effects?.Video(VideoCommand.Restart, 0);
            return true;
        }
        PlaylistItem chosen = steps[index];
        if (chosen.IsDocument && await ResolveItemAsync(chosen, run.Screen) is { } page) resolved[chosen] = page;
        else if (chosen.IsDocument) return false; // its page couldn't be made
        _playlistSwitching = true;
        try
        {
            if (run.Screen == PlaylistScreen.Second) await ShowOnSecondTvAsync(resolved[chosen]!);
            else if (chosen.IsScene) await ShowSceneAsync(chosen.Slot);
            else await PresentAsync(resolved[chosen]!);
        }
        finally
        {
            _playlistSwitching = false;
        }
        run.Index = index;
        run.Timer.Stop();
        // A video plays to its end (it says when); the timer is only a backstop for one that never does.
        run.Timer.Interval = chosen.IsVideo ? (int)TimeSpan.FromHours(4).TotalMilliseconds : Slideshow.Normalize(chosen.Seconds) * 1000;
        run.Timer.Start();
        return true;
    }

    /// <summary>What an item shows now, or null when it can't be (not open, not connected, never-show, a TV's own screen).</summary>
    private async Task<SourceWindow?> ResolveItemAsync(PlaylistItem item, PlaylistScreen screen)
    {
        if (item.IsPicture) return item.Path is { } file && PictureSource.Problem(file) is null ? SourceWindow.ForPicture(file) : null;
        if (item.IsCard) return item.Text is { } words && TextCard.Render(CardsFolder, words) is { } card ? SourceWindow.ForPicture(card, TextCard.Title(words)) : null;
        if (item.IsVideo) return item.Path is { } video && VideoSource.Problem(video) is null && screen == PlaylistScreen.Main ? SourceWindow.ForVideo(video) : null; // a video plays on the main TV
        if (item.IsDocument) return item.Path is { } pdf && PdfPages.Problem(pdf) is null ? await DocumentPageAsync(pdf, Math.Max(1, item.Slot), quiet: true) : null;
        if (item.IsRoomSign) return Settings.Calendar is null ? null : RoomSignSource();
        Favourite? f = item.IsScene ? Settings.Scenes[item.Slot - 1]?.Source : Settings.Favourites[item.Slot - 1];
        if (f is null || await ResolveAsync(f) is not { } source) return null;
        if (source.Screen is { } s && (_session.FullScreenOutput is { } tv && s.IsSameScreen(tv) || _second?.FullScreenOutput is { } tv2 && s.IsSameScreen(tv2)
            || screen == PlaylistScreen.Second && SecondTvScreen is { } target && s.IsSameScreen(target))) return null;
        return source;
    }

    private async Task OnPlaylistTickAsync(PlaylistRun run)
    {
        if (run.Ticking || !_playing.TryGetValue(run.Screen, out PlaylistRun? current) || !ReferenceEquals(current, run)) return;
        SessionController session = run.Screen == PlaylistScreen.Main ? _session : _second ?? _session;
        // Blank, frozen or held black: it waits, so it never changes what's behind the black.
        if (session.State == SessionState.Presenting && (session.IsBlanked || session.IsFrozen || session.IsHeld)) return;
        run.Ticking = true;
        try
        {
            if (!await ShowNextAsync(run)) StopPlaylist(run.Screen, $"Nothing in “{run.Playlist.Name}” can be shown any more, so it stopped.");
        }
        finally
        {
            run.Ticking = false;
        }
    }

    public void StopPlaylist(PlaylistScreen screen, string? why)
    {
        if (!_playing.Remove(screen, out PlaylistRun? run)) return;
        run.Dispose();
        _tray.Refresh();
        UpdateWidget();
        if (why is not null) Message(why);
    }

    public void StopPlaylists(string? why)
    {
        bool any = _playing.Count > 0;
        foreach (PlaylistScreen screen in _playing.Keys.ToList()) StopPlaylist(screen, null);
        if (any && why is not null) Message(why);
    }

    /// <summary>The presenter chose something else on a screen: that screen's playlist stops.</summary>
    private void PresenterChose(PlaylistScreen screen)
    {
        if (!_playlistSwitching && _playing.ContainsKey(screen)) StopPlaylist(screen, "The playlist stopped: you chose something to show.");
    }

    /// <summary>Before screens change: remember a playlist on the main TV, so it can carry on when the TV comes back.</summary>
    private void NotePlaylistBeforeScreensChange() => _interruptedPlaylist ??= PlayingOn(PlaylistScreen.Main);

    /// <summary>After screens change: a playlist the TV going away stopped starts again once something can be shown.</summary>
    private async Task ResumePlaylistAsync()
    {
        if (_interruptedPlaylist is not { } name) return;
        if (PlayingOn(PlaylistScreen.Main) is not null)
        {
            _interruptedPlaylist = null; // still playing: nothing was interrupted
            return;
        }
        if (ChosenPlacement is null) return; // the TV isn't back yet
        _interruptedPlaylist = null;
        await StartPlaylistAsync(name);
    }

    /// <summary>"Make a playlist": a name, its screen, how long each shows, and which favourites and scenes.</summary>
    public void MakePlaylist()
    {
        var favourites = Enumerable.Range(1, AppSettings.SlotCount).Where(s => Settings.Favourites[s - 1] is not null).ToList();
        var scenes = Enumerable.Range(1, Scene.Slots).Where(s => Settings.Scenes[s - 1] is not null).ToList();
        // Pictures and cards need no favourites: a playlist can be made of them alone.
        if (Settings.Playlists.Count >= Playlist.Max)
        {
            Message($"You can keep {Playlist.Max} playlists. Remove one in Settings first.");
            return;
        }
        using var form = new FieldsForm("Make a playlist", "The ones you tick take turns, round and round, like a lobby screen. Choosing something else on that TV stops it.", "Make it");
        (_, TextBox name) = form.AddField("Name", "", Playlist.MaxNameLength, "Lobby");
        ComboBox where = form.AddList("Plays on", ["The TV", "The second TV"], 0);
        ComboBox seconds = form.AddList("Each shows for", [.. Slideshow.Seconds.Select(Slideshow.Words)], Math.Max(0, Slideshow.Seconds.ToList().IndexOf(Settings.SlideshowSeconds)));
        CheckBox shuffle = form.AddCheck("Shuffle: a different order each time round", false);
        (_, TextBox folder) = form.AddField("Pictures from a folder (optional): each in turn", "", 260, @"C:\Users\Public\Pictures\Lobby");
        (_, TextBox card) = form.AddField("A card's words (optional): the first line is its title", "", TextCard.MaxLength, "Welcome to Acme" + Environment.NewLine + "Wi-Fi: Guest", lines: 3);
        var ticks = new List<(CheckBox Box, PlaylistItem Item)>();
        foreach (int s in favourites)
            ticks.Add((form.AddCheck($"Favourite {s}: {TrayMenuPlan.Shorten(Settings.Favourites[s - 1]!.Title, 40)}", true), new PlaylistItem { Kind = "Favourite", Slot = s }));
        foreach (int s in scenes)
            ticks.Add((form.AddCheck($"Scene {s}: {TrayMenuPlan.Shorten(Settings.Scenes[s - 1]!.Title(s), 40)}", true), new PlaylistItem { Kind = "Scene", Slot = s }));
        form.Check = () =>
            RoomProfile.TidyName(name.Text).Length == 0 ? "Type a name for the playlist."
            : Playlist.Find(Settings.Playlists, name.Text) is not null ? "There's a playlist with that name already."
            : folder.Text.Trim().Length > 0 && !(Path.IsPathFullyQualified(folder.Text.Trim()) && Directory.Exists(folder.Text.Trim())) ? "That pictures folder can't be found."
            : folder.Text.Trim().Length > 0 && PictureSource.InFolder(folder.Text.Trim()).Count == 0 ? "That folder has no pictures in it."
            : !ticks.Any(t => t.Box.Checked) && folder.Text.Trim().Length == 0 && TextCard.Tidy(card.Text).Length == 0 ? "Tick a favourite or scene, or add pictures or a card." : null;
        if (!form.Ask()) return;
        int each = Slideshow.Seconds[Math.Max(0, seconds.SelectedIndex)];
        Settings.Playlists.Add(new Playlist
        {
            Name = RoomProfile.TidyName(name.Text),
            Screen = (where.SelectedIndex == 1 ? PlaylistScreen.Second : PlaylistScreen.Main).ToString(),
            Items =
            [
                .. ticks.Where(t => t.Box.Checked).Select(t => new PlaylistItem { Kind = t.Item.Kind, Slot = t.Item.Slot, Seconds = each }),
                .. folder.Text.Trim() is { Length: > 0 } pictures ? [new PlaylistItem { Kind = PlaylistItem.PictureKind, Path = pictures, Seconds = each }] : Array.Empty<PlaylistItem>(),
                .. TextCard.Tidy(card.Text) is { Length: > 0 } words ? [new PlaylistItem { Kind = PlaylistItem.CardKind, Text = words, Seconds = each }] : Array.Empty<PlaylistItem>(),
            ],
            Shuffle = shuffle.Checked,
        });
        Save();
        _tray.Refresh();
        Message($"Made “{RoomProfile.TidyName(name.Text)}”. Play it from the tray menu, a schedule or --playlist.");
    }

    /// <summary>Settings: a playlist in order or shuffled (a playing one follows from its next item).</summary>
    public void SetPlaylistShuffle(string name, bool on)
    {
        if (Playlist.Find(Settings.Playlists, name) is not { } playlist || playlist.Shuffle == on) return;
        playlist.Shuffle = on;
        Save();
    }

    public void DeletePlaylist(string name)
    {
        if (Settings.Playlists.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0) return;
        foreach ((PlaylistScreen screen, PlaylistRun run) in _playing.ToList())
            if (string.Equals(run.Playlist.Name, name, StringComparison.OrdinalIgnoreCase)) StopPlaylist(screen, null);
        Settings.Schedule.RemoveAll(e => e.What == ScheduleAction.StartPlaylist && string.Equals(e.Playlist, name, StringComparison.OrdinalIgnoreCase));
        Settings.Normalize();
        Save();
        _tray.Refresh();
        UpdateSchedule();
    }

    /// <summary>Settings: a playlist to start with the app (a screen that runs itself), or none.</summary>
    public void SetPlaylistAtStart(string? name)
    {
        Settings.PlaylistAtStart = name;
        Settings.Normalize();
        Save();
    }

    /// <summary>How long after start-up the start playlist begins: screens and apps settle first.</summary>
    public static readonly TimeSpan PlaylistAtStartDelay = TimeSpan.FromSeconds(8);

    // ───────────────────────────── automatic hiding (FR-88) ─────────────────────────────

    private DateTime _autoHiddenToldUtc = DateTime.MinValue;

    /// <summary>The same thing isn't announced again within this time.</summary>
    public static readonly TimeSpan AutoHiddenQuiet = TimeSpan.FromSeconds(20);

    /// <summary>Settings › Privacy › Hide automatically: what to look for (none switches it off).</summary>
    public void SetAutoHide(SensitiveKinds kinds)
    {
        if ((_policy.AutoHide & ~kinds) != 0)
        {
            kinds |= _policy.AutoHide; // the organisation keeps these on
            Message("Your organisation keeps some details always hidden automatically.", BadgeDot.Warning);
        }
        SensitiveKinds before = Settings.AutoHideKinds;
        Settings.AutoHideKinds = kinds;
        Save();
        if (before == kinds) return;
        if (_session.State == SessionState.Presenting)
        {
            if (_session.Effects is { } effects) effects.AutoHide(kinds, Settings.HiddenPartStyle);
            else if (kinds != SensitiveKinds.None) _ = GuardAsync(ReshowAsync); // the light way can't read or cover anything
        }
        if (_second?.Effects is { } second) second.AutoHide(kinds, Settings.HiddenPartStyle);
        Message(kinds == SensitiveKinds.None ? "Automatic hiding is off."
            : "The details you chose (in Settings › Privacy) are covered on the TV when they're seen. It's a moment behind, so hide parts you know about yourself.", BadgeDot.Accent);
    }

    /// <summary>Settings › Privacy › Hide automatically › Your own words: names and words always covered (with "Words from your list" ticked).</summary>
    public void SetAutoHideWords(IEnumerable<string> words)
    {
        List<string> tidy = SensitiveText.NormalizeWords(words);
        if (tidy.SequenceEqual(Settings.AutoHideWords, StringComparer.Ordinal)) return;
        Settings.AutoHideWords = tidy;
        Save();
        _session.Effects?.AutoHideWords(tidy);
        _second?.Effects?.AutoHideWords(tidy);
        if (tidy.Count > 0 && !Settings.AutoHideKinds.HasFlag(SensitiveKinds.Custom)) SetAutoHide(Settings.AutoHideKinds | SensitiveKinds.Custom); // words given: they're wanted
    }

    /// <summary>"auto-hide": off, or back to what was chosen (everything when nothing was).</summary>
    private void ToggleAutoHide()
    {
        if (Settings.AutoHideKinds != SensitiveKinds.None)
        {
            _lastAutoHide = Settings.AutoHideKinds;
            SetAutoHide(SensitiveKinds.None);
        }
        else SetAutoHide(_lastAutoHide == SensitiveKinds.None ? SensitiveKinds.All : _lastAutoHide);
    }

    private SensitiveKinds _lastAutoHide;

    private void OnAutoHidden(object? sender, string words)
    {
        if (!ReferenceEquals(sender, _session.Effects) || DateTime.UtcNow - _autoHiddenToldUtc < AutoHiddenQuiet) return;
        _autoHiddenToldUtc = DateTime.UtcNow;
        Message($"Covered on the TV automatically: {words}.", BadgeDot.Accent, Button("Turn automatic hiding off", () => SetAutoHide(SensitiveKinds.None), primary: false));
    }

    // ───────────────────────────── transcripts and caption sizes (FR-89) ─────────────────────────────

    private TranscriptWriter? _transcript;
    private (string Text, DateTime Started, DateTime Ended)? _lastWritten;

    private void StartTranscript()
    {
        if (!Settings.SaveTranscript || _transcript is not null) return;
        (TranscriptWriter? writer, string? problem) = TranscriptWriter.Start(Settings.TranscriptFolder ?? SavedFiles.DefaultTranscriptFolder, DateTime.UtcNow);
        _transcript = writer;
        _lastWritten = null;
        if (problem is not null) Message(problem, BadgeDot.Warning);
    }

    /// <summary>After each caption line: anything said in full goes into the transcript.</summary>
    private void WriteTranscript()
    {
        if (_transcript is null || _liveText.LastSaid is not { } said || said == _lastWritten) return;
        _lastWritten = said;
        _transcript.Add(said.Text, said.Started.ToUniversalTime(), said.Ended.ToUniversalTime());
    }

    private void FinishTranscript()
    {
        if (_transcript is not { } writer) return;
        _transcript = null;
        writer.Dispose();
        if (writer.Lines.Count == 0)
        {
            try { File.Delete(writer.Path); } catch (IOException) { } // nothing was said: no empty file left behind
            return;
        }
        Message($"The transcript is saved ({writer.Lines.Count} lines), with subtitles beside it.", BadgeDot.Accent, Button("Show it", () => ShowInFolder(writer.Path)));
    }

    public void SetCaptionsOnTv(bool on)
    {
        Settings.CaptionsOnTv = on;
        Save();
        _session.Effects?.LiveCaption(on && _captions is not null ? _liveText.Shown : null);
        ApplyMarksChange();
    }

    public void SetSaveTranscript(bool on)
    {
        Settings.SaveTranscript = on;
        Save();
        if (_captions is null) return;
        if (on) StartTranscript();
        else FinishTranscript();
    }

    /// <summary>Null = the default folder (Documents\ScreenCast Presenter\Transcripts).</summary>
    public void SetTranscriptFolder(string? folder)
    {
        Settings.TranscriptFolder = folder;
        Save();
    }

    public void SetCaptionLook(CaptionLook look)
    {
        Settings.CaptionLook = look;
        Save();
        _liveText.Limit = look.MaxShown;
        _session.Effects?.CaptionStyle(look);
    }

    // ───────────────────────────── the phone remote (FR-90) ─────────────────────────────

    private RemoteProcess? _remote;
    private RemotePairingForm? _pairing;
    private string? _remoteAddress;
    private string _remoteState = "Starting…";

    public bool PhoneRemoteOn => _remote is not null;

    /// <summary>"Phone remote…": turns it on if needed and shows the pairing code (on this screen only).</summary>
    private void OpenPairing()
    {
        if (_remote is null) SetPhoneRemote(true, quiet: true);
        if (_pairing is { IsDisposed: false })
        {
            _pairing.Activate();
            return;
        }
        var form = new RemotePairingForm();
        form.NewCodeRequested += (_, _) => Guard(() => StartRemote(newKey: true));
        form.TurnOffRequested += (_, _) => Guard(() => SetPhoneRemote(false));
        form.FormClosed += (_, _) => _pairing = null;
        _pairing = form;
        form.Show(_remoteAddress, _remoteState);
        if (PresenterScreen() is { } screen)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.CreateControl();
            Rectangle area = screen.WorkArea;
            form.Location = new Point(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2);
        }
        form.Show();
        form.Activate();
    }

    /// <summary>Settings › General › Phone remote: on starts the remote's helper; off stops it.</summary>
    public void SetPhoneRemote(bool on, bool quiet = false)
    {
        if (on && Blocked(_policy.PhoneRemoteAllowed, "the phone remote")) return;
        Settings.PhoneRemote = on;
        Save();
        if (on && _remote is null) StartRemote(newKey: false);
        else if (!on && _remote is not null)
        {
            StopRemote();
            _pairing?.Close();
            if (!quiet) Message("The phone remote is off.");
        }
    }

    /// <summary>Settings › General › Remote's port: applies at once when the remote is on.</summary>
    public void SetRemotePort(int port)
    {
        if (port is < 1024 or > 65535 || port == Settings.RemotePort) return;
        Settings.RemotePort = port;
        Save();
        if (_remote is not null) StartRemote(newKey: false);
    }

    private void StartRemote(bool newKey)
    {
        StopRemote();
        byte[] key = !newKey && RemoteAuth.Unprotect(Settings.RemoteKey) is { } kept ? kept : RemoteAuth.NewKey();
        if (newKey || Settings.RemoteKey is null || RemoteAuth.Unprotect(Settings.RemoteKey) is null)
        {
            Settings.RemoteKey = RemoteAuth.Protect(key);
            Save();
        }
        _remoteAddress = null;
        _remoteState = "Starting…";
        _pairing?.Show(null, _remoteState);
        (RemoteProcess? remote, string? problem) = RemoteProcess.Start(key, Settings.RemotePort);
        if (remote is null)
        {
            Message(problem ?? "The phone remote couldn't start.", BadgeDot.Warning);
            return;
        }
        _remote = remote;
        remote.Listening += (port, addresses) => Guard(() =>
        {
            if (!ReferenceEquals(_remote, remote)) return;
            _remoteAddress = addresses.Count > 0 ? $"http://{addresses[0]}:{port}/#k={RemoteAuth.Base64Url(key)}" : null;
            _remoteState = addresses.Count > 0 ? "Waiting for your phone…" : "This PC isn't connected to a network your phone can join.";
            _pairing?.Show(_remoteAddress, _remoteState);
            PushRemoteStatus();
        });
        remote.Seen += address => Guard(() =>
        {
            if (!ReferenceEquals(_remote, remote)) return;
            _remoteState = $"Connected: a phone at {address}.";
            _pairing?.SetStatus(_remoteState);
        });
        remote.Command += command => _ = GuardAsync(() => RunRemoteCommandAsync(remote, command));
        remote.Problem += why => Guard(() =>
        {
            if (!ReferenceEquals(_remote, remote)) return;
            _remote = null;
            remote.Dispose();
            _remoteState = why;
            _pairing?.Show(null, why);
            Message(why, BadgeDot.Warning);
        });
        if (newKey) Message("New pairing code: phones paired before no longer work. Scan the new code.");
    }

    private void StopRemote()
    {
        RemoteProcess? remote = _remote;
        _remote = null;
        remote?.Dispose();
    }

    /// <summary>Whether a switch of "--on NAME" is on now; null for a name that isn't one.</summary>
    internal bool? IsSwitchedOn(string what) => what switch
    {
        "spotlight" => _spotlightOn,
        "caption" => _captionOn,
        "live-captions" => LiveCaptionsOn,
        "auto-hide" => Settings.AutoHideKinds != SensitiveKinds.None,
        "laser" => _laserOn,
        "highlight-clicks" => Settings.HighlightClicks,
        "pointer-ring" => Settings.PointerRing,
        "phone-remote" => Settings.PhoneRemote,
        "audience-view" => AudienceViewOpen,
        "outline" => Settings.ShowOutline,
        "badge" => Settings.ShowBadge,
        "widget" => Settings.ShowWidget,
        "show-keys" => Settings.ShowKeys,
        "replay" => Settings.ReplayOn,
        "voice-commands" => Settings.VoiceCommands,
        "notes" => NotesOpen,
        "follow-me" => FollowMeOn,
        "timer" => TimerOn,
        "night-filter" => Settings.Night != NightMode.Off,
        "clicker-keys" => Settings.ClickerKeys,
        "speaker-screen" => SpeakerScreenOpen,
        "rehearse" => Rehearsing,
        _ => null,
    };

    /// <summary>"--on NAME" / "--off NAME": set as asked (nothing when it already is). Why it couldn't be, or null.</summary>
    private async Task<string?> SwitchAsync(string what, bool on)
    {
        if (IsSwitchedOn(what) is not { } now) return $"“{what}” isn't something that can be switched on or off.";
        if (now == on) return null;
        switch (what)
        {
            case "caption" when on && Settings.CaptionName.Length == 0:
                _ = RunCommandAsync("caption"); // asks for the name first: don't hold up the command line
                return null;
            case "phone-remote": SetPhoneRemote(on); break;
            case "highlight-clicks": SetHighlightClicks(on); break;
            case "pointer-ring": SetPointerRing(on); break;
            case "outline": SetShowOutline(on); break;
            case "badge": SetShowBadge(on); break;
            case "widget": SetShowWidget(on); break;
            case "show-keys": SetShowKeys(on); break;
            case "replay": SetReplay(on); break;
            case "voice-commands": SetVoiceCommands(on); break;
            case "notes": ToggleNotes(); break;
            case "timer" when on: StartTimer(Settings.TimerMinutes); break;
            case "timer": StopTimer(null); break;
            case "night-filter": SetNightFilter(on ? NightMode.Soften : NightMode.Off); break;
            case "clicker-keys": SetClickerKeys(on, Settings.ClickerArrows); break;
            default: await RunAsync(what); break; // the others are toggles of their own name
        }
        return IsSwitchedOn(what) == on ? null : LastCommandMessage($"Couldn't switch {what} {(on ? "on" : "off")}.");
    }

    /// <summary>A command from the phone (checked by the helper and again here): run, then what's on goes back.</summary>
    private async Task RunRemoteCommandAsync(RemoteProcess remote, string command)
    {
        if (!ReferenceEquals(_remote, remote) || !RemoteCommands.IsAllowed(command)) return;
        Diag("remote: " + command);
        List<string> said = _remoteMessages = [];
        try
        {
            switch (command)
            {
                case RemoteCommands.Status: break;
                case "next-slide": SendSlide(forward: true); break;
                case "previous-slide": SendSlide(forward: false); break;
                case "caption" when Settings.CaptionName.Length == 0 && !_captionOn:
                    Message("Type the name for the banner on the PC first (the tray menu's name banner): the phone can't type it."); // never a window from the phone
                    break;
                case string p when RemoteCommands.Number(p, RemoteCommands.Playlist, Playlist.Max) is { } n:
                    if (n <= Settings.Playlists.Count) await StartPlaylistAsync(Settings.Playlists[n - 1].Name);
                    else Message("That playlist isn't there any more.");
                    break;
                default: await RunAsync(command); break;
            }
        }
        finally
        {
            if (ReferenceEquals(_remoteMessages, said)) _remoteMessages = null;
        }
        PushRemoteStatus(command == RemoteCommands.Status ? null : said.LastOrDefault());
    }

    /// <summary>Messages shown while a phone's command runs: the last goes back to the phone.</summary>
    private List<string>? _remoteMessages;

    /// <summary>What's on, and the favourites' and scenes' names, for the phone's page.</summary>
    /// <param name="note">What the PC said about the command just run (shown on the phone), or null.</param>
    internal string RemoteStatusJson(string? note = null)
    {
        var json = new JsonObject
        {
            ["status"] = _session.State == SessionState.Off ? "Not showing anything"
                : _session.IsBlanked ? $"{OutputNameCapitalised()} is blank"
                : _session.Source is { } s ? $"{(_session.IsFrozen ? "Frozen" : "Showing")}: {SourceName(s)}" : $"{OutputNameCapitalised()} shows a blank screen",
            ["favourites"] = new JsonArray([.. Settings.Favourites.Select(f => (JsonNode?)(f is null ? null : JsonValue.Create(TrayMenuPlan.Shorten(f.Title.Length > 0 ? f.Title : f.AppName, 40))))]),
            ["scenes"] = new JsonArray([.. Settings.Scenes.Select((sc, i) => (JsonNode?)(sc is null ? null : JsonValue.Create(TrayMenuPlan.Shorten(sc.Title(i + 1), 40))))]),
            ["playlist"] = PlayingOn(PlaylistScreen.Main) ?? PlayingOn(PlaylistScreen.Second),
            ["playlists"] = new JsonArray([.. Settings.Playlists.Select(p => (JsonNode?)JsonValue.Create(TrayMenuPlan.Shorten(p.Name, 40)))]),
        };
        if (note is not null) json["note"] = TrayMenuPlan.Shorten(note, 200);
        if (Settings.NotesOnPhone && NotesForPhone() is { } notes) json["notes"] = notes;
        return json.ToJsonString();
    }

    private void PushRemoteStatus(string? note = null) => _remote?.SendStatus(RemoteStatusJson(note));

    /// <summary>Next or previous slide in what's shown (Page Down / Page Up, like a clicker).</summary>
    private void SendSlide(bool forward, bool fromClicker = false)
    {
        if (_session.Source is { IsDocument: true })
        {
            _ = GuardAsync(() => TurnPageAsync(forward ? 1 : -1)); // FR-115: the PDF's page turns
            return;
        }
        if (JumpVideo(forward ? 1 : -1)) return; // FR-116: a video jumps ten seconds
        if (fromClicker && _session.State == SessionState.Presenting && _session.Source is { IsWindow: true } window)
        {
            SlideKeys.Post(window.Handle, forward); // taken as a clicker key: posted, never typed (it would be taken again)
            return;
        }
        if (_session.State != SessionState.Presenting || _session.Source is not { IsWindow: true } source)
        {
            Message("Next and previous slide work on a window that's shown, like PowerPoint or a PDF.");
            return;
        }
        if (!SlideKeys.Send(source.Handle, forward)) Message($"“{SourceName(source)}” didn't take the key.");
    }

    /// <summary>Settings: the idle screen shows the message and logo (FR-16).</summary>
    public void SetIdleShowsMessage(bool on)
    {
        Settings.IdleShowsMessage = on;
        Save();
    }

    // ───────────────────────────── shared ─────────────────────────────

    private void StartExtend()
    {
        ApplyPolicy();
        TidyCards();
        TidyReplayParts();
        _liveText.Limit = Settings.CaptionLook.MaxShown;
        if (Settings.PhoneRemote) StartRemote(newKey: false);
        if (Settings.PlaylistAtStart is { } name)
        {
            var timer = new System.Windows.Forms.Timer { Interval = (int)PlaylistAtStartDelay.TotalMilliseconds };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                if (!_quitting && PlayingOn(PlaylistScreen.Main) is null) _ = GuardAsync(() => StartPlaylistAsync(name));
            };
            timer.Start();
        }
    }

    /// <summary>A new engine took over: caption look and automatic hiding go to it too.</summary>
    private void ReplayExtend(IEffectsEngine effects)
    {
        effects.CaptionStyle(Settings.CaptionLook);
        effects.AutoHidden -= OnAutoHidden;
        effects.AutoHidden += OnAutoHidden;
        bool startedWithIt = effects is RemoteWgcEngine remote && remote.StartAutoHide == Settings.AutoHideKinds
            && (remote.StartAutoWords ?? []).SequenceEqual(Settings.AutoHideWords, StringComparer.Ordinal);
        if (Settings.AutoHideKinds != SensitiveKinds.None && !startedWithIt)
        {
            effects.AutoHideWords(Settings.AutoHideWords);
            effects.AutoHide(Settings.AutoHideKinds, Settings.HiddenPartStyle);
        }
    }

    private void DisposeExtend()
    {
        if (ReplayRunning) _replay!.Stop();
        _timeline?.Dispose();
        _timeline = null;
        _caret?.Dispose();
        _caret = null;
        _keyTimer.Dispose();
        _keyClear.Dispose();
        _voice?.Dispose();
        _voice = null;
        _notesWindow?.Dispose();
        StopPlaylists(null);
        StopRemote();
        FinishTranscript();
        _pairing?.Dispose();
    }
}
