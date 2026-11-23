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

    /// <summary>"window:1234" or "screen:\\.\DISPLAY2".</summary>
    public static string SourceSpec(SourceWindow source) =>
        source.Screen is { } screen ? "screen:" + screen.DeviceName : "window:" + source.Handle.ToString(Inv);

    public static SourceWindow? ParseSource(string spec, IReadOnlyList<DisplayMonitor>? monitors = null)
    {
        if (spec.StartsWith("window:", StringComparison.Ordinal) && long.TryParse(spec.AsSpan(7), NumberStyles.Integer, Inv, out long hwnd) && hwnd != 0)
            return new SourceWindow((nint)hwnd, "", "", false);
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

    public static string Start(nint parent, SourceWindow source, bool cursor, bool frozen, Size output) =>
        string.Create(Inv, $"start {parent} {SourceSpec(source)} {(cursor ? 1 : 0)} {(frozen ? 1 : 0)} {Crop(source.Crop)} {output.Width} {output.Height}");

    public static string Opacity(double value) => string.Create(Inv, $"opacity {value:R}");
    public static string OutputSize(Size size) => string.Create(Inv, $"size {size.Width} {size.Height}");
    public static string Freeze(bool frozen) => frozen ? "freeze 1" : "freeze 0";

    /// <summary>The message text goes last so it may contain spaces; line breaks are flattened.</summary>
    public static string Message(string? text, double brightness) =>
        string.Create(Inv, $"message {brightness:R} {(text ?? "").Replace('\r', ' ').Replace('\n', ' ')}");

    public static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : 0;

    public static int ParseInt(string text) => int.TryParse(text, NumberStyles.Integer, Inv, out int v) ? v : 0;
}
