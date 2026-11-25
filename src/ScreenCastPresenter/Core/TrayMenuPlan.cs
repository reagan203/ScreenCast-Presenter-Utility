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

    /// <summary>When nothing is shown: the last thing shown, to bring back in one click.</summary>
    public string? ShowAgainTitle { get; init; }

    public IReadOnlyList<TrayFavourite> Favourites { get; init; } = [];
    public IReadOnlyList<TrayScreen> Screens { get; init; } = [];
    public bool WindowChosen { get; init; }
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
        "help", "settings", "settings:output", "settings:appearance", "quit", "record-output", "record-picker",
        "stop-recording", "pause-recording", "open-recordings", "output-window", "zoom", "stop-after:never", "show-again",
    };

    /// <summary>A command something will actually run (tests check every menu line and help topic).</summary>
    public static bool IsKnownCommand(string command) =>
        AppCommands.Contains(command)
        || ShortcutCatalog.Find(command) is not null
        || command.StartsWith("output:", StringComparison.Ordinal) && command.Length > "output:".Length
        || command.StartsWith("stop-after:", StringComparison.Ordinal) && int.TryParse(command.AsSpan("stop-after:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

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
            }
            menu.Add(new("Stop showing", "stop", Shortcut: "stop"));
        }

        menu.Add(MenuEntry.Separator);
        menu.Add(new("Take a screenshot", "screenshot", Shortcut: "screenshot"));
        AddRecording(menu, s);
        menu.Add(new("Open my recordings", "open-recordings"));

        menu.Add(MenuEntry.Separator);
        menu.Add(MenuEntry.Submenu("Show on", ShowOn(s)));
        if (s.Favourites.Count > 0 || presenting) menu.Add(MenuEntry.Submenu("Favourites", Favourites(s, presenting)));

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
            var limits = new List<MenuEntry> { new("Only when the window closes or I stop it", "stop-after:never", Checked: s.StopAfter is null) };
            foreach (int minutes in StopAfterMinutes)
            {
                TimeSpan span = TimeSpan.FromMinutes(minutes);
                limits.Add(new($"After {DurationWords(span)}", $"stop-after:{minutes}", Enabled: s.RecordingElapsed < span, Checked: s.StopAfter == span));
            }
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

    private static List<MenuEntry> ShowOn(TrayMenuState s)
    {
        var items = s.Screens.Select(screen => new MenuEntry(screen.Text, "output:" + screen.DeviceName, Checked: !s.WindowChosen && screen.Chosen)).ToList();
        items.Add(new("A window (to share in a meeting)", "output-window", Checked: s.WindowChosen, Shortcut: "switch-output"));
        items.Add(MenuEntry.Separator);
        items.Add(new("How to share in Zoom or Teams…", "zoom"));
        return items;
    }

    private static List<MenuEntry> Favourites(TrayMenuState s, bool presenting)
    {
        var items = s.Favourites.OrderBy(f => f.Slot)
            .Select(f => new MenuEntry($"{f.Slot}   {Shorten(f.Title)} · {f.AppName}", ShortcutCatalog.ShowFavouriteId(f.Slot), Shortcut: ShortcutCatalog.ShowFavouriteId(f.Slot)))
            .ToList();
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
