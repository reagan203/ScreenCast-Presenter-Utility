using ScreenCastPresenter.Recording;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter.Core;

internal enum TileTone { Normal, Accent, Recording }

/// <summary>
/// One tile in the quick-actions panel: an icon and a short label, a command (as in the tray
/// menu), whether it is switched on, and whether the panel stays open after it (toggles do, like
/// Windows' Quick Settings; anything that opens something else closes the panel).
/// </summary>
internal sealed record QuickTile(string Label, char Icon, string Command, bool On = false, TileTone Tone = TileTone.Normal,
    bool Enabled = true, bool KeepOpen = false, string? Shortcut = null, string? Description = null);

/// <summary>What the panel shows, captured each time it is drawn.</summary>
internal sealed record QuickPanelState
{
    public SessionState Session { get; init; }
    public bool Blanked { get; init; }
    public bool Frozen { get; init; }
    public bool Cropped { get; init; }
    public RecordingStatus Recording { get; init; } = RecordingStatus.Off;

    /// <summary>Where the audience looks: "the TV", "the Zoom screen", "the window", "Screen 2".</summary>
    public string Where { get; init; } = "the TV";

    /// <summary>When nothing is shown: the last thing shown, to bring back in one click.</summary>
    public string? ShowAgainTitle { get; init; }

    public bool Zoomed { get; init; }
    public bool Drawing { get; init; }
}

/// <summary>
/// The quick-actions panel above the clock (left-click on the tray icon), like Windows 11's Quick
/// Settings: big tiles for what you do most, switched-on tiles in the accent colour, recording in
/// red. Like the tray menu, it only offers what applies right now. Everything else is one click
/// away in the footer (Search and help, Settings) or in the right-click menu.
/// </summary>
internal static class QuickPanelPlan
{
    public const int MaxTiles = 9;

    public static IReadOnlyList<QuickTile> Tiles(QuickPanelState s)
    {
        var tiles = new List<QuickTile>();
        bool presenting = s.Session == SessionState.Presenting;
        bool recording = s.Recording != RecordingStatus.Off;

        if (s.Session == SessionState.Off)
        {
            tiles.Add(new("Show something", Icons.Play, "picker", Tone: TileTone.Accent, Shortcut: "picker", Description: "Choose a window or screen to show"));
            if (s.ShowAgainTitle is { Length: > 0 } title)
                tiles.Add(new("Show it again", Icons.Back, "show-again", Description: $"Show “{TrayMenuPlan.Shorten(title)}” again"));
        }
        else
        {
            tiles.Add(new(presenting ? "Show something else" : "Choose what to show", Icons.Play, "picker", Shortcut: "picker",
                Description: presenting ? "Show something else" : "Choose what to show"));
            if (presenting)
            {
                tiles.Add(new("Blank", Icons.Hide, "blank", On: s.Blanked, KeepOpen: true, Shortcut: "blank",
                    Description: s.Blanked ? $"{Capitalise(s.Where)} is blank. Click to show it again" : $"Blank {s.Where}: the audience sees black"));
                tiles.Add(new("Freeze", Icons.Pause, "freeze", On: s.Frozen, KeepOpen: true, Shortcut: "freeze",
                    Description: s.Frozen ? "Frozen. Click to go live again" : $"Freeze {s.Where} while you work privately"));
                tiles.Add(s.Cropped
                    ? new("Part of it", Icons.Crop, "remove-crop", On: true, KeepOpen: true, Shortcut: "remove-crop", Description: "Showing part of the window. Click to show all of it")
                    : new("Part of it", Icons.Crop, "crop", Shortcut: "crop", Description: "Show only part of the window"));
                tiles.Add(new("Zoom in", Icons.Search, "zoom-in", On: s.Zoomed, KeepOpen: true, Shortcut: "zoom-in",
                    Description: s.Zoomed ? "Zoomed in, following your mouse. Click to zoom out" : $"Zoom in around your mouse on {s.Where}"));
                // While recording, the recording tiles need the room.
                if (!recording) tiles.Add(new("Draw", Icons.Highlight, "draw", On: s.Drawing, Shortcut: "draw",
                    Description: s.Drawing ? "Drawing. Click to stop and clear it" : $"Draw on {s.Where} as you talk"));
            }
        }

        tiles.Add(new("Screenshot", Icons.Camera, "screenshot", Shortcut: "screenshot", Description: "Take a screenshot"));

        if (recording)
        {
            bool paused = s.Recording == RecordingStatus.Paused;
            tiles.Add(new(s.Recording == RecordingStatus.Stopping ? "Saving…" : "Stop recording", Icons.Stop, "stop-recording", On: true, Tone: TileTone.Recording,
                Enabled: s.Recording != RecordingStatus.Stopping, Shortcut: "record", Description: "Stop recording and save it"));
            tiles.Add(new(paused ? "Resume recording" : "Pause recording", paused ? Icons.Record : Icons.Pause, "pause-recording", On: paused, KeepOpen: true,
                Enabled: s.Recording is RecordingStatus.Recording or RecordingStatus.Paused, Shortcut: "pause-recording",
                Description: paused ? "Resume recording" : "Pause recording"));
        }
        else if (s.Session != SessionState.Off)
        {
            tiles.Add(new("Record", Icons.Record, "record-output", Shortcut: "record", Description: $"Record what {s.Where} shows"));
        }
        else
        {
            tiles.Add(new("Record", Icons.Record, "record-picker", Shortcut: "record", Description: "Record a window or screen, even while you work"));
        }

        tiles.Add(new("Share in a meeting", Icons.Share, "zoom", Description: "Share in Zoom or Teams"));
        if (!presenting) tiles.Add(new("My recordings", Icons.Folder, "open-recordings", Description: "Open your recordings folder"));
        return tiles.Take(MaxTiles).ToList();
    }

    /// <summary>The footer: Stop showing (while something is shown), Search and help, Settings.</summary>
    public static IReadOnlyList<QuickTile> Footer(QuickPanelState s)
    {
        var footer = new List<QuickTile>();
        if (s.Session != SessionState.Off) footer.Add(new("Stop showing", Icons.Stop, "stop", Shortcut: "stop", Description: "Stop showing"));
        footer.Add(new("Search and help", Icons.Help, "help", Shortcut: "palette", Description: "Search and help"));
        footer.Add(new("Settings", Icons.Settings, "settings", Description: "Settings"));
        return footer;
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
