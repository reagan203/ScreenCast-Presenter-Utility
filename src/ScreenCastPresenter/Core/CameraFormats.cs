namespace ScreenCastPresenter.Core;

/// <summary>One way a camera can send its picture: size and frames per second.</summary>
internal readonly record struct CameraFormat(int Width, int Height, double Fps);

/// <summary>
/// Picks how a camera sends its picture (FR-68). The picture travels through ordinary memory in
/// the helper, so it is held to Full HD: sharp enough to read a page under a document camera,
/// and a few percent of one core. Smooth enough matters too: at least 15 frames a second, and
/// 30 preferred over 60, which only costs more for slides and paper.
/// </summary>
internal static class CameraFormats
{
    public const int MaxWidth = 1920, MaxHeight = 1080;
    public const double MinFps = 15, IdealFps = 30;

    /// <summary>The best format, or null to leave the camera as it is (it offers nothing usable).</summary>
    public static CameraFormat? Choose(IEnumerable<CameraFormat> offered)
    {
        List<CameraFormat> usable = [.. offered.Where(f => f.Width > 0 && f.Height > 0 && double.IsFinite(f.Fps))];
        IEnumerable<CameraFormat> fitting = usable.Where(f => FitsFullHd(f) && f.Fps >= MinFps - 0.01);
        if (!fitting.Any()) fitting = usable.Where(FitsFullHd);   // a slow camera: still the best it can do
        if (!fitting.Any()) fitting = usable;                        // only bigger: take the smallest of those
        return fitting
            .OrderByDescending(f => FitsFullHd(f) ? (long)f.Width * f.Height : -(long)f.Width * f.Height)
            .ThenBy(f => f.Fps >= IdealFps - 0.01 ? f.Fps - IdealFps : 1000 + IdealFps - f.Fps)
            .Cast<CameraFormat?>()
            .FirstOrDefault();
    }

    /// <summary>Full HD either way round (a document camera may stand portrait).</summary>
    private static bool FitsFullHd(CameraFormat f) =>
        f.Width <= MaxWidth && f.Height <= MaxHeight || f.Width <= MaxHeight && f.Height <= MaxWidth;
}
