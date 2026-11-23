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

    /// <summary>The same source: the same screen, or the same app with the same or a similar title.</summary>
    public bool Matches(Favourite other) =>
        ScreenDevice is not null || other.ScreenDevice is not null
            ? ScreenDevice == other.ScreenDevice
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

    [JsonIgnore]
    public bool OutputInWindow => string.Equals(OutputMode, "Window", StringComparison.OrdinalIgnoreCase);

    /// <summary>Computer sound in recordings (FR-53, FR-58): "App" (only the recorded app), "All" or "None".</summary>
    public string RecordSound { get; set; } = "App";

    /// <summary>Record the microphone too (people in the room, FR-53).</summary>
    public bool RecordMicrophone { get; set; }

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
                ? "Your settings file was damaged, so SCP started with default settings."
                : $"Your settings file was damaged, so SCP started with default settings. The old file was kept as {Path.GetFileName(backup)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadProblem = "SCP couldn't read its settings file, so it started with default settings.";
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
                ? "SCP couldn't save your settings: the settings file or folder is read-only."
                : "SCP couldn't save your settings. The disk may be full.";
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
