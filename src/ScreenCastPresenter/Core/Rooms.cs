namespace ScreenCastPresenter.Core;

/// <summary>The name banner along the bottom of the TV (FR-79): a name and a second line, e.g. a job title.</summary>
internal static class NameCaption
{
    public const int MaxLength = 60;

    /// <summary>One line, no control characters, at most <see cref="MaxLength"/>; never null.</summary>
    public static string Tidy(string? text) => text is null ? "" : InkStroke.TidyText(text, MaxLength);

    /// <summary>"Jane Doe · Head of Sales", or just the name.</summary>
    public static string Words(string name, string second) => second.Length > 0 ? $"{name} · {second}" : name;
}

/// <summary>
/// A room profile (FR-82): what one click sets up for a room. The TV it uses, how dim the picture
/// is, the extras on the TV, the black screen's message and logo, the name banner, and (if saved
/// with them) the favourites. Made from the current settings, so setting up a room is "arrange it
/// as you like it, then save it as a room".
/// </summary>
internal sealed class RoomProfile
{
    public const int Max = 12;
    public const int MaxNameLength = 40;

    public string Name { get; set; } = "";
    public string? OutputDevice { get; set; }
    public string OutputMode { get; set; } = "Screen";
    public string? SecondOutputDevice { get; set; }
    public double Dim { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool HighlightClicks { get; set; }
    public bool PointerRing { get; set; }
    public string BlankMessage { get; set; } = "We'll be right back";
    public string? BlankLogo { get; set; }
    public string CaptionName { get; set; } = "";
    public string CaptionSecond { get; set; } = "";

    /// <summary>The room's favourites; null leaves the favourites as they are when the room is chosen.</summary>
    public Favourite?[]? Favourites { get; set; }

    /// <summary>A room made from how things are set up now.</summary>
    public static RoomProfile From(string name, AppSettings s, bool withFavourites) => new()
    {
        Name = TidyName(name),
        OutputDevice = s.OutputDevice,
        OutputMode = s.OutputInWindow ? "Window" : "Screen",
        SecondOutputDevice = s.SecondOutputDevice,
        Dim = s.Dim,
        ShowCursor = s.ShowCursor,
        HighlightClicks = s.HighlightClicks,
        PointerRing = s.PointerRing,
        BlankMessage = s.BlankMessage,
        BlankLogo = s.BlankLogo,
        CaptionName = s.CaptionName,
        CaptionSecond = s.CaptionSecond,
        Favourites = withFavourites ? [.. s.Favourites.Select(Copy)] : null,
    };

    /// <summary>Sets <paramref name="s"/> up for this room. Things the room didn't save stay as they are.</summary>
    public void ApplyTo(AppSettings s)
    {
        s.OutputDevice = OutputDevice;
        s.OutputMode = OutputMode;
        s.SecondOutputDevice = SecondOutputDevice == OutputDevice ? null : SecondOutputDevice;
        s.Dim = Dim;
        s.ShowCursor = ShowCursor;
        s.HighlightClicks = HighlightClicks;
        s.PointerRing = PointerRing;
        s.BlankMessage = BlankMessage;
        s.BlankLogo = BlankLogo;
        s.CaptionName = CaptionName;
        s.CaptionSecond = CaptionSecond;
        if (Favourites is not null) s.Favourites = [.. Favourites.Select(Copy)];
        s.Normalize();
    }

    public static string TidyName(string? name) => name is null ? "" : InkStroke.TidyText(name, MaxNameLength);

    /// <summary>Rooms from a settings file made sensible: named, no two with one name, at most <see cref="Max"/>.</summary>
    public static List<RoomProfile> NormalizeAll(List<RoomProfile>? rooms)
    {
        var result = new List<RoomProfile>();
        foreach (RoomProfile? room in rooms ?? [])
        {
            if (room is null) continue; // a hand-edited file can hold nulls
            room.Name = TidyName(room.Name);
            if (room.Name.Length == 0 || result.Any(r => string.Equals(r.Name, room.Name, StringComparison.OrdinalIgnoreCase))) continue;
            room.Dim = double.IsFinite(room.Dim) ? Math.Clamp(room.Dim, 0, AppSettings.MaxDim) : 0;
            room.OutputMode = string.Equals(room.OutputMode, "Window", StringComparison.OrdinalIgnoreCase) ? "Window" : "Screen";
            room.BlankMessage = string.IsNullOrWhiteSpace(room.BlankMessage) ? "We'll be right back" : InkStroke.TidyText(room.BlankMessage, AppSettings.MaxMessageLength);
            room.CaptionName = NameCaption.Tidy(room.CaptionName);
            room.CaptionSecond = NameCaption.Tidy(room.CaptionSecond);
            if (string.IsNullOrWhiteSpace(room.BlankLogo)) room.BlankLogo = null;
            if (string.IsNullOrWhiteSpace(room.OutputDevice)) room.OutputDevice = null;
            if (string.IsNullOrWhiteSpace(room.SecondOutputDevice)) room.SecondOutputDevice = null;
            if (room.Favourites is { } f && f.Length != AppSettings.SlotCount)
            {
                var slots = new Favourite?[AppSettings.SlotCount];
                Array.Copy(f, slots, Math.Min(f.Length, AppSettings.SlotCount));
                room.Favourites = slots;
            }
            if (room.Favourites is { } favs)
            {
                for (int i = 0; i < favs.Length; i++)
                    if (favs[i] is { } fav && (string.IsNullOrWhiteSpace(fav.AppName) || fav.Title is null)) favs[i] = null;
            }
            result.Add(room);
            if (result.Count == Max) break;
        }
        return result;
    }

    private static Favourite? Copy(Favourite? f) => f is null ? null : new Favourite { AppName = f.AppName, Title = f.Title, ScreenDevice = f.ScreenDevice, CameraId = f.CameraId };

    /// <summary>Finds a room by name, ignoring case.</summary>
    public static RoomProfile? Find(IEnumerable<RoomProfile> rooms, string? name) =>
        name is null ? null : rooms.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
