using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Parts of a window or screen hidden from the audience (FR-74): boxes as fractions of the
/// window's client area (or the screen), so a box stays on the same spot of the window when it is
/// moved or resized, and the content scrolls underneath it. Remembered per app (and per screen),
/// like crops.
/// </summary>
internal static class Redactions
{
    public const int MaxPerSource = 12;

    /// <summary>Most boxes automatic hiding (FR-88) puts on one picture: a full page of addresses stays bounded.</summary>
    public const int MaxAutomatic = 64;

    /// <summary>Smallest automatic box, as a share of the picture: a short word in small print on a 4K screen.</summary>
    public const double MinAutomaticSize = 0.002;

    /// <summary>Where a source's boxes are kept: the app's name, or "screen:" and the screen's device; null for a camera.</summary>
    public static string? Key(SourceWindow source) =>
        source.Screen is { } screen ? "screen:" + screen.DeviceName
        : source.IsCamera ? null
        : source.AppName.Length > 0 ? source.AppName : null;

    /// <summary>Tidies a source's boxes from a settings file: valid, not the whole thing, at most <see cref="MaxPerSource"/>.</summary>
    public static List<CropRegion> Normalize(IEnumerable<CropRegion?>? regions) =>
        [.. (regions ?? []).OfType<CropRegion>()
            .Select(r => CropRegion.Create(r.X, r.Y, r.Width, r.Height))
            .OfType<CropRegion>()
            .Take(MaxPerSource)];

    public static Dictionary<string, List<CropRegion>> NormalizeAll(Dictionary<string, List<CropRegion>>? all)
    {
        var result = new Dictionary<string, List<CropRegion>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, List<CropRegion> regions) in all ?? [])
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            List<CropRegion> tidy = Normalize(regions);
            if (tidy.Count > 0) result[key] = tidy;
        }
        return result;
    }

    /// <summary>The boxes kept for <paramref name="source"/> (none for a camera or an app with none).</summary>
    public static IReadOnlyList<CropRegion> For(AppSettings settings, SourceWindow source) =>
        Key(source) is { } key && settings.HiddenParts.TryGetValue(key, out List<CropRegion>? boxes) ? boxes : [];

    /// <summary>What the session covers for <paramref name="source"/>; null when nothing is hidden.</summary>
    public static HiddenParts? PartsFor(AppSettings settings, SourceWindow source) =>
        For(settings, source) is { Count: > 0 } boxes ? new HiddenParts(boxes, settings.HiddenPartStyle) : null;

    /// <summary>Adds a box to a source's list; false (nothing changed) when it already has <see cref="MaxPerSource"/>.</summary>
    public static bool Add(AppSettings settings, SourceWindow source, CropRegion box)
    {
        if (Key(source) is not { } key) return false;
        if (!settings.HiddenParts.TryGetValue(key, out List<CropRegion>? boxes)) settings.HiddenParts[key] = boxes = [];
        if (boxes.Count >= MaxPerSource) return false;
        boxes.Add(box);
        return true;
    }

    /// <summary>Forgets a source's boxes; true if it had any.</summary>
    public static bool Clear(AppSettings settings, SourceWindow source) => Key(source) is { } key && settings.HiddenParts.Remove(key);

    /// <summary>
    /// True when <paramref name="box"/> covers all of <paramref name="part"/> (the crop or zoomed
    /// view): then nothing of the source would be seen, which is allowed but worth saying.
    /// </summary>
    public static bool CoversAll(CropRegion box, CropRegion? part)
    {
        CropRegion p = part ?? CropRegion.Full;
        return box.X <= p.X + 1e-6 && box.Y <= p.Y + 1e-6 && box.X + box.Width >= p.X + p.Width - 1e-6 && box.Y + box.Height >= p.Y + p.Height - 1e-6;
    }
}

/// <summary>Parts of a source covered for the audience (FR-74), and how.</summary>
internal sealed record HiddenParts(IReadOnlyList<CropRegion> Regions, RedactStyle Style);

/// <summary>Something with hidden parts couldn't have them covered, so it isn't shown at all (privacy beats showing it).</summary>
internal sealed class HiddenPartsException(string message) : InvalidOperationException(message);
