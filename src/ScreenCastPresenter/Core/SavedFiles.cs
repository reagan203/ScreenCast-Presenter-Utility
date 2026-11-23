using System.Globalization;

namespace ScreenCastPresenter.Core;

/// <summary>
/// File names and folders for screenshots and recordings, saved automatically like the Snipping
/// Tool (FR-51, FR-52): "Screenshot 2026-11-23 134501.png" in Pictures\Screenshots and
/// "Screen Recording 2026-11-23 134501.mp4" in Videos\Screen Recordings.
/// </summary>
internal static class SavedFiles
{
    public static string DefaultScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public static string DefaultRecordingFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");

    /// <summary>A file name that doesn't exist yet: "Screenshot 2026-11-23 134501.png", then " (2)", " (3)"…</summary>
    public static string NextPath(string folder, string prefix, DateTime now, string extension, Func<string, bool> exists)
    {
        string stem = $"{prefix} {now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture)}";
        string path = Path.Combine(folder, stem + extension);
        for (int n = 2; exists(path) && n < 10_000; n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }

    /// <summary>Bytes free on the drive holding <paramref name="folder"/>, or null if unknown.</summary>
    public static long? FreeSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Plain-language reason a save failed.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "SCP isn't allowed to save in that folder.",
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => "The disk is full.",
        DirectoryNotFoundException => "The folder doesn't exist any more.",
        _ => "The file couldn't be saved (" + ex.Message + ").",
    };
}
