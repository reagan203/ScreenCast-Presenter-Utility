namespace ScreenCastPresenter.Core;

internal enum ShortcutGroup { Presenting, OnTheTv, Favourites, Crop, Recording, OnMyScreen }

/// <summary>One thing the user can do from a shortcut, the palette or the tray (FR-21, FR-25).</summary>
/// <param name="Available">False for features of a later milestone: listed, but never registered, so they don't steal keys.</param>
internal sealed record ShortcutAction(
    string Id,
    string Name,
    ShortcutGroup Group,
    Hotkey? Default,
    bool Available = true,
    string Keywords = "");

internal static class ShortcutCatalog
{
    public const string PauseShortcutsId = "pause-shortcuts";

    public static IReadOnlyList<ShortcutAction> All { get; } = Build();

    private static readonly Dictionary<string, ShortcutAction> ById = All.ToDictionary(a => a.Id);

    public static ShortcutAction? Find(string id) => ById.GetValueOrDefault(id);

    public static string ShowFavouriteId(int slot) => $"favourite-{slot}";
    public static string SaveFavouriteId(int slot) => $"save-favourite-{slot}";

    private static List<ShortcutAction> Build()
    {
        static Hotkey C(Keys k) => Hotkey.CtrlAlt(k);
        static Hotkey CS(Keys k) => Hotkey.CtrlAlt(k).WithShift();

        var list = new List<ShortcutAction>
        {
            new("picker", "Choose what to show", ShortcutGroup.Presenting, C(Keys.P), Keywords: "present choose window picker open"),
            new("palette", "Search actions and help", ShortcutGroup.Presenting, C(Keys.Enter), Keywords: "search command palette help how"),
            new("present-active", "Show the window I am in", ShortcutGroup.Presenting, C(Keys.W), Keywords: "active current"),
            new("previous-source", "Show the previous window again", ShortcutGroup.Presenting, C(Keys.Back), Keywords: "last undo back source"),
            new("next-window", "Show the next window", ShortcutGroup.Presenting, C(Keys.PageDown), Keywords: "next cycle"),
            new("previous-window", "Show the window before", ShortcutGroup.Presenting, C(Keys.PageUp), Keywords: "previous cycle"),
            new("stop", "Stop showing", ShortcutGroup.Presenting, C(Keys.S), Keywords: "end close"),

            new("blank", "Blank the TV / show again", ShortcutGroup.OnTheTv, C(Keys.B), Keywords: "black hide"),
            new("blank-message", "Blank with a message", ShortcutGroup.OnTheTv, CS(Keys.B), Keywords: "right back break"),
            new("freeze", "Freeze the TV / go live", ShortcutGroup.OnTheTv, C(Keys.F), Keywords: "pause still"),
            new("dim", "Dim the TV (0–40%)", ShortcutGroup.OnTheTv, C(Keys.D), Keywords: "night darker brightness"),
            new("move-output", "Show on the next screen", ShortcutGroup.OnTheTv, C(Keys.M), Keywords: "monitor display output move"),
            new("switch-output", "Switch where it shows: TV, Zoom screen, window", ShortcutGroup.OnTheTv, C(Keys.O), Keywords: "zoom floating window virtual share output"),
        };

        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
            list.Add(new(ShowFavouriteId(slot), $"Show favourite {slot}", ShortcutGroup.Favourites, C(Keys.D0 + slot)));
        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
            list.Add(new(SaveFavouriteId(slot), $"Save what's showing as favourite {slot}", ShortcutGroup.Favourites, CS(Keys.D0 + slot)));

        list.AddRange(
        [
            new("crop", "Show only part of it (crop)", ShortcutGroup.Crop, C(Keys.C), Keywords: "part area region zoom crop"),
            new("remove-crop", "Show all of it again", ShortcutGroup.Crop, CS(Keys.C), Keywords: "uncrop whole all remove crop"),

            new("record", "Start / stop recording", ShortcutGroup.Recording, C(Keys.R), Keywords: "video capture webinar record"),
            new("pause-recording", "Pause / resume recording", ShortcutGroup.Recording, CS(Keys.R), Keywords: "hold break"),
            new("screenshot", "Take a screenshot", ShortcutGroup.Recording, CS(Keys.S), Keywords: "snip capture image"),

            new("toggle-outline", "Yellow outline on / off", ShortcutGroup.OnMyScreen, C(Keys.Y), Keywords: "border frame"),
            new("toggle-badge", "Status pill on / off", ShortcutGroup.OnMyScreen, C(Keys.I), Keywords: "pill status badge"),
            new("toggle-widget", "Now-showing bar on / off", ShortcutGroup.OnMyScreen, C(Keys.N), Keywords: "widget bar teams sharing toolbar go to"),
            new("cheat-sheet", "Show all shortcuts", ShortcutGroup.OnMyScreen, C(Keys.K), Keywords: "help keys list"),
            new(PauseShortcutsId, "Pause all shortcuts", ShortcutGroup.OnMyScreen, CS(Keys.P), Keywords: "disable game"),
            new("toggle-theme", "Switch light / dark", ShortcutGroup.OnMyScreen, null, Keywords: "appearance theme mode"),
        ]);
        return list;
    }
}

internal enum ShortcutStatus
{
    /// <summary>Resolved and waiting to be registered.</summary>
    Pending,
    Active,
    /// <summary>Switched off by the user, or no default.</summary>
    Off,
    /// <summary>Another app (or another SCP action) already owns the keys.</summary>
    Taken,
    /// <summary>All shortcuts are paused or switched off.</summary>
    Paused,
    /// <summary>The feature arrives in a later milestone.</summary>
    Unavailable,
    /// <summary>The stored text isn't a valid shortcut.</summary>
    Invalid,
}

internal sealed record ShortcutBinding(ShortcutAction Action, Hotkey? Hotkey, ShortcutStatus Status, string? Note);

/// <summary>
/// Works out which keys each action gets (FR-21, FR-21a), without touching Windows, so every rule
/// is unit-testable. <see cref="HotkeyManager"/> then registers the Pending ones.
/// </summary>
internal static class ShortcutResolver
{
    public static List<ShortcutBinding> Resolve(
        IReadOnlyList<ShortcutAction> actions,
        IReadOnlyDictionary<string, string> overrides,
        bool enabled,
        bool paused,
        Func<Hotkey, bool> typesCharacter)
    {
        var result = new List<ShortcutBinding>(actions.Count);
        var claimed = new Dictionary<Hotkey, ShortcutAction>();

        foreach (ShortcutAction action in actions)
        {
            Hotkey? hotkey;
            string? note = null;

            if (overrides.TryGetValue(action.Id, out string? custom))
            {
                if (string.IsNullOrWhiteSpace(custom))
                {
                    result.Add(new(action, null, ShortcutStatus.Off, "Switched off"));
                    continue;
                }
                if (!Hotkey.TryParse(custom, out Hotkey parsed))
                {
                    result.Add(new(action, null, ShortcutStatus.Invalid, $"“{custom}” isn't a shortcut ScreenCast Presenter can use"));
                    continue;
                }
                hotkey = parsed;
                if (typesCharacter(parsed)) note = "These keys also type a character on your keyboard";
            }
            else
            {
                hotkey = action.Default;
                if (hotkey is { } d && typesCharacter(d))
                {
                    // AltGr layouts: Ctrl+Alt+key types a character (e.g. € on UK, { on German).
                    Hotkey moved = d.WithShift();
                    if (typesCharacter(moved) || moved == d)
                    {
                        result.Add(new(action, null, ShortcutStatus.Off, $"{d} types a character on your keyboard, so it was switched off"));
                        continue;
                    }
                    note = $"Moved: {d} types a character on your keyboard";
                    hotkey = moved;
                }
            }

            if (hotkey is null)
            {
                result.Add(new(action, null, ShortcutStatus.Off, null));
                continue;
            }
            if (!action.Available)
            {
                result.Add(new(action, hotkey, ShortcutStatus.Unavailable, "Coming in a later update"));
                continue;
            }

            // Two actions can't share keys; the first in the list keeps them.
            if (claimed.TryGetValue(hotkey.Value, out ShortcutAction? owner))
            {
                result.Add(new(action, hotkey, ShortcutStatus.Taken, $"Also set for “{owner.Name}”"));
                continue;
            }

            bool isPauseKey = action.Id == ShortcutCatalog.PauseShortcutsId;
            if (!enabled || (paused && !isPauseKey))
            {
                result.Add(new(action, hotkey, ShortcutStatus.Paused, note));
                continue;
            }

            claimed[hotkey.Value] = action;
            result.Add(new(action, hotkey, ShortcutStatus.Pending, note));
        }
        return result;
    }
}
