using System.Globalization;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Core;

/// <summary>What "--present", "--screen", "--camera", "--favourite" or "--again" asks to show.</summary>
internal enum CliShowKind { Window, Screen, Camera, Favourite, Again }

internal sealed record CliShow(CliShowKind Kind, string Value);

/// <summary>"--output": a screen by Windows' number, the window, or the Zoom screen.</summary>
internal sealed record CliOutput(int? Screen, bool Window, bool Zoom);

/// <summary>
/// One command-line request (FR-43), e.g. <c>ScreenCastPresenter.exe --present "PowerPoint" --output 2</c>.
/// The output is chosen first, then the thing to show, then the actions in the order given.
/// </summary>
internal sealed record CliRequest
{
    public CliOutput? Output { get; init; }
    public CliShow? Show { get; init; }

    /// <summary>
    /// Action ids from the shortcut catalogue, or these exact-state commands: "cli:blank-on",
    /// "cli:blank-off", "cli:freeze-on", "cli:freeze-off", "cli:record-on", "stop-recording",
    /// "cli:on:NAME" and "cli:off:NAME" (NAME one of <see cref="CommandLine.Switches"/>).
    /// </summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    public bool Help { get; init; }

    public bool IsEmpty => Output is null && Show is null && Actions.Count == 0 && !Help;
}

/// <summary>The answer the running copy sends back to a command-line launch.</summary>
internal sealed record CliReply(bool Ok, string Message);

/// <summary>
/// Parses the command line (FR-43) without touching Windows, so every rule is unit-tested. Values
/// may follow the option ("--screen 2") or be joined to it ("--screen=2").
/// </summary>
internal static class CommandLine
{
    /// <summary>The app started by a command-line launch to carry out its request: quiet, like at sign-in.</summary>
    public const string QuietStartSwitch = "--quiet-start";

    /// <summary>Switches the exe uses for itself (helpers, start with Windows, uninstall). Never command-line requests.</summary>
    private static readonly HashSet<string> Internal = new(StringComparer.OrdinalIgnoreCase)
    {
        QuietStartSwitch,
        Capture.CaptureProtocol.HostSwitch, Capture.CaptureProtocol.StillSwitch, Recording.RecordingProtocol.RecordSwitch,
        Installer.UninstallSwitch, StartupRegistration.StartupSwitch, UpdateCheck.Switch, LiveCaptions.Switch, Remote.RemoteHost.Switch,
    };

    /// <summary>What "--on NAME" and "--off NAME" switch: set exactly, so a button deck can't get out of step.</summary>
    public static readonly IReadOnlyList<string> Switches =
        ["spotlight", "caption", "live-captions", "auto-hide", "laser", "highlight-clicks", "pointer-ring", "phone-remote", "audience-view", "outline", "badge", "widget"];

    private static readonly Dictionary<string, string> Simple = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--blank"] = "cli:blank-on",
        ["--unblank"] = "cli:blank-off",
        ["--freeze"] = "cli:freeze-on",
        ["--unfreeze"] = "cli:freeze-off",
        ["--stop"] = "stop",
        ["--start-recording"] = "cli:record-on",
        ["--stop-recording"] = "stop-recording",
        ["--screenshot"] = "screenshot",
        ["--choose"] = "picker",
    };

    public const string Usage =
        """
        ScreenCast Presenter: show one window, screen or camera on a TV or in a meeting.

        Usage: ScreenCastPresenter.exe [what to show] [--output where] [actions]

        What to show (one of these):
          --present "text"     the open window whose app or title contains the text
          --screen N           the whole of Screen N (Windows' own number)
          --camera "text"      the camera whose name contains the text
          --favourite N        favourite 1 to 9
          --again              the last thing shown

        Where to show it:
          --output N           full screen on Screen N
          --output window      in a window you can share in a meeting
          --output zoom        on the Zoom screen

        Actions (run in the order given):
          --blank, --unblank           hide or show the picture
          --freeze, --unfreeze         hold the picture or go live
          --stop                       stop showing
          --start-recording            record what the audience sees
          --stop-recording             stop and save the recording
          --screenshot                 take a screenshot
          --choose                     open "Choose what to show"
          --room "name"                set things up for a saved room
          --qr "link"                  put a QR code for the link on the TV ("--qr off" takes it off)
          --scene N                    show scene 1 to 9
          --playlist "name"            play a playlist on its TV ("--playlist off" stops them)
          --on NAME, --off NAME        switch one of these on or off (nothing happens if it already is):
                                       spotlight, caption, live-captions, auto-hide, laser, highlight-clicks,
                                       pointer-ring, phone-remote, audience-view, outline, badge, widget
          --action NAME                any action, by the name in Settings, e.g. dim, zoom, slideshow
          --help                       this text

        If ScreenCast Presenter isn't running it starts first; either way the command goes to it, and this returns as soon as it's done.
        Exit code 0 when it worked, 1 when it couldn't be done, 2 for a mistake in the command.
        Example: ScreenCastPresenter.exe --present "PowerPoint" --output 2
        """;

    /// <summary>True when the arguments are a command-line request rather than an internal switch or nothing.</summary>
    public static bool IsRequest(IReadOnlyList<string> args) =>
        args.Count > 0 && args.Any(a => !Internal.Contains(a) && (a.StartsWith('-') || a.StartsWith('/')));

    public static (CliRequest? Request, string? Error) Parse(IReadOnlyList<string> args)
    {
        var request = new CliRequest();
        var actions = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (Internal.Contains(arg)) continue;
            string name = arg, value = "";
            bool joined = false;
            int eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                name = arg[..eq];
                value = arg[(eq + 1)..];
                joined = true;
            }

            string? NextValue()
            {
                if (joined) return value.Trim().Length > 0 ? value.Trim() : null;
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return null;
                return args[++i].Trim() is { Length: > 0 } v ? v : null;
            }

            switch (name.ToLowerInvariant())
            {
                case "--help" or "-h" or "-?" or "/?" or "/h" or "/help":
                    request = request with { Help = true };
                    break;
                case "--present" or "--screen" or "--camera" or "--favourite" or "--favorite" or "--again":
                {
                    if (request.Show is not null) return (null, "Choose one thing to show: use only one of --present, --screen, --camera, --favourite or --again.");
                    CliShowKind kind = name.ToLowerInvariant() switch
                    {
                        "--present" => CliShowKind.Window,
                        "--screen" => CliShowKind.Screen,
                        "--camera" => CliShowKind.Camera,
                        "--again" => CliShowKind.Again,
                        _ => CliShowKind.Favourite,
                    };
                    string? v = kind == CliShowKind.Again ? "" : NextValue();
                    if (v is null) return (null, $"{name} needs a value, e.g. {Example(kind)}.");
                    if (kind == CliShowKind.Screen && !IsNumberIn(v, 1, 64)) return (null, $"--screen needs a screen number, like --screen 2 (got “{v}”).");
                    if (kind == CliShowKind.Favourite && !IsNumberIn(v, 1, AppSettings.SlotCount)) return (null, $"--favourite needs a number from 1 to {AppSettings.SlotCount} (got “{v}”).");
                    request = request with { Show = new CliShow(kind, v) };
                    break;
                }
                case "--output":
                {
                    if (request.Output is not null) return (null, "Use --output once.");
                    string? v = NextValue();
                    if (v is null) return (null, "--output needs a value: a screen number, “window” or “zoom”.");
                    CliOutput? output = v.ToLowerInvariant() switch
                    {
                        "window" => new CliOutput(null, true, false),
                        "zoom" => new CliOutput(null, false, true),
                        _ when IsNumberIn(v, 1, 64) => new CliOutput(int.Parse(v, CultureInfo.InvariantCulture), false, false),
                        _ => null,
                    };
                    if (output is null) return (null, $"--output needs a screen number, “window” or “zoom” (got “{v}”).");
                    request = request with { Output = output };
                    break;
                }
                case "--room":
                {
                    string? v = NextValue();
                    if (v is null) return (null, "--room needs a room's name, e.g. --room \"Board room\".");
                    actions.Add("room:" + v);
                    break;
                }
                case "--scene":
                {
                    string? v = NextValue();
                    if (v is null || !IsNumberIn(v, 1, Scene.Slots)) return (null, $"--scene needs a number from 1 to {Scene.Slots}{(v is null ? "" : $" (got “{v}”)")}.");
                    actions.Add("scene-" + v);
                    break;
                }
                case "--playlist":
                {
                    string? v = NextValue();
                    if (v is null) return (null, "--playlist needs a playlist's name, e.g. --playlist \"Lobby\", or “off”.");
                    actions.Add(v.Equals("off", StringComparison.OrdinalIgnoreCase) ? "stop-playlist" : "playlist:" + v);
                    break;
                }
                case "--qr":
                {
                    string? v = NextValue();
                    if (v is null) return (null, "--qr needs a link, e.g. --qr https://example.com, or “off” to take it off.");
                    if (v.Equals("off", StringComparison.OrdinalIgnoreCase)) actions.Add("cli:qr-off");
                    else if (System.Text.Encoding.UTF8.GetByteCount(v) > QrCode.MaxBytes) return (null, $"That's too long for a QR code on the TV (at most {QrCode.MaxBytes} letters).");
                    else actions.Add("cli:qr:" + v);
                    break;
                }
                case "--on" or "--off":
                {
                    string? v = NextValue();
                    string? what = v is null ? null : Switches.FirstOrDefault(s => s.Equals(v, StringComparison.OrdinalIgnoreCase));
                    if (what is null)
                        return (null, $"{name} needs one of: {string.Join(", ", Switches)}{(v is null ? "" : $" (got “{v}”)")}.");
                    actions.Add((name.Equals("--on", StringComparison.OrdinalIgnoreCase) ? "cli:on:" : "cli:off:") + what);
                    break;
                }
                case "--action":
                {
                    string? v = NextValue();
                    if (v is null) return (null, "--action needs the name of an action, e.g. --action dim.");
                    if (ShortcutCatalog.Find(v.ToLowerInvariant()) is not { } action) return (null, $"“{v}” isn't an action ScreenCast Presenter knows. Names are listed in Settings › Keyboard shortcuts.");
                    actions.Add(action.Id);
                    break;
                }
                default:
                    if (Simple.TryGetValue(name, out string? simple))
                    {
                        if (joined) return (null, $"{name} doesn't take a value.");
                        actions.Add(simple);
                        break;
                    }
                    return (null, arg.StartsWith('-') || arg.StartsWith('/')
                        ? $"“{arg}” isn't an option ScreenCast Presenter knows. Use --help to see them."
                        : $"“{arg}” was unexpected here. Put text with spaces in quotes, e.g. --present \"My slides\".");
            }
        }
        request = request with { Actions = actions };
        return request.IsEmpty ? (null, "Nothing to do. Use --help to see the options.") : (request, null);
    }

    private static string Example(CliShowKind kind) => kind switch
    {
        CliShowKind.Window => "--present \"PowerPoint\"",
        CliShowKind.Screen => "--screen 2",
        CliShowKind.Camera => "--camera \"Document camera\"",
        _ => "--favourite 1",
    };

    private static bool IsNumberIn(string text, int min, int max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max;

    /// <summary>
    /// The window "--present" means: an app with exactly that name first (e.g. "POWERPNT",
    /// "notepad"), otherwise the best match by title or app, like typing in the search box.
    /// Most recently used first.
    /// </summary>
    public static SourceWindow? FindWindow(IReadOnlyList<SourceWindow> frontToBack, string query)
    {
        string folded = PickerLayout.Fold(query).Trim();
        if (folded.Length == 0) return null;
        foreach (SourceWindow w in frontToBack)
        {
            if (PickerLayout.Fold(w.AppName) == folded) return w;
        }
        return PickerLayout.Filter(frontToBack, query, w => w.Title, w => w.AppName).FirstOrDefault();
    }

    /// <summary>One line for the pipe: no line breaks or tabs.</summary>
    public static string Flatten(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
