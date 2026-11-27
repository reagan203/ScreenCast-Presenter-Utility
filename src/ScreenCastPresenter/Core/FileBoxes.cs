namespace ScreenCastPresenter.Core;

/// <summary>
/// Open and Save boxes (FR-114). A file box shows folder names, file names and recent places from
/// anywhere on the PC, none of which the presenter chose to show. While one is open on a whole
/// screen being shown, the picture cuts to black (like a never-show app) until it closes or moves away.
/// A window shown on its own isn't affected: its file boxes are separate windows the audience never sees.
/// </summary>
internal static class FileBoxes
{
    /// <summary>The class Windows gives dialog boxes, file boxes included.</summary>
    public const string DialogClass = "#32770";

    /// <summary>Inside a dialog box, any of these makes it a file or folder box: the Open/Save box's view, a file list, a folder tree.</summary>
    public static IReadOnlySet<string> FileViewClasses { get; } = new HashSet<string>(StringComparer.Ordinal) { "DUIViewWndClassName", "SHELLDLL_DefView", "SysTreeView32" };

    /// <summary>A top-level window of <paramref name="windowClass"/> whose descendants have <paramref name="childClasses"/> is a file or folder box.</summary>
    public static bool IsFileBox(string windowClass, IEnumerable<string> childClasses) =>
        windowClass == DialogClass && childClasses.Any(FileViewClasses.Contains);

    /// <summary>The first file box (front to back) with any part inside <paramref name="shown"/>.</summary>
    public static VisibleWindow? FindOnScreen(IReadOnlyList<VisibleWindow> fileBoxes, Rectangle shown) =>
        shown.Width <= 0 || shown.Height <= 0 ? null : fileBoxes.FirstOrDefault(w => w.Bounds.Width > 0 && w.Bounds.Height > 0 && w.Bounds.IntersectsWith(shown));
}
