using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.Core;

internal enum MenuKind { Item, Label, Separator }

/// <summary>
/// One line of the tray menu. <see cref="Command"/> says what it does (an action id from the
/// shortcut catalog, or one of the tray's own commands such as "settings" or "output:\\.\DISPLAY2");
/// <see cref="Shortcut"/> is the action whose keys are shown next to it.
/// </summary>
internal sealed record MenuEntry(
    string Text,
    string? Command = null,
    MenuKind Kind = MenuKind.Item,
    bool Enabled = true,
    bool Checked = false,
    string? Shortcut = null,
    IReadOnlyList<MenuEntry>? Children = null)
{
    public static MenuEntry Separator { get; } = new("", Kind: MenuKind.Separator);

    public static MenuEntry Label(string text) => new(text, Kind: MenuKind.Label, Enabled: false);

    public static MenuEntry Submenu(string text, IReadOnlyList<MenuEntry> children) => new(text, Children: children);
}

/// <summary>A screen in "Show on": its command ("output:" + device name), label, and whether it's chosen.</summary>
internal sealed record TrayScreen(string DeviceName, string Text, bool Chosen);

/// <summary>A saved favourite as the tray lists it.</summary>
internal sealed record TrayFavourite(int Slot, string Title, string AppName);

/// <summary>Everything the tray menu depends on, captured when it opens.</summary>
internal sealed record TrayMenuState
{
    public SessionState Session { get; init; }
    public bool Blanked { get; init; }
    public bool Frozen { get; init; }
    public bool Cropped { get; init; }

    /// <summary>The first line: "● Showing on TV: Slides".</summary>
    public string Status { get; init; } = "";

    /// <summary>Where the audience looks: "the TV", "the Zoom screen", "the window", "Screen 2".</summary>
    public string Where { get; init; } = "the TV";

    public RecordingStatus Recording { get; init; } = RecordingStatus.Off;
    public string? RecordingLine { get; init; }
    public TimeSpan RecordingElapsed { get; init; }
    public TimeSpan? StopAfter { get; init; }

    /// <summary>FR-59: the recording stops when the clock reaches this; null for none.</summary>
    public DateTime? StopAt { get; init; }

    /// <summary>When nothing is shown: the last thing shown, to bring back in one click.</summary>
    public string? ShowAgainTitle { get; init; }

    public IReadOnlyList<TrayFavourite> Favourites { get; init; } = [];
    public IReadOnlyList<TrayScreen> Screens { get; init; } = [];
    public bool WindowChosen { get; init; }

    // What's on the TV besides the picture (FR-64–FR-72).
    public bool Zoomed { get; init; }
    public bool Drawing { get; init; }
    public bool LaserOn { get; init; }
    public bool ClicksShown { get; init; }
    public bool HasInset { get; init; }

    /// <summary>The last minute is kept (FR-106), and whether it's running now.</summary>
    public bool ReplayOn { get; init; }
    public bool ReplayRunning { get; init; }

    /// <summary>Voice commands are on; the notes window is open.</summary>
    public bool VoiceCommands { get; init; }
    public bool NotesOpen { get; init; }

    /// <summary>Shortcuts pressed show on the TV.</summary>
    public bool ShowKeys { get; init; }

    /// <summary>Pictures shown beside the main one (split screen).</summary>
    public int SplitCount { get; init; }
    public bool Slideshow { get; init; }

    // The stage tools (FR-74–FR-85).
    public bool HasHiddenParts { get; init; }
    public bool SpotlightOn { get; init; }
    public bool CaptionOn { get; init; }
    public bool HasQr { get; init; }
    public bool LiveCaptions { get; init; }
    public bool AudienceView { get; init; }

    /// <summary>What's ready to show next (FR-76); null when nothing is.</summary>
    public string? NextTitle { get; init; }

    /// <summary>The second TV (FR-85): whether it's on, and whether one could be used.</summary>
    public bool SecondTvOn { get; init; }
    public bool SecondTvPossible { get; init; }

    public IReadOnlyList<string> Rooms { get; init; } = [];

    // Running the room (FR-86–FR-90).
    /// <summary>Saved scenes by slot: (slot, name).</summary>
    public IReadOnlyList<(int Slot, string Name)> Scenes { get; init; } = [];
    public IReadOnlyList<string> Playlists { get; init; } = [];

    /// <summary>A playlist is playing on some screen.</summary>
    public bool PlaylistPlaying { get; init; }
    public bool PhoneRemote { get; init; }
    public bool AutoHide { get; init; }
}

/// <summary>
/// The tray menu, built from the state when it opens. It holds only what applies right now:
/// presenting controls only while presenting, recording controls only while recording, favourites
/// only once there are some. Everything else (appearance, dimming, indicators, shortcuts, how
/// windows are captured) lives in Settings, so the menu stays short enough to read at a glance.
/// </summary>
internal static class TrayMenuPlan
{
    public static readonly int[] StopAfterMinutes = [30, 60, 90, 120, 180, 240];

    /// <summary>
    /// The app's own commands, handled by ScpApp.RunCommandAsync (keep the two in step); anything
    /// else is an action id from the shortcut catalog, "output:" + a screen, or "stop-after:" + minutes.
    /// </summary>
    public static IReadOnlySet<string> AppCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "help", "settings", "settings:output", "settings:appearance", "settings:privacy", "quit", "record-output", "record-picker",
        "stop-recording", "pause-recording", "open-recordings", "output-window", "zoom", "stop-after:never", "show-again",
        "settings:rooms", "stop-second-tv", "stop-at",
    };

    /// <summary>A command something will actually run (tests check every menu line and help topic).</summary>
    public static bool IsKnownCommand(string command) =>
        AppCommands.Contains(command)
        || ShortcutCatalog.Find(command) is not null
        || command.StartsWith("output:", StringComparison.Ordinal) && command.Length > "output:".Length
        || command.StartsWith("room:", StringComparison.Ordinal) && command.Length > "room:".Length
        || command.StartsWith("playlist:", StringComparison.Ordinal) && command.Length > "playlist:".Length
        || command.StartsWith("stop-after:", StringComparison.Ordinal) && int.TryParse(command.AsSpan("stop-after:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
        || command.StartsWith("countdown:", StringComparison.Ordinal) && int.TryParse(command.AsSpan("countdown:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

    public static IReadOnlyList<MenuEntry> Build(TrayMenuState s)
    {
        var menu = new List<MenuEntry> { MenuEntry.Label(s.Status), MenuEntry.Separator };
        bool presenting = s.Session == SessionState.Presenting;

        if (s.Session == SessionState.Off)
        {
            menu.Add(new("Show something…", "picker", Shortcut: "picker"));
            if (s.ShowAgainTitle is { Length: > 0 } title) menu.Add(new($"Show “{Shorten(title)}” again", "show-again"));
        }
        else
        {
            menu.Add(new(presenting ? "Show something else…" : "Choose what to show…", "picker", Shortcut: "picker"));
            if (presenting)
            {
                menu.Add(new(s.Blanked ? $"Show {s.Where} again" : $"Blank {s.Where}", "blank", Shortcut: "blank"));
                menu.Add(new(s.Frozen ? "Go live again" : $"Freeze {s.Where}", "freeze", Shortcut: "freeze"));
                menu.Add(s.Cropped ? new("Show all of it again", "remove-crop", Shortcut: "remove-crop") : new("Show only part of it…", "crop", Shortcut: "crop"));
                menu.Add(MenuEntry.Submenu($"On {s.Where}", OnTheTv(s)));
            }
            if (s.NextTitle is { Length: > 0 } next) menu.Add(new($"Show “{Shorten(next, 30)}” now", "go", Shortcut: "go"));
            menu.Add(new("See what the audience sees", "audience-view", Checked: s.AudienceView, Shortcut: "audience-view"));
            menu.Add(new("Stop showing", "stop", Shortcut: "stop"));
        }

        menu.Add(MenuEntry.Separator);
        menu.Add(new("Take a screenshot", "screenshot", Shortcut: "screenshot"));
        AddRecording(menu, s);
        if (s.ReplayRunning) menu.Add(new("Save the last minute", "save-replay", Shortcut: "save-replay"));
        if (presenting || s.ReplayOn) menu.Add(new("Keep the last minute", "replay", Checked: s.ReplayOn, Shortcut: "replay")); // the idle menu stays short
        menu.Add(new("Open my recordings", "open-recordings"));

        menu.Add(MenuEntry.Separator);
        menu.Add(MenuEntry.Submenu("Show on", ShowOn(s)));
        if (s.Favourites.Count > 0 || presenting) menu.Add(MenuEntry.Submenu("Favourites", Favourites(s, presenting)));
        menu.Add(MenuEntry.Submenu("Rooms, scenes and playlists", Rooms(s, presenting)));

        menu.Add(MenuEntry.Separator);
        menu.Add(new("Search and help…", "help", Shortcut: "palette"));
        menu.Add(new("Settings…", "settings"));
        menu.Add(new("Quit", "quit"));
        return menu;
    }

    private static void AddRecording(List<MenuEntry> menu, TrayMenuState s)
    {
        if (s.Recording != RecordingStatus.Off)
        {
            if (s.RecordingLine is { Length: > 0 } line) menu.Add(MenuEntry.Label(line));
            menu.Add(new(s.Recording == RecordingStatus.Paused ? "Resume recording" : "Pause recording", "pause-recording",
                Enabled: s.Recording is RecordingStatus.Recording or RecordingStatus.Paused, Shortcut: "pause-recording"));
            menu.Add(new("Stop recording", "stop-recording", Enabled: s.Recording != RecordingStatus.Stopping, Shortcut: "record"));
            var limits = new List<MenuEntry> { new("Only when the window closes or I stop it", "stop-after:never", Checked: s.StopAfter is null && s.StopAt is null) };
            foreach (int minutes in StopAfterMinutes)
            {
                TimeSpan span = TimeSpan.FromMinutes(minutes);
                limits.Add(new($"After {DurationWords(span)}", $"stop-after:{minutes}", Enabled: s.RecordingElapsed < span, Checked: s.StopAfter == span));
            }
            limits.Add(new(s.StopAt is { } at ? $"At {at:HH:mm} (change…)" : "At a set time…", "stop-at", Checked: s.StopAt is not null));
            menu.Add(MenuEntry.Submenu("Stop automatically", limits));
            return;
        }
        if (s.Session != SessionState.Off)
        {
            menu.Add(new($"Record what {s.Where} shows", "record-output", Shortcut: "record"));
            menu.Add(new("Record something else…", "record-picker"));
        }
        else
        {
            menu.Add(new("Record…", "record-picker", Shortcut: "record"));
        }
    }

    /// <summary>Countdown lengths offered in the menu ("countdown:" + minutes).</summary>
    public static readonly int[] CountdownMinutes = [1, 2, 5, 10, 15, 20];

    /// <summary>
    /// What can go on the TV besides the picture, while presenting: zoom, drawing, the laser, click
    /// rings, a small picture in a corner, and blanking with a countdown.
    /// </summary>
    private static List<MenuEntry> OnTheTv(TrayMenuState s)
    {
        var countdown = CountdownMinutes.Select(m => new MenuEntry(m == 1 ? "1 minute" : $"{m} minutes", $"countdown:{m}")).ToList();
        return
        [
            new(s.Zoomed ? "Zoom out" : "Zoom in around the mouse", "zoom-in", Shortcut: "zoom-in"),
            new(s.Drawing ? "Stop drawing" : "Draw on it…", "draw", Shortcut: "draw"),
            new("Laser", "laser", Checked: s.LaserOn, Shortcut: "laser"),
            new("Show my clicks", "highlight-clicks", Checked: s.ClicksShown, Shortcut: "highlight-clicks"),
            new(s.HasInset ? "Take the small picture off" : "Small picture in a corner…", "inset", Shortcut: "inset"),
            new(s.SplitCount > 0 ? "Back to one picture" : "Split screen: show something beside it…", "split-screen", Shortcut: "split-screen"),
            new("Spotlight", "spotlight", Checked: s.SpotlightOn, Shortcut: "spotlight"),
            new(s.CaptionOn ? "Take my name off" : "My name along the bottom", "caption", Shortcut: "caption"),
            new(s.HasQr ? "Take the QR code off" : "A QR code (link or Wi-Fi)…", "qr", Shortcut: "qr"),
            new("Live captions", "live-captions", Checked: s.LiveCaptions, Shortcut: "live-captions"),
            new("Show my shortcuts", "show-keys", Checked: s.ShowKeys, Shortcut: "show-keys"),
            MenuEntry.Separator,
            new("Hide part of it…", "hide-part", Shortcut: "hide-part"),
            new("Hide private details automatically", "auto-hide", Checked: s.AutoHide, Shortcut: "auto-hide"),
            new("Show the hidden parts", "show-hidden", Enabled: s.HasHiddenParts, Shortcut: "show-hidden"),
            MenuEntry.Separator,
            MenuEntry.Submenu("Blank with a countdown", countdown),
        ];
    }

    /// <summary>
    /// Running the room: rooms (FR-82), scenes (FR-86), playlists (FR-87) and the phone remote
    /// (FR-90), each one click; saving what's on as a room or a scene.
    /// </summary>
    private static List<MenuEntry> Rooms(TrayMenuState s, bool presenting)
    {
        var items = s.Rooms.Select(name => new MenuEntry(Shorten(name), "room:" + name)).ToList();
        if (s.Scenes.Count > 0 || presenting)
        {
            if (items.Count > 0) items.Add(MenuEntry.Separator);
            items.AddRange(s.Scenes.Select(sc => new MenuEntry($"{sc.Slot}   {Shorten(sc.Name, 34)}", ShortcutCatalog.SceneId(sc.Slot), Shortcut: ShortcutCatalog.SceneId(sc.Slot))));
            if (presenting) items.Add(new("Save this as a scene…", "save-scene", Shortcut: "save-scene"));
        }
        if (s.Playlists.Count > 0)
        {
            items.Add(MenuEntry.Separator);
            items.AddRange(s.Playlists.Select(p => new MenuEntry($"Play “{Shorten(p, 30)}”", "playlist:" + p)));
            if (s.PlaylistPlaying) items.Add(new("Stop the playlist", "stop-playlist", Shortcut: "stop-playlist"));
        }
        items.Add(MenuEntry.Separator);
        items.Add(new("Show a picture…", "show-picture", Shortcut: "show-picture"));
        items.Add(new("Show a card…", "show-card", Shortcut: "show-card"));
        items.Add(new("Phone remote…", "phone-remote", Checked: s.PhoneRemote, Shortcut: "phone-remote"));
        items.Add(new("Voice commands", "voice-commands", Checked: s.VoiceCommands, Shortcut: "voice-commands"));
        items.Add(new("My notes", "notes", Checked: s.NotesOpen, Shortcut: "notes"));
        items.Add(new("Turn the TV on", "tv-on", Shortcut: "tv-on"));
        items.Add(new("Turn the TV off", "tv-off", Shortcut: "tv-off"));
        items.Add(new("Save this setup as a room…", "save-room", Shortcut: "save-room"));
        items.Add(new("Rooms, scenes and playlists…", "settings:rooms"));
        return items;
    }

    private static List<MenuEntry> ShowOn(TrayMenuState s)
    {
        var items = s.Screens.Select(screen => new MenuEntry(screen.Text, "output:" + screen.DeviceName, Checked: !s.WindowChosen && screen.Chosen)).ToList();
        items.Add(new("A window (to share in a meeting)", "output-window", Checked: s.WindowChosen, Shortcut: "switch-output"));
        items.Add(MenuEntry.Separator);
        if (s.SecondTvOn) items.Add(new("Stop the second TV", "stop-second-tv"));
        else items.Add(new("Second TV: show something…", "second-tv", Enabled: s.SecondTvPossible, Shortcut: "second-tv"));
        items.Add(MenuEntry.Separator);
        items.Add(new("How to share in Zoom or Teams…", "zoom"));
        return items;
    }

    private static List<MenuEntry> Favourites(TrayMenuState s, bool presenting)
    {
        var items = s.Favourites.OrderBy(f => f.Slot)
            .Select(f => new MenuEntry($"{f.Slot}   {Shorten(f.Title)} · {f.AppName}", ShortcutCatalog.ShowFavouriteId(f.Slot), Shortcut: ShortcutCatalog.ShowFavouriteId(f.Slot)))
            .ToList();
        if (s.Favourites.Count > 0)
        {
            items.Add(MenuEntry.Separator);
            items.Add(new(s.Slideshow ? "Stop the slideshow" : "Show them one after another", "slideshow", Shortcut: "slideshow")); // FR-69
        }
        if (!presenting) return items;
        if (items.Count > 0) items.Add(MenuEntry.Separator);
        var save = new List<MenuEntry>();
        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
        {
            TrayFavourite? taken = s.Favourites.FirstOrDefault(f => f.Slot == slot);
            save.Add(new(taken is null ? $"Favourite {slot}" : $"Favourite {slot} (replaces “{Shorten(taken.Title, 24)}”)",
                ShortcutCatalog.SaveFavouriteId(slot), Shortcut: ShortcutCatalog.SaveFavouriteId(slot)));
        }
        items.Add(MenuEntry.Submenu("Save what's showing as", save));
        return items;
    }

    /// <summary>"30 minutes", "1 hour", "1½ hours", "2 hours".</summary>
    public static string DurationWords(TimeSpan t)
    {
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} minutes";
        double hours = t.TotalHours;
        string number = hours % 1 == 0.5 ? $"{(int)hours}½" : ((int)Math.Round(hours)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return number == "1" ? "1 hour" : $"{number} hours";
    }

    public static string Shorten(string? text, int max = 40) => text is null ? "" : text.Length > max ? text[..(max - 1)] + "…" : text;

    /// <summary>Every entry, submenus included, depth first (for tests and the shortcut lookups).</summary>
    public static IEnumerable<MenuEntry> Flatten(IEnumerable<MenuEntry> entries)
    {
        foreach (MenuEntry e in entries)
        {
            yield return e;
            if (e.Children is { } children)
            {
                foreach (MenuEntry child in Flatten(children)) yield return child;
            }
        }
    }
}
