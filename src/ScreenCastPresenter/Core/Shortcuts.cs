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
    public static string SceneId(int slot) => $"scene-{slot}";

    private static List<ShortcutAction> Build()
    {
        static Hotkey C(Keys k) => Hotkey.CtrlAlt(k);
        static Hotkey CS(Keys k) => Hotkey.CtrlAlt(k).WithShift();

        var list = new List<ShortcutAction>
        {
            new("picker", "Choose what to show", ShortcutGroup.Presenting, C(Keys.P), Keywords: "present choose window picker open"),
            new("palette", "Search actions and help", ShortcutGroup.Presenting, C(Keys.Enter), Keywords: "search command palette help how"),
            new("present-active", "Show the window I am in", ShortcutGroup.Presenting, C(Keys.W), Keywords: "active current"),
            new("pick-window", "Show a window by clicking it", ShortcutGroup.Presenting, CS(Keys.W), Keywords: "click point choose pick mouse"),
            new("previous-source", "Show the previous window again", ShortcutGroup.Presenting, C(Keys.Back), Keywords: "last undo back source"),
            new("next-window", "Show the next window", ShortcutGroup.Presenting, C(Keys.PageDown), Keywords: "next cycle"),
            new("previous-window", "Show the window before", ShortcutGroup.Presenting, C(Keys.PageUp), Keywords: "previous cycle"),
            new("stop", "Stop showing", ShortcutGroup.Presenting, C(Keys.S), Keywords: "end close"),
            new("audience-view", "See what the audience sees", ShortcutGroup.Presenting, C(Keys.V), Keywords: "preview monitor audience view confidence what tv see check"),
            new("get-ready", "Get the next thing ready", ShortcutGroup.Presenting, CS(Keys.G), Keywords: "next queue cue prepare preview stage ready"),
            new("go", "Show what's ready", ShortcutGroup.Presenting, C(Keys.G), Keywords: "go switch next cue take show ready"),
            new("second-tv", "Second TV: show something / stop", ShortcutGroup.Presenting, null, Keywords: "second two tv screens dual another display projector both"),
            new("save-room", "Save this setup as a room", ShortcutGroup.Presenting, null, Keywords: "room profile setup preset meeting room classroom lobby save"),

            new("blank", "Blank the TV / show again", ShortcutGroup.OnTheTv, C(Keys.B), Keywords: "black hide"),
            new("blank-message", "Blank with a message", ShortcutGroup.OnTheTv, CS(Keys.B), Keywords: "right back break"),
            new("blank-countdown", "Blank with a countdown", ShortcutGroup.OnTheTv, C(Keys.T), Keywords: "break timer countdown back in minutes coffee"),
            new("freeze", "Freeze the TV / go live", ShortcutGroup.OnTheTv, C(Keys.F), Keywords: "pause still"),
            new("dim", "Dim the TV (0–40%)", ShortcutGroup.OnTheTv, C(Keys.D), Keywords: "night darker brightness"),
            new("move-output", "Show on the next screen", ShortcutGroup.OnTheTv, C(Keys.M), Keywords: "monitor display output move"),
            new("switch-output", "Switch where it shows: TV, Zoom screen, window", ShortcutGroup.OnTheTv, C(Keys.O), Keywords: "zoom floating window virtual share output"),
            new("zoom-in", "Zoom in around the mouse / zoom out", ShortcutGroup.OnTheTv, C(Keys.Z), Keywords: "magnify closer bigger enlarge follow zoomit"),
            new("zoom-in-more", "Zoom in closer", ShortcutGroup.OnTheTv, CS(Keys.Z), Keywords: "magnify closer bigger level"),
            new("highlight-clicks", "Show my clicks on the TV on / off", ShortcutGroup.OnTheTv, C(Keys.H), Keywords: "click highlight ring circle mouse"),
            new("pointer-ring", "Ring around the mouse on the TV on / off", ShortcutGroup.OnTheTv, CS(Keys.H), Keywords: "spotlight find mouse cursor halo circle"),
            new("laser", "Laser pointer on / off", ShortcutGroup.OnTheTv, CS(Keys.L), Keywords: "laser red dot point presenter"),
            new("inset", "Small picture in a corner on / off", ShortcutGroup.OnTheTv, null, Keywords: "picture in picture pip webcam face corner overlay small inset"),
            new("draw", "Draw on the TV / stop drawing", ShortcutGroup.OnTheTv, CS(Keys.D), Keywords: "draw pen ink annotate highlighter marker sketch write arrow box text shapes"),
            new("hide-part", "Hide part of it from the audience", ShortcutGroup.OnTheTv, C(Keys.X), Keywords: "hide redact cover blur private secret password email blank out censor box"),
            new("show-hidden", "Show the hidden parts again", ShortcutGroup.OnTheTv, CS(Keys.X), Keywords: "unhide uncover redact remove reveal hidden"),
            new("spotlight", "Spotlight around the mouse on / off", ShortcutGroup.OnTheTv, CS(Keys.F), Keywords: "spotlight torch focus dim darken highlight area circle attention"),
            new("caption", "Name banner on / off", ShortcutGroup.OnTheTv, CS(Keys.N), Keywords: "name caption lower third title banner who speaker job"),
            new("qr", "QR code on the TV / take it off", ShortcutGroup.OnTheTv, C(Keys.Q), Keywords: "qr code link wifi wi-fi scan phone url website guest network"),
            new("live-captions", "Live captions on / off", ShortcutGroup.OnTheTv, CS(Keys.T), Keywords: "subtitles captions speech transcribe deaf hard of hearing accessibility words spoken"),
        };

        // In "Presenting" rather than "Favourites", so Search and help lists it (it skips the numbered favourites).
        list.Add(new("slideshow", "Show favourites one after another / stop", ShortcutGroup.Presenting, C(Keys.L), Keywords: "slideshow loop rotate lobby waiting room cycle carousel"));
        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
            list.Add(new(ShowFavouriteId(slot), $"Show favourite {slot}", ShortcutGroup.Favourites, C(Keys.D0 + slot)));
        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
            list.Add(new(SaveFavouriteId(slot), $"Save what's showing as favourite {slot}", ShortcutGroup.Favourites, CS(Keys.D0 + slot)));
        // Scenes (FR-86) on Ctrl+Alt+F1–F9: a button deck sends these too.
        for (int slot = 1; slot <= Scene.Slots; slot++)
            list.Add(new(SceneId(slot), $"Show scene {slot}", ShortcutGroup.Favourites, C(Keys.F1 + slot - 1)));
        list.Add(new("save-scene", "Save this as a scene", ShortcutGroup.Presenting, null, Keywords: "scene save preset layout setup obs"));
        list.Add(new("stop-playlist", "Stop the playlist", ShortcutGroup.Presenting, null, Keywords: "playlist stop signage lobby loop"));
        list.Add(new("next-slide", "Next slide", ShortcutGroup.Presenting, null, Keywords: "next slide clicker page down forward advance"));
        list.Add(new("previous-slide", "Previous slide", ShortcutGroup.Presenting, null, Keywords: "previous slide clicker page up back"));
        list.Add(new("phone-remote", "Use my phone as a remote", ShortcutGroup.OnMyScreen, null, Keywords: "phone remote clicker mobile control pair wifi"));
        list.Add(new("auto-hide", "Hide private details automatically on / off", ShortcutGroup.OnTheTv, null, Keywords: "automatic hide redact email phone card iban ocr privacy personal details password key token secret words names"));

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
            new("check-updates", "Check for a newer version", ShortcutGroup.OnMyScreen, null, Keywords: "update upgrade new version release latest"),
            new("restart-timer", "Restart the talk timer", ShortcutGroup.OnMyScreen, null, Keywords: "clock time talk timer reset start"),
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
