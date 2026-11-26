namespace ScreenCastPresenter.UI;

/// <summary>
/// Icons from the icon font that ships with Windows: Segoe Fluent Icons on Windows 11, Segoe MDL2
/// Assets on Windows 10 (the same code points for these). No image files, nothing to load, sharp
/// at every size and DPI, and in the same style as Windows itself. If neither font exists, icons
/// are simply left out (<see cref="Available"/>), never drawn as boxes.
/// </summary>
internal static class Icons
{
    public const char Play = '\uE768';
    public const char Stop = '\uE71A';
    public const char Pause = '\uE769';
    public const char Hide = '\uED1A';
    public const char View = '\uE890';
    public const char Crop = '\uE7A8';
    public const char Camera = '\uE722';
    public const char Record = '\uE7C8';
    public const char Video = '\uE714';
    public const char Folder = '\uE8B7';
    public const char Settings = '\uE713';
    public const char Help = '\uE897';
    public const char Search = '\uE721';
    public const char Power = '\uE7E8';
    public const char Tv = '\uE7F4';
    public const char Window = '\uE737';
    public const char Share = '\uE72D';
    public const char Star = '\uE734';
    public const char Keyboard = '\uE765';
    public const char Colour = '\uE790';
    public const char Font = '\uE8D2';
    public const char Microphone = '\uE720';
    public const char Volume = '\uE767';
    public const char Brightness = '\uE706';
    public const char Mouse = '\uE962';
    public const char Pin = '\uE718';
    public const char Download = '\uE896';
    public const char Delete = '\uE74D';
    public const char Lock = '\uE72E';
    public const char Info = '\uE946';
    public const char Back = '\uE72B';
    public const char ChevronRight = '\uE76C';
    public const char OpenWindow = '\uE8A7';
    public const char Message = '\uE8BD';
    public const char Repair = '\uE90F';
    public const char Home = '\uE80F';
    public const char Highlight = '\uE7E6';
    public const char Clock = '\uE823';
    public const char Check = '\uE73E';
    public const char Next = '\uE72A';
    public const char QrCode = '\uED14';
    public const char Captions = '\uE7F0';
    public const char Spotlight = '\uEA80';
    public const char Person = '\uE77B';
    public const char People = '\uE716';
    public const char Calendar = '\uE787';
    public const char Picture = '\uE8B9';
    public const char List = '\uE8FD';
    public const char Phone = '\uE8EA';

    public static IReadOnlyList<char> All { get; } =
    [
        Play, Stop, Pause, Hide, View, Crop, Camera, Record, Video, Folder, Settings, Help, Search, Power, Tv, Window, Share, Star,
        Keyboard, Colour, Font, Microphone, Volume, Brightness, Mouse, Pin, Download, Delete, Lock, Info, Back, ChevronRight,
        OpenWindow, Message, Repair, Home, Highlight, Clock, Check, Next, QrCode, Captions, Spotlight, Person, People, Calendar, Picture, List, Phone,
    ];

    /// <summary>The icon for a command or action id (tray menu, search results); null for none.</summary>
    public static char? ForCommand(string? command) => command switch
    {
        null => null,
        "picker" or "present-active" => Play,
        "pick-window" or "highlight-clicks" or "pointer-ring" => Mouse,
        "zoom-in" or "zoom-in-more" => Search,
        "laser" or "draw" => Highlight,
        "inset" => OpenWindow,
        "hide-part" or "show-hidden" => Hide,
        "audience-view" => View,
        "get-ready" => List,
        "go" => Play,
        "spotlight" => Spotlight,
        "caption" => Person,
        "qr" => QrCode,
        "live-captions" => Captions,
        "second-tv" or "stop-second-tv" => Tv,
        "save-room" => People,
        _ when command.StartsWith("room:", StringComparison.Ordinal) => People,
        "save-scene" => Star,
        "next-slide" => Next,
        "previous-slide" => Back,
        "stop-playlist" => Stop,
        "phone-remote" => Phone,
        "auto-hide" => Hide,
        _ when command.StartsWith("scene-", StringComparison.Ordinal) => Star,
        _ when command.StartsWith("playlist:", StringComparison.Ordinal) => List,
        "check-updates" => Download,
        "show-again" or "previous-source" or "previous-window" => Back,
        "next-window" or "slideshow" => Next,
        "blank" => Hide,
        "blank-message" => Message,
        "blank-countdown" or "restart-timer" => Clock,
        "freeze" => Pause,
        "crop" or "remove-crop" => Crop,
        "stop" or "stop-recording" => Stop,
        "screenshot" => Camera,
        "record" or "record-output" or "record-picker" => Record,
        "pause-recording" => Pause,
        "open-recordings" => Folder,
        "help" or "palette" => Help,
        "quit" => Power,
        "zoom" => Share,
        "output-window" => Window,
        "dim" => Brightness,
        "move-output" or "switch-output" => Tv,
        "toggle-outline" => Highlight,
        "toggle-badge" => Message,
        "toggle-widget" => View,
        "cheat-sheet" or "pause-shortcuts" => Keyboard,
        "toggle-theme" => Colour,
        _ when command.StartsWith("settings", StringComparison.Ordinal) => Settings,
        _ when command.StartsWith("output:", StringComparison.Ordinal) => Tv,
        _ when command.StartsWith("stop-after:", StringComparison.Ordinal) || command.StartsWith("countdown:", StringComparison.Ordinal) => Clock,
        _ when command.StartsWith("favourite-", StringComparison.Ordinal) || command.StartsWith("save-favourite-", StringComparison.Ordinal) => Star,
        _ => null,
    };

    private static readonly string? FamilyName = FindFamily();
    private static readonly Dictionary<(float, int), Font> Fonts = [];

    /// <summary>The icon font's name, or null when Windows has neither.</summary>
    public static string? Family => FamilyName;

    public static bool Available => FamilyName is not null;

    private static string? FindFamily()
    {
        foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try
            {
                using var family = new FontFamily(name);
                return family.Name == name ? name : null;
            }
            catch (ArgumentException)
            {
                // Not installed; try the next one.
            }
        }
        return null;
    }

    /// <summary>The icon font at <paramref name="pixels"/> device pixels, cached (a handful of sizes are used).</summary>
    public static Font? FontAt(float pixels)
    {
        if (FamilyName is null || pixels <= 0) return null;
        var key = (MathF.Round(pixels, 1), 0);
        if (!Fonts.TryGetValue(key, out Font? font))
        {
            font = new Font(FamilyName, key.Item1, FontStyle.Regular, GraphicsUnit.Pixel);
            Fonts[key] = font;
        }
        return font;
    }

    /// <summary>Draws an icon centred in <paramref name="box"/> (GDI+, so it also works on a backdrop).</summary>
    public static void Draw(Graphics g, char icon, RectangleF box, Color colour, float? pixels = null)
    {
        if (FontAt(pixels ?? Math.Min(box.Width, box.Height)) is not { } font) return;
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        System.Drawing.Text.TextRenderingHint hint = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(icon.ToString(), font, brush, box, format);
        g.TextRenderingHint = hint;
    }

    /// <summary>A small icon bitmap for menus (the caller owns it); null without the font.</summary>
    public static Bitmap? Bitmap(char icon, int size, Color colour)
    {
        if (!Available) return null;
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        Draw(g, icon, new RectangleF(0, 0, size, size), colour, size * 0.8f);
        return bitmap;
    }
}
