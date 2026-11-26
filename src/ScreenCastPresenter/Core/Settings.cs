using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenCastPresenter.Core;

internal enum ThemeMode { System, Dark, Light }

/// <summary>A saved source for one-key recall (FR-23).</summary>
internal sealed class Favourite
{
    public string AppName { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Set when the favourite is a whole screen (FR-02): its device name, e.g. "\\.\DISPLAY1".</summary>
    public string? ScreenDevice { get; set; }

    /// <summary>Set when the favourite is a camera (FR-68): Windows' id for it; <see cref="Title"/> is its name.</summary>
    public string? CameraId { get; set; }

    /// <summary>The same source: the same screen, the same camera, or the same app with the same or a similar title.</summary>
    public bool Matches(Favourite other) =>
        ScreenDevice is not null || other.ScreenDevice is not null ? ScreenDevice == other.ScreenDevice
        : CameraId is not null || other.CameraId is not null ? CameraId == other.CameraId
        : string.Equals(AppName, other.AppName, StringComparison.OrdinalIgnoreCase) && (Title == other.Title || FavouriteMatcher.IsSimilar(Title, other.Title));
}

/// <summary>Everything SCP remembers (FR-40/41). Unknown JSON fields are ignored, missing ones get defaults.</summary>
internal sealed class AppSettings
{
    public const int SlotCount = 9;
    public const double MaxDim = 0.4;
    public const int MaxMessageLength = 120;

    /// <summary>2: light became the default theme (files from version 1 still on the old default move to it once).</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string? OutputDevice { get; set; }

    /// <summary>Stored as text so a value written by a newer version can't make the whole file unreadable.</summary>
    public string Theme { get; set; } = nameof(ThemeMode.Light);

    public double Dim { get; set; }
    public bool ShowOutline { get; set; } = true;
    public bool ShowBadge { get; set; } = true;
    public bool ShortcutsEnabled { get; set; } = true;

    /// <summary>Only the user's changes: action id → "Ctrl+Alt+X", or "" for "switched off".</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    public Favourite?[] Favourites { get; set; } = new Favourite?[SlotCount];
    public bool FirstRunDone { get; set; }
    public string BlankMessage { get; set; } = "We'll be right back";

    /// <summary>"Automatic" (DWM for windows, WGC for screens and freeze) or "WGC" (always). FR-32.</summary>
    public string CaptureEngine { get; set; } = "Automatic";

    /// <summary>Show the mouse pointer on the output (FR-24; only the WGC engine can).</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>Last crop per app (FR-04), applied automatically next time that app is shown.</summary>
    public Dictionary<string, CropRegion> SavedCrops { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where screenshots go; null = Pictures\Screenshots, like the Snipping Tool (FR-51).</summary>
    public string? ScreenshotFolder { get; set; }

    /// <summary>Where recordings go; null = Videos\Screen Recordings, like the Snipping Tool (FR-52).</summary>
    public string? RecordingFolder { get; set; }

    /// <summary>The small "now showing" bar (like Teams' sharing bar) while presenting or recording.</summary>
    public bool ShowWidget { get; set; } = true;

    /// <summary>Where the bar was dragged to (physical pixels); null = top centre of the presenter's screen.</summary>
    public WindowBounds? WidgetPosition { get; set; }

    /// <summary>The last thing shown (FR-41), so "previous source" works after a restart.</summary>
    public Favourite? LastSource { get; set; }

    /// <summary>"Screen" (fullscreen on OutputDevice) or "Window" (a floating window, FR-11).</summary>
    public string OutputMode { get; set; } = "Screen";

    /// <summary>The floating output window stays above every other window while presenting.</summary>
    public bool FloatingOnTop { get; set; } = true;

    /// <summary>Where the floating output window was last (physical pixels); null = a default corner.</summary>
    public WindowBounds? FloatingWindow { get; set; }

    /// <summary>Text size of SCP's own windows (1, 1.15 or 1.3); the TV is unaffected.</summary>
    public double TextSize { get; set; } = Core.TextSize.Normal;

    /// <summary>
    /// Browsers: keep to the page that was chosen. Switching tabs or going to another page in the
    /// shown browser window cuts the picture to black until you go back to it.
    /// </summary>
    public bool KeepBrowserPage { get; set; } = true;

    /// <summary>
    /// The screens connected when "show in a window" was chosen. A screen plugged in later (an HDMI
    /// TV) is then offered as the output. Null in older settings: only the main screen counts.
    /// </summary>
    public string[]? WindowModeScreens { get; set; }

    /// <summary>Buttons and highlights in the Windows accent colour instead of SCP's blue.</summary>
    public bool UseWindowsAccent { get; set; }

    [JsonIgnore]
    public bool OutputInWindow => string.Equals(OutputMode, "Window", StringComparison.OrdinalIgnoreCase);

    /// <summary>Computer sound in recordings (FR-53, FR-58): "App" (only the recorded app), "All" or "None".</summary>
    public string RecordSound { get; set; } = "App";

    /// <summary>Record the microphone too (people in the room, FR-53).</summary>
    public bool RecordMicrophone { get; set; }

    /// <summary>
    /// Apps never shown (by program name): their windows can't be chosen, and a whole screen goes
    /// black while one is open on it. Starts with common password managers; a file from before
    /// this setting existed gets them too.
    /// </summary>
    public List<string> NeverShowApps { get; set; } = [.. NeverShow.Defaults];

    /// <summary>Remind to switch on Do not disturb when the whole main screen is shown or recorded.</summary>
    public bool RemindDoNotDisturb { get; set; } = true;

    /// <summary>How much closer zooming in on the pointer brings the TV (FR-64): 1.5, 2, 3 or 4.</summary>
    public double ZoomLevel { get; set; } = Core.ZoomView.DefaultLevel;

    /// <summary>A ring on the TV where you click (FR-65).</summary>
    public bool HighlightClicks { get; set; }

    /// <summary>A soft ring around the pointer on the TV, so a room can follow it (FR-65).</summary>
    public bool PointerRing { get; set; }

    /// <summary>"Blank with a countdown" counts down from this many minutes (FR-66).</summary>
    public int CountdownMinutes { get; set; } = BlankCountdown.DefaultMinutes;

    /// <summary>How long you've been presenting, in the now-showing bar (FR-67).</summary>
    public bool ShowTalkTimer { get; set; } = true;

    /// <summary>The talk's length in minutes, for warnings two minutes before and at the end; 0 = none.</summary>
    public int TalkMinutes { get; set; }

    /// <summary>How long each favourite shows in a slideshow (FR-69).</summary>
    public int SlideshowSeconds { get; set; } = Slideshow.DefaultSeconds;

    /// <summary>Offer to show something when a TV or projector is plugged in (FR-70).</summary>
    public bool OfferNewScreen { get; set; } = true;

    /// <summary>The small picture's corner (FR-72): BottomRight, BottomLeft, TopRight or TopLeft.</summary>
    public string InsetCorner { get; set; } = nameof(Core.InsetCorner.BottomRight);

    /// <summary>The small picture's width, in percent of the TV's width: 20, 28 or 36.</summary>
    public int InsetSize { get; set; } = InsetLayout.DefaultSize;

    /// <summary>Once a day, ask GitHub whether a newer version is out (FR-73). Off unless switched on.</summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>When it last asked (UTC); null = never.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>A version the user chose to skip ("v1.2.0"): not announced again, though a newer one is.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Parts of windows and screens hidden from the audience (FR-74): app name (or "screen:" and a device) → boxes.</summary>
    public Dictionary<string, List<CropRegion>> HiddenParts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How hidden parts look on the TV: "Solid" (the default, nothing shows through) or "Blurred".</summary>
    public string HiddenPartLook { get; set; } = nameof(RedactStyle.Solid);

    /// <summary>The name banner's two lines (FR-79), e.g. "Jane Doe" and "Head of Sales".</summary>
    public string CaptionName { get; set; } = "";
    public string CaptionSecond { get; set; } = "";

    /// <summary>Your logo above the blank screen's message (FR-80): a copy kept next to these settings; null for none.</summary>
    public string? BlankLogo { get; set; }

    /// <summary>The last link put on the TV as a QR code (FR-81), offered again next time.</summary>
    public string? QrLink { get; set; }

    /// <summary>The last Wi-Fi network put on the TV as a QR code; its password is never kept.</summary>
    public string? QrWifiName { get; set; }

    /// <summary>The QR code's corner: TopRight (the default), TopLeft, BottomRight or BottomLeft.</summary>
    public string QrCorner { get; set; } = nameof(Core.InsetCorner.TopRight);

    /// <summary>Room profiles (FR-82): one click sets the screen, dimming, extras and favourites for a room.</summary>
    public List<RoomProfile> Rooms { get; set; } = [];

    /// <summary>Things done at set times (FR-83), e.g. the lobby slideshow from 8:00 to 18:00 on weekdays.</summary>
    public List<ScheduleEntry> Schedule { get; set; } = [];

    /// <summary>The second TV (FR-85): its screen's device name; null for none.</summary>
    public string? SecondOutputDevice { get; set; }

    /// <summary>Where the "what the audience sees" window was (physical pixels); null = beside the bar.</summary>
    public WindowBounds? StudioWindow { get; set; }

    /// <summary>Scenes 1–9 (FR-86): what's shown and everything around it, one key each.</summary>
    public Scene?[] Scenes { get; set; } = new Scene?[Scene.Slots];

    /// <summary>Playlists (FR-87): favourites and scenes taking turns on a TV.</summary>
    public List<Playlist> Playlists { get; set; } = [];

    /// <summary>A playlist to start when ScreenCast Presenter starts (a lobby screen that runs itself); null for none.</summary>
    public string? PlaylistAtStart { get; set; }

    /// <summary>What automatic hiding covers (FR-88), e.g. "Emails, Phones"; "None" (the default) is off.</summary>
    public string AutoHide { get; set; } = nameof(SensitiveKinds.None);

    /// <summary>Live captions on the TV (FR-84); off still allows a transcript (FR-89).</summary>
    public bool CaptionsOnTv { get; set; } = true;

    /// <summary>Save what live captions hear as a transcript, with subtitles (FR-89).</summary>
    public bool SaveTranscript { get; set; }

    /// <summary>Where transcripts go; null = Documents\ScreenCast Presenter\Transcripts.</summary>
    public string? TranscriptFolder { get; set; }

    /// <summary>Live captions' size and place on the TV (FR-89).</summary>
    public string CaptionSize { get; set; } = nameof(Core.CaptionSize.Normal);
    public string CaptionPlace { get; set; } = nameof(Core.CaptionPlace.Bottom);

    /// <summary>The phone remote (FR-90): off unless switched on.</summary>
    public bool PhoneRemote { get; set; }

    /// <summary>The port the phone remote listens on (it falls back to any free port when this one is taken).</summary>
    public int RemotePort { get; set; } = Remote.RemoteServer.DefaultPort;

    /// <summary>The phone remote's pairing key, encrypted for this Windows user (DPAPI); null = not paired.</summary>
    public string? RemoteKey { get; set; }

    [JsonIgnore]
    public SensitiveKinds AutoHideKinds
    {
        get => Enum.TryParse(AutoHide, ignoreCase: true, out SensitiveKinds k) ? k & SensitiveKinds.All : SensitiveKinds.None;
        set => AutoHide = (value & SensitiveKinds.All).ToString();
    }

    [JsonIgnore]
    public CaptionLook CaptionLook
    {
        get => new(Core.CaptionLook.ParseSize(CaptionSize), Core.CaptionLook.ParsePlace(CaptionPlace));
        set
        {
            CaptionSize = value.Size.ToString();
            CaptionPlace = value.Place.ToString();
        }
    }

    [JsonIgnore]
    public RedactStyle HiddenPartStyle
    {
        get => Enum.TryParse(HiddenPartLook, ignoreCase: true, out RedactStyle style) && Enum.IsDefined(style) ? style : RedactStyle.Solid;
        set => HiddenPartLook = value.ToString();
    }

    [JsonIgnore]
    public InsetCorner QrPlace
    {
        get => InsetLayout.ParseCorner(QrCorner, Core.InsetCorner.TopRight);
        set => QrCorner = value.ToString();
    }

    [JsonIgnore]
    public Recording.MeetingSound MeetingSound
    {
        get => Enum.TryParse(RecordSound, ignoreCase: true, out Recording.MeetingSound sound) && Enum.IsDefined(sound) ? sound : Recording.MeetingSound.App;
        set => RecordSound = value.ToString();
    }

    [JsonIgnore]
    public bool PreferWgc => string.Equals(CaptureEngine, "WGC", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public ThemeMode ThemeMode
    {
        get => Enum.TryParse(Theme, ignoreCase: true, out ThemeMode mode) && Enum.IsDefined(mode) ? mode : ThemeMode.Light;
        set => Theme = value.ToString();
    }

    /// <summary>Repairs anything out of range, e.g. from a hand-edited or older file.</summary>
    public AppSettings Normalize()
    {
        Dim = double.IsFinite(Dim) ? Math.Clamp(Dim, 0, MaxDim) : 0;
        TextSize = Math.Round(Core.TextSize.Clamp(TextSize), 2); // 1.15, not 1.149999976 in the file
        // Version 1 defaulted to "Match Windows"; light is the app's theme now. A file from a
        // newer version is left as it is.
        if (Version < 2 && string.Equals(Theme, nameof(ThemeMode.System), StringComparison.OrdinalIgnoreCase)) Theme = nameof(ThemeMode.Light);
        Version = Math.Max(Version, CurrentVersion);
        Theme = ThemeMode.ToString();
        Shortcuts ??= [];
        if (Favourites is null || Favourites.Length != SlotCount)
        {
            var fixedSlots = new Favourite?[SlotCount];
            if (Favourites is not null) Array.Copy(Favourites, fixedSlots, Math.Min(Favourites.Length, SlotCount));
            Favourites = fixedSlots;
        }
        for (int i = 0; i < Favourites.Length; i++)
        {
            if (Favourites[i] is { } f && (string.IsNullOrWhiteSpace(f.AppName) || f.Title is null)) Favourites[i] = null;
        }
        CaptureEngine = PreferWgc ? "WGC" : "Automatic";
        var crops = new Dictionary<string, CropRegion>(StringComparer.OrdinalIgnoreCase);
        foreach ((string app, CropRegion? crop) in SavedCrops ?? [])
        {
            // Re-validate: a hand-edited file could hold anything.
            if (!string.IsNullOrWhiteSpace(app) && crop is not null && CropRegion.Create(crop.X, crop.Y, crop.Width, crop.Height) is { IsFull: false } valid)
                crops[app] = valid;
        }
        SavedCrops = crops;
        if (string.IsNullOrWhiteSpace(ScreenshotFolder)) ScreenshotFolder = null;
        if (string.IsNullOrWhiteSpace(RecordingFolder)) RecordingFolder = null;
        RecordSound = MeetingSound.ToString();
        OutputMode = OutputInWindow ? "Window" : "Screen";
        if (FloatingWindow is { Width: <= 0 } or { Height: <= 0 } or { Width: > 20_000 } or { Height: > 20_000 }) FloatingWindow = null;
        NeverShowApps = NeverShow.Normalize(NeverShowApps);
        ZoomLevel = Core.ZoomView.Normalize(ZoomLevel);
        CountdownMinutes = BlankCountdown.Normalize(CountdownMinutes);
        TalkMinutes = TalkTimer.Normalize(TalkMinutes);
        SlideshowSeconds = Slideshow.Normalize(SlideshowSeconds);
        InsetCorner = InsetLayout.ParseCorner(InsetCorner).ToString();
        InsetSize = InsetLayout.Normalize(InsetSize);
        HiddenParts = Redactions.NormalizeAll(HiddenParts);
        HiddenPartLook = HiddenPartStyle.ToString();
        CaptionName = NameCaption.Tidy(CaptionName);
        CaptionSecond = NameCaption.Tidy(CaptionSecond);
        if (string.IsNullOrWhiteSpace(BlankLogo)) BlankLogo = null;
        QrLink = string.IsNullOrWhiteSpace(QrLink) ? null : QrLink.Trim();
        QrWifiName = string.IsNullOrWhiteSpace(QrWifiName) ? null : QrWifiName.Trim();
        QrCorner = QrPlace.ToString();
        Rooms = RoomProfile.NormalizeAll(Rooms);
        Schedule = ScheduleEntry.NormalizeAll(Schedule);
        if (string.IsNullOrWhiteSpace(SecondOutputDevice)) SecondOutputDevice = null;
        if (StudioWindow is { Width: <= 0 } or { Height: <= 0 } or { Width: > 20_000 } or { Height: > 20_000 }) StudioWindow = null;
        Scenes = Scene.NormalizeAll(Scenes);
        Playlists = Playlist.NormalizeAll(Playlists);
        if (Playlist.Find(Playlists, PlaylistAtStart) is not { } atStart) PlaylistAtStart = null;
        else PlaylistAtStart = atStart.Name;
        AutoHide = AutoHideKinds.ToString();
        if (string.IsNullOrWhiteSpace(TranscriptFolder)) TranscriptFolder = null;
        CaptionLook = CaptionLook;
        if (RemotePort is < 1024 or > 65535) RemotePort = Remote.RemoteServer.DefaultPort;
        if (string.IsNullOrWhiteSpace(RemoteKey)) RemoteKey = null;
        BlankMessage = string.IsNullOrWhiteSpace(BlankMessage) ? "We'll be right back" : BlankMessage.Trim();
        if (BlankMessage.Length > MaxMessageLength) BlankMessage = BlankMessage[..MaxMessageLength];
        return this;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves settings.json (FR-40, FR-45): atomic writes, corrupt files backed up and
/// replaced by defaults, and no exception ever escapes to the caller.
/// </summary>
internal sealed class SettingsStore(string path)
{
    public const long MaxFileBytes = 1024 * 1024;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenCastPresenter", "settings.json");

    public string FilePath { get; } = path;
    public AppSettings Current { get; private set; } = new();

    /// <summary>Plain-language description of the last load problem, if any.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Plain-language description of the last save problem, if any.</summary>
    public string? SaveProblem { get; private set; }

    public AppSettings Load()
    {
        LoadProblem = null;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) return Current = new AppSettings();
            if (info.Length > MaxFileBytes) throw new InvalidDataException("The settings file is too large.");

            byte[] bytes = File.ReadAllBytes(FilePath);
            AppSettings? loaded = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.AppSettings);
            return Current = (loaded ?? throw new InvalidDataException("The settings file is empty.")).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            string? backup = BackUpCorruptFile();
            LoadProblem = backup is null
                ? "Your settings file was damaged, so ScreenCast Presenter started with default settings."
                : $"Your settings file was damaged, so ScreenCast Presenter started with default settings. The old file was kept as {Path.GetFileName(backup)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadProblem = "ScreenCast Presenter couldn't read its settings file, so it started with default settings.";
        }
        return Current = new AppSettings();
    }

    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then renames it over the real file, so a
    /// crash or power cut mid-save leaves either the old or the new settings, never half a file.
    /// </summary>
    public bool Save()
    {
        SaveProblem = null;
        string temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Current.Normalize(), SettingsJsonContext.Default.AppSettings);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveProblem = ex is UnauthorizedAccessException
                ? "ScreenCast Presenter couldn't save your settings: the settings file or folder is read-only."
                : "ScreenCast Presenter couldn't save your settings. The disk may be full.";
            TryDelete(temp);
            return false;
        }
    }

    private string? BackUpCorruptFile()
    {
        try
        {
            string backup = Path.Combine(Path.GetDirectoryName(FilePath)!,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(FilePath, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>A window position and size, as saved in settings.</summary>
internal sealed record WindowBounds(int X, int Y, int Width, int Height)
{
    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static WindowBounds From(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
