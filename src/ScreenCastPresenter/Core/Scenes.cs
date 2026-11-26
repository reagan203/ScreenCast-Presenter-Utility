namespace ScreenCastPresenter.Core;

/// <summary>
/// A scene (FR-86): what's shown and everything around it, recalled with one key or a button deck.
/// The source (with its crop), the small picture and its place, the name banner, a QR code for a
/// link, the spotlight and dimming. Hidden parts aren't stored: they belong to the app and always
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
    public string InsetCorner { get; set; } = nameof(Core.InsetCorner.BottomRight);
    public int InsetSize { get; set; } = InsetLayout.DefaultSize;
    public bool Caption { get; set; }
    public string? QrLink { get; set; }
    public bool Spotlight { get; set; }
    public double Dim { get; set; }

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
            scene.InsetCorner = InsetLayout.ParseCorner(scene.InsetCorner).ToString();
            scene.InsetSize = InsetLayout.Normalize(scene.InsetSize);
            scene.QrLink = KeepableQr(scene.QrLink?.Trim());
            if (scene.QrLink is not null && QrCode.Encode(scene.QrLink) is null) scene.QrLink = null;
            scene.Dim = double.IsFinite(scene.Dim) ? Math.Clamp(scene.Dim, 0, AppSettings.MaxDim) : 0;
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
        if (Caption) parts.Add("name");
        if (QrLink is not null) parts.Add("QR code");
        if (Spotlight) parts.Add("spotlight");
        if (Dim > 0) parts.Add($"dimmed {Dim:P0}");
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }
}

/// <summary>Which screen a playlist plays on (FR-87).</summary>
internal enum PlaylistScreen { Main, Second }

/// <summary>One step of a playlist: a favourite or a scene, for so many seconds.</summary>
internal sealed class PlaylistItem
{
    public string Kind { get; set; } = "Favourite";
    public int Slot { get; set; } = 1;
    public int Seconds { get; set; } = Slideshow.DefaultSeconds;

    public bool IsScene => string.Equals(Kind, "Scene", StringComparison.OrdinalIgnoreCase);

    public string Words(Func<int, string>? favouriteName = null, Func<int, string>? sceneName = null) =>
        (IsScene ? sceneName?.Invoke(Slot) ?? $"scene {Slot}" : favouriteName?.Invoke(Slot) ?? $"favourite {Slot}") + $" for {Slideshow.Words(Seconds)}";
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
            p.Items = [.. (p.Items ?? []).OfType<PlaylistItem>()
                .Where(i => i.Slot is >= 1 and <= AppSettings.SlotCount)
                .Select(i => new PlaylistItem { Kind = i.IsScene ? "Scene" : "Favourite", Slot = i.Slot, Seconds = Slideshow.Normalize(i.Seconds) })
                .Take(MaxItems)];
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

    /// <summary>A playlist of every saved favourite and scene, <paramref name="seconds"/> each (what the "make one" form starts from).</summary>
    public static List<PlaylistItem> Everything(IReadOnlyList<Favourite?> favourites, IReadOnlyList<Scene?> scenes, int seconds) =>
        [
            .. Enumerable.Range(1, favourites.Count).Where(s => favourites[s - 1] is not null).Select(s => new PlaylistItem { Kind = "Favourite", Slot = s, Seconds = seconds }),
            .. Enumerable.Range(1, scenes.Count).Where(s => scenes[s - 1] is not null).Select(s => new PlaylistItem { Kind = "Scene", Slot = s, Seconds = seconds }),
        ];
}
