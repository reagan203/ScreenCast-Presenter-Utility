using System.Globalization;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// The line protocol between SCP and its capture helper process (same exe, started with
/// --capture-host). SCP → helper: start / opacity / size / crop / freeze / message / changed / stop.
/// Helper → SCP: ready / failed. Text, one command per line, invariant culture.
/// </summary>
internal static class CaptureProtocol
{
    public const string HostSwitch = "--capture-host";
    public const string StillSwitch = "--still";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"window:1234", "screen:\\.\DISPLAY2", or "camera:" and the camera's id escaped so it has no spaces.</summary>
    public static string SourceSpec(SourceWindow source) =>
        source.Screen is { } screen ? "screen:" + screen.DeviceName
        : source.Camera is { } camera ? "camera:" + Uri.EscapeDataString(camera.Id)
        : "window:" + source.Handle.ToString(Inv);

    public static SourceWindow? ParseSource(string spec, IReadOnlyList<DisplayMonitor>? monitors = null)
    {
        if (spec.StartsWith("window:", StringComparison.Ordinal) && long.TryParse(spec.AsSpan(7), NumberStyles.Integer, Inv, out long hwnd) && hwnd != 0)
            return new SourceWindow((nint)hwnd, "", "", false);
        if (spec.StartsWith("camera:", StringComparison.Ordinal) && spec.Length > 7)
        {
            try
            {
                return SourceWindow.ForCamera(new CameraDevice(Uri.UnescapeDataString(spec[7..]), ""));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
        if (spec.StartsWith("screen:", StringComparison.Ordinal))
        {
            string device = spec[7..];
            DisplayMonitor? screen = (monitors ?? DisplayHelpers.GetMonitors()).FirstOrDefault(m => m.DeviceName == device);
            return screen is null ? null : SourceWindow.ForScreen(screen);
        }
        return null;
    }

    public static string Crop(CropRegion? crop) =>
        crop is null ? "none" : string.Create(Inv, $"{crop.X:R},{crop.Y:R},{crop.Width:R},{crop.Height:R}");

    public static CropRegion? ParseCrop(string text)
    {
        if (text == "none") return null;
        string[] p = text.Split(',');
        if (p.Length != 4) return null;
        double[] v = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(p[i], NumberStyles.Float, Inv, out v[i])) return null;
        return CropRegion.Create(v[0], v[1], v[2], v[3]);
    }

    /// <param name="autoHide">Automatic hiding from the first frame (FR-88): the picture is held until it's been scanned once.</param>
    public static string Start(nint parent, SourceWindow source, bool cursor, bool frozen, Size output, SensitiveKinds autoHide = SensitiveKinds.None, RedactStyle style = RedactStyle.Solid,
        IReadOnlyList<string>? words = null) =>
        string.Create(Inv, $"start {parent} {SourceSpec(source)} {(cursor ? 1 : 0)} {(frozen ? 1 : 0)} {Crop(source.Crop)} {output.Width} {output.Height} {(int)autoHide} {(int)style} {SensitiveText.WordsToken(words)}");

    public static string Opacity(double value) => string.Create(Inv, $"opacity {value:R}");
    public static string OutputSize(Size size) => string.Create(Inv, $"size {size.Width} {size.Height}");
    public static string Freeze(bool frozen) => frozen ? "freeze 1" : "freeze 0";

    /// <summary>
    /// The message text goes last so it may contain spaces. A line break travels as U+2028 (the
    /// Unicode line separator), which DirectWrite breaks lines at, so the protocol stays one line per command.
    /// </summary>
    public static string Message(string? text, double brightness) =>
        string.Create(Inv, $"message {brightness:R} {(text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', LineSeparator)}");

    /// <summary>U+2028: a line break inside a message.</summary>
    public const char LineSeparator = '\u2028';

    // ───────────── marks drawn on the TV (FR-65, FR-71): points are fractions of the content's whole area ─────────────

    /// <summary>"0.25,0.5". Four decimals: a fifth of a pixel on a 2,000-pixel-wide window.</summary>
    public static string Point(PointF p) => string.Create(Inv, $"{p.X:0.####},{p.Y:0.####}");

    /// <summary>A point, or null if it isn't one. Slightly outside 0–1 is allowed (the pointer at the very edge).</summary>
    public static PointF? ParsePoint(string text)
    {
        int comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0
            || !float.TryParse(text.AsSpan(0, comma), NumberStyles.Float, Inv, out float x)
            || !float.TryParse(text.AsSpan(comma + 1), NumberStyles.Float, Inv, out float y)
            || !float.IsFinite(x) || !float.IsFinite(y) || x is < -1 or > 2 || y is < -1 or > 2) return null;
        return new PointF(x, y);
    }

    public static string Click(PointF at, bool secondary) => $"click {Point(at)} {(secondary ? 1 : 0)}";

    /// <summary>The ring around the pointer (FR-65); null hides it.</summary>
    public static string Pointer(PointF? at) => at is { } p ? "pointer " + Point(p) : "pointer none";

    /// <summary>The laser dot (FR-71); null hides it (its trail fades on its own).</summary>
    public static string Laser(PointF? at) => at is { } p ? "laser " + Point(p) : "laser none";

    /// <summary>Whether the real pointer is drawn (off while the laser stands in for it).</summary>
    public static string Cursor(bool on) => on ? "cursor 1" : "cursor 0";

    /// <summary>A new mark; a text mark's words go last (they may contain spaces).</summary>
    public static string InkStart(InkStroke stroke) =>
        string.Create(Inv, $"ink-start {stroke.Id} {(int)stroke.Tool} {stroke.Argb} {stroke.Width:0.#####}") + (stroke.Text is { Length: > 0 } text ? " " + OneLine(text) : "");

    /// <summary>A mark's points replaced (an arrow or box being dragged).</summary>
    public static string InkSet(int id, IEnumerable<PointF> points) => string.Create(Inv, $"ink-set {id} {string.Join(' ', points.Select(Point))}");

    /// <summary>Parts of the window hidden from the audience (FR-74), as fractions of its client area; "redact none" shows everything.</summary>
    public static string Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) =>
        regions.Count == 0 ? "redact none" : string.Create(Inv, $"redact {(int)style} {string.Join(' ', regions.Select(Crop))}");

    /// <summary>The rest of a "redact" line: the style and the parts; null for "none" or nonsense.</summary>
    public static (RedactStyle Style, List<CropRegion> Regions)? ParseRedact(string text)
    {
        string[] p = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !int.TryParse(p[0], NumberStyles.Integer, Inv, out int s) || !Enum.IsDefined((RedactStyle)s)) return null;
        var regions = p.Skip(1).Select(ParseCrop).OfType<CropRegion>().Take(Redactions.MaxPerSource).ToList();
        return regions.Count == 0 ? null : ((RedactStyle)s, regions);
    }

    /// <summary>The spotlight's centre (FR-77); null turns it off.</summary>
    public static string Spotlight(PointF? at) => at is { } p ? "spotlight " + Point(p) : "spotlight none";

    /// <summary>The name banner (FR-79): the name and a second line; null takes it off.</summary>
    public static string Caption(string? name, string? second) =>
        name is null ? "caption none" : $"caption {OneLine(name)}{LineSeparator}{OneLine(second ?? "")}";

    /// <summary>The picture on the blank screen (FR-80): a file path, which may contain spaces, last.</summary>
    public static string Logo(string? path) => path is null ? "logo none" : "logo " + OneLine(path);

    /// <summary>A QR code in a corner (FR-81): the corner, then the text it holds.</summary>
    public static string Qr(string? text, InsetCorner corner) => text is null ? "qr none" : string.Create(Inv, $"qr {(int)corner} {OneLine(text)}");

    /// <summary>Live captions (FR-84): the words heard most recently; "live" alone takes them off.</summary>
    public static string Live(string? text) => text is null ? "live" : "live " + OneLine(text);

    /// <summary>Live captions' size and place (FR-89).</summary>
    public static string CaptionStyle(CaptionLook look) => string.Create(Inv, $"live-style {(int)look.Size} {(int)look.Place}");

    /// <summary>Automatic hiding (FR-88): what to look for and how covered parts look.</summary>
    public static string AutoHide(SensitiveKinds kinds, RedactStyle style) => string.Create(Inv, $"autohide {(int)kinds} {(int)style}");

    /// <summary>The presenter's own words for automatic hiding, as one escaped word.</summary>
    public static string AutoHideWords(IReadOnlyList<string>? words) => "autohide-words " + SensitiveText.WordsToken(words);

    /// <summary>"autohide kinds style" back to its parts; nonsense is off.</summary>
    public static (SensitiveKinds Kinds, RedactStyle Style) ParseAutoHide(string kinds, string style)
    {
        SensitiveKinds k = int.TryParse(kinds, NumberStyles.Integer, Inv, out int n) ? (SensitiveKinds)n & SensitiveKinds.All : SensitiveKinds.None;
        RedactStyle s = int.TryParse(style, NumberStyles.Integer, Inv, out int m) && Enum.IsDefined((RedactStyle)m) ? (RedactStyle)m : RedactStyle.Solid;
        return (k, s);
    }

    /// <summary>User text on one protocol line: line breaks, the line separator and other control characters become spaces.</summary>
    public static string OneLine(string text) =>
        new([.. text.Select(c => char.IsControl(c) || c == LineSeparator || c == (char)0x2029 ? ' ' : c)]);

    /// <summary>Points added to a line, space-separated; a long line is sent in several of these.</summary>
    public static string InkAdd(int id, IEnumerable<PointF> points) => string.Create(Inv, $"ink-add {id} {string.Join(' ', points.Select(Point))}");

    public static string InkRemove(int id) => string.Create(Inv, $"ink-remove {id}");

    public const string InkClear = "ink-clear";

    /// <summary>"inset none", or "inset" with what to show, the corner and the width in percent (FR-72).</summary>
    public static string Inset(SourceWindow? source, InsetCorner corner, int percent) =>
        source is null ? "inset none" : string.Create(Inv, $"inset {SourceSpec(source)} {(int)corner} {InsetLayout.Normalize(percent)}");

    /// <summary>The rest of an "inset" line after what to show: its corner and size; defaults for anything odd.</summary>
    public static (InsetCorner Corner, int Percent) ParseInsetPlace(string text)
    {
        string[] p = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var corner = p.Length > 0 && int.TryParse(p[0], NumberStyles.Integer, Inv, out int c) && Enum.IsDefined((InsetCorner)c) ? (InsetCorner)c : InsetCorner.BottomRight;
        int percent = p.Length > 1 && int.TryParse(p[1], NumberStyles.Integer, Inv, out int size) ? InsetLayout.Normalize(size) : InsetLayout.DefaultSize;
        return (corner, percent);
    }

    public static List<PointF> ParsePoints(string text)
    {
        var points = new List<PointF>();
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParsePoint(part) is { } p) points.Add(p);
        }
        return points;
    }

    public static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : 0;

    public static int ParseInt(string text) => int.TryParse(text, NumberStyles.Integer, Inv, out int v) ? v : 0;
}
