namespace ScreenCastPresenter.Core;

/// <summary>
/// A scene (FR-86): what's shown and everything around it, recalled with one key or a button deck.
/// The source (with its crop), the small picture and its place, the name banner, a QR code for a
/// link, the spotlight, dimming and live captions. Hidden parts aren't stored: they belong to the app and always
/// apply (FR-74). A Wi-Fi QR code is never stored (it holds the password).
/// </summary>
internal sealed class Scene
{
    /// <summary>As many scenes as favourites: keys 1–9.</summary>
    public const int Slots = AppSettings.SlotCount;

    public const int MaxNameLength = 40;

    public string Name { get; set; } = "";
    public Favourite? Source { get; set; }
    public CropRegion? Crop { get; set; }
    public Favourite? Inset { get; set; }

    /// <summary>Split screen (FR-104): what's shown beside the main picture, and how.</summary>
    public List<Favourite> SplitWith { get; set; } = [];
    public string SplitLayout { get; set; } = nameof(Core.SplitLayout.SideBySide);
    public string InsetCorner { get; set; } = nameof(Core.InsetCorner.BottomRight);
    public int InsetSize { get; set; } = InsetLayout.DefaultSize;
    public bool Caption { get; set; }
    public string? QrLink { get; set; }
    public bool Spotlight { get; set; }
    public double Dim { get; set; }

    /// <summary>Speaker notes (FR-111): for the presenter's eyes only.</summary>
    public string Notes { get; set; } = "";

    /// <summary>Live captions on or off with this scene; null (a scene saved before scenes kept them) leaves them as they are.</summary>
    public bool? LiveCaptions { get; set; }

    /// <summary>"Demo" or "Scene 3" when it has no name.</summary>
    public string Title(int slot) => Name.Length > 0 ? Name : $"Scene {slot}";

    /// <summary>A QR code that may be kept in a scene: a link, never Wi-Fi details.</summary>
    public static string? KeepableQr(string? qr) => qr is { Length: > 0 } && !qr.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase) ? qr : null;

    /// <summary>Scenes from a settings file made sensible: exactly <see cref="Slots"/> slots, each with a source.</summary>
    public static Scene?[] NormalizeAll(Scene?[]? scenes)
    {
        var slots = new Scene?[Slots];
        if (scenes is null) return slots;
        for (int i = 0; i < Math.Min(Slots, scenes.Length); i++)
        {
            if (scenes[i] is not { Source: { } source } scene || string.IsNullOrWhiteSpace(source.AppName) || source.Title is null) continue;
            scene.Name = InkStroke.TidyText(scene.Name ?? "", MaxNameLength);
            scene.Crop = scene.Crop is { } c ? CropRegion.Create(c.X, c.Y, c.Width, c.Height) is { IsFull: false } valid ? valid : null : null;
            if (scene.Inset is { } inset && (string.IsNullOrWhiteSpace(inset.AppName) || inset.Title is null)) scene.Inset = null;
            scene.SplitWith = [.. (scene.SplitWith ?? []).OfType<Favourite>().Where(f => !string.IsNullOrWhiteSpace(f.AppName) && f.Title is not null).Take(SplitScreen.MaxOthers)];
            scene.SplitLayout = SplitScreen.Parse(scene.SplitLayout).ToString();
            if (scene.SplitWith.Count > 0) scene.Inset = null; // one or the other
            scene.InsetCorner = InsetLayout.ParseCorner(scene.InsetCorner).ToString();
            scene.InsetSize = InsetLayout.Normalize(scene.InsetSize);
            scene.QrLink = KeepableQr(scene.QrLink?.Trim());
            if (scene.QrLink is not null && QrCode.Encode(scene.QrLink) is null) scene.QrLink = null;
            scene.Dim = double.IsFinite(scene.Dim) ? Math.Clamp(scene.Dim, 0, AppSettings.MaxDim) : 0;
            scene.Notes = NotesCipher.Tidy(scene.Notes);
            slots[i] = scene;
        }
        return slots;
    }

    /// <summary>"Slides · small picture · name · QR · spotlight · dimmed 20%".</summary>
    public string Words()
    {
        var parts = new List<string> { Source?.Title is { Length: > 0 } t ? TrayMenuPlan.Shorten(t, 30) : Source?.AppName ?? "" };
        if (Crop is not null) parts.Add("part of it");
        if (Inset is not null) parts.Add("small picture");
        if (SplitWith.Count > 0) parts.Add(SplitWith.Count == 1 ? "split screen" : $"split screen of {SplitWith.Count + 1}");
        if (Caption) parts.Add("name");
        if (QrLink is not null) parts.Add("QR code");
        if (Spotlight) parts.Add("spotlight");
        if (LiveCaptions == true) parts.Add("live captions");
        if (Notes.Length > 0) parts.Add("notes");
        if (Dim > 0) parts.Add($"dimmed {Dim:P0}");
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }
}

/// <summary>Which screen a playlist plays on (FR-87).</summary>
internal enum PlaylistScreen { Main, Second }

/// <summary>
/// One step of a playlist, for so many seconds: a favourite or a scene (by number), a picture file
/// or a folder of pictures (each in turn), or a text card (FR-103).
/// </summary>
internal sealed class PlaylistItem
{
    public const string FavouriteKind = "Favourite", SceneKind = "Scene", PictureKind = "Picture", CardKind = "Card";

    public string Kind { get; set; } = FavouriteKind;
    public int Slot { get; set; } = 1;
    public int Seconds { get; set; } = Slideshow.DefaultSeconds;

    /// <summary>A picture's file, or a folder of pictures.</summary>
    public string? Path { get; set; }

    /// <summary>A card's words (the first line is its title).</summary>
    public string? Text { get; set; }

    public bool IsScene => string.Equals(Kind, SceneKind, StringComparison.OrdinalIgnoreCase);
    public bool IsPicture => string.Equals(Kind, PictureKind, StringComparison.OrdinalIgnoreCase);
    public bool IsCard => string.Equals(Kind, CardKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>A favourite or a scene: shown by its number.</summary>
    public bool IsSlot => !IsPicture && !IsCard;

    /// <summary>Made sensible from a settings file; null when it can't be a step (no number, no picture, no words).</summary>
    public PlaylistItem? Normalized()
    {
        int seconds = Slideshow.Normalize(Seconds);
        if (IsPicture)
            return Path?.Trim() is { Length: > 0 } p && System.IO.Path.IsPathFullyQualified(p) ? new PlaylistItem { Kind = PictureKind, Path = p, Seconds = seconds } : null;
        if (IsCard) return TextCard.Tidy(Text) is { Length: > 0 } t ? new PlaylistItem { Kind = CardKind, Text = t, Seconds = seconds } : null;
        return Slot is >= 1 and <= AppSettings.SlotCount ? new PlaylistItem { Kind = IsScene ? SceneKind : FavouriteKind, Slot = Slot, Seconds = seconds } : null;
    }

    public string Words(Func<int, string>? favouriteName = null, Func<int, string>? sceneName = null) =>
        (IsPicture ? Path is { } p && !PictureSource.IsPicture(p) ? $"the pictures in “{System.IO.Path.GetFileName(p.TrimEnd('\\', '/'))}”" : $"“{PictureSource.Name(Path ?? "")}”"
            : IsCard ? $"the card “{TrayMenuPlan.Shorten(TextCard.Title(Text ?? ""), 30)}”"
            : IsScene ? sceneName?.Invoke(Slot) ?? $"scene {Slot}" : favouriteName?.Invoke(Slot) ?? $"favourite {Slot}")
        + $" for {Slideshow.Words(Seconds)}{(IsPicture && Path is { } folder && !PictureSource.IsPicture(folder) ? " each" : "")}";
}

/// <summary>
/// A playlist (FR-87): favourites and scenes taking turns on the main TV or the second TV, each
/// for its own time, round and round. Lobby screens and waiting rooms, without a cloud service.
/// </summary>
internal sealed class Playlist
{
    public const int Max = 12;
    public const int MaxItems = 30;
    public const int MaxNameLength = 40;

    public string Name { get; set; } = "";
    public string Screen { get; set; } = nameof(PlaylistScreen.Main);
    public List<PlaylistItem> Items { get; set; } = [];

    /// <summary>Items in a random order each time round, never the same one twice running.</summary>
    public bool Shuffle { get; set; }

    public PlaylistScreen Where => Enum.TryParse(Screen, ignoreCase: true, out PlaylistScreen s) && Enum.IsDefined(s) ? s : PlaylistScreen.Main;

    public static List<Playlist> NormalizeAll(List<Playlist>? playlists)
    {
        var result = new List<Playlist>();
        foreach (Playlist? p in playlists ?? [])
        {
            if (p is null) continue; // a hand-edited file can hold nulls
            p.Name = RoomProfile.TidyName(p.Name);
            if (p.Name.Length == 0 || result.Any(r => string.Equals(r.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            p.Screen = p.Where.ToString();
            p.Items = [.. (p.Items ?? []).OfType<PlaylistItem>().Select(i => i.Normalized()).OfType<PlaylistItem>().Take(MaxItems)];
            if (p.Items.Count == 0) continue;
            result.Add(p);
            if (result.Count == Max) break;
        }
        return result;
    }

    public static Playlist? Find(IEnumerable<Playlist> playlists, string? name) =>
        name is null ? null : playlists.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a playlist shows next (FR-87): pure, so every rule is tested.</summary>
internal static class PlaylistPlayer
{
    /// <summary>
    /// Shuffled: a random item that can be shown, never <paramref name="current"/> again when
    /// another can be (from <paramref name="random"/>, so tests can fix it). In order when not.
    /// </summary>
    public static int? Next(IReadOnlyList<PlaylistItem> items, int? current, Func<PlaylistItem, bool> available, bool shuffle, Random random)
    {
        if (!shuffle) return Next(items, current, available);
        var can = Enumerable.Range(0, items.Count).Where(i => available(items[i])).ToList();
        if (can.Count == 0) return null;
        if (can.Count > 1 && current is { } c) can.Remove(c);
        return can[random.Next(can.Count)];
    }

    /// <summary>
    /// The item after <paramref name="current"/> (from the start when null), going round and
    /// skipping items <paramref name="available"/> says can't be shown now; null when none can.
    /// </summary>
    public static int? Next(IReadOnlyList<PlaylistItem> items, int? current, Func<PlaylistItem, bool> available)
    {
        int count = items.Count;
        if (count == 0) return null;
        int start = current is { } c && c >= 0 && c < count ? c + 1 : 0;
        for (int step = 0; step < count; step++)
        {
            int index = (start + step) % count;
            if (available(items[index])) return index;
        }
        return null;
    }

    /// <summary>
    /// The steps a playlist plays now: a folder becomes each of its pictures in turn (as they are
    /// now; read again each time round), everything else stays one step.
    /// </summary>
    public static List<PlaylistItem> Expand(IReadOnlyList<PlaylistItem> items)
    {
        var steps = new List<PlaylistItem>(items.Count);
        foreach (PlaylistItem item in items)
        {
            if (item.IsPicture && item.Path is { } folder && !PictureSource.IsPicture(folder))
            {
                foreach (string file in PictureSource.InFolder(folder)) steps.Add(new PlaylistItem { Kind = PlaylistItem.PictureKind, Path = file, Seconds = item.Seconds });
            }
            else steps.Add(item);
        }
        return steps;
    }

    /// <summary>A playlist of every saved favourite and scene, <paramref name="seconds"/> each (what the "make one" form starts from).</summary>
    public static List<PlaylistItem> Everything(IReadOnlyList<Favourite?> favourites, IReadOnlyList<Scene?> scenes, int seconds) =>
        [
            .. Enumerable.Range(1, favourites.Count).Where(s => favourites[s - 1] is not null).Select(s => new PlaylistItem { Kind = "Favourite", Slot = s, Seconds = seconds }),
            .. Enumerable.Range(1, scenes.Count).Where(s => scenes[s - 1] is not null).Select(s => new PlaylistItem { Kind = "Scene", Slot = s, Seconds = seconds }),
        ];
}
