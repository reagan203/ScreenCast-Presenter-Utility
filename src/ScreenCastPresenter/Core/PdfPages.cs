using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>
/// PDFs as sources (FR-115): a handout or an exported deck shown page by page without opening a
/// PDF reader. Pages are made into pictures on this PC by Windows' own PDF renderer (in a helper
/// process, so a damaged file can't take the app down), kept in a folder of their own, and shown
/// as pictures: fades, zoom, marks, automatic hiding, split screen and screenshots all work as for
/// any picture. Next and previous slide turn the page. The pages ahead are made before they're
/// needed, so turning a page is quick.
/// </summary>
internal static class PdfPages
{
    public const string Extension = ".pdf";
    public const string Filter = "PDF documents (*.pdf)|*.pdf";
    public const string App = "PDF";

    /// <summary>Bigger files are refused: a handout or a deck, not an archive.</summary>
    public const long MaxBytes = 300L * 1024 * 1024;

    /// <summary>A page's longer side is made this many pixels: sharp on a 4K TV, small enough to make quickly.</summary>
    public const int RenderSide = 2560;

    /// <summary>Pages made at once: the one wanted and the next few.</summary>
    public const int Ahead = 3;

    /// <summary>Made pages kept for at most this many PDFs; the oldest folder goes first.</summary>
    public const int KeptDocuments = 8;

    public static bool IsPdf(string? path) => path is { Length: > 0 } && string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="path"/> can't be shown, or null when it can (whether it opens is found out when its pages are made).</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Choose a PDF.";
        if (!IsPdf(path)) return "Choose a PDF document.";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return $"“{Path.GetFileName(path)}” can't be found.";
            if (info.Length == 0) return $"“{info.Name}” is empty.";
            if (info.Length > MaxBytes) return $"“{info.Name}” is too big (over {MaxBytes / (1024 * 1024)} MB).";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "That PDF can't be opened.";
        }
    }

    /// <summary>A PDF's name for messages and the tray: its file name without the extension.</summary>
    public static string Name(string path) => Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name ? name : path;

    /// <summary>"Handout · page 3 of 12".</summary>
    public static string Title(string path, int page, int pages) =>
        pages > 0 ? $"{Name(path)} · page {page} of {pages}" : $"{Name(path)} · page {page}";

    /// <summary>
    /// The folder a PDF's pages are kept in, under <paramref name="root"/>: named from the file's
    /// path, size and last change, so a changed file gets new pages and an unchanged one reuses them.
    /// </summary>
    public static string FolderFor(string root, string path, long length, DateTime writtenUtc)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFullPath(path).ToUpperInvariant()}|{length}|{writtenUtc.Ticks}");
        return Path.Combine(root, "pdf-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16]);
    }

    /// <summary>Where page <paramref name="page"/> (from 1) is kept in <paramref name="folder"/>.</summary>
    public static string PagePath(string folder, int page) => Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"page-{page:0000}.png"));

    /// <summary>A page number kept within 1 and <paramref name="pages"/> (when known).</summary>
    public static int Clamp(int page, int pages) => pages > 0 ? Math.Clamp(page, 1, pages) : Math.Max(1, page);

    /// <summary>The page after turning <paramref name="by"/> (+1 next, −1 back): null at either end.</summary>
    public static int? Turn(int page, int pages, int by)
    {
        int next = page + by;
        return next < 1 || pages > 0 && next > pages ? null : next;
    }

    /// <summary>The pixel size to make a page of <paramref name="width"/> × <paramref name="height"/> points: the longer side <see cref="RenderSide"/>, the shape kept.</summary>
    public static (int Width, int Height) RenderSize(double width, double height, int side = RenderSide)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return (side, (int)Math.Round(side * 9 / 16.0));
        double scale = side / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>The pages to make for <paramref name="page"/>: it first, then the ones after it, then the one before.</summary>
    public static List<int> ToMake(int page, int pages)
    {
        var list = new List<int> { Clamp(page, pages) };
        for (int i = 1; i < Ahead; i++)
            if (pages <= 0 || page + i <= pages) list.Add(page + i);
        if (page > 1) list.Add(page - 1);
        return list;
    }

    /// <summary>The helper's line for how many pages there are: "pages 12".</summary>
    public static string PagesLine(int pages) => string.Create(CultureInfo.InvariantCulture, $"pages {pages}");

    public static int? ParsePages(string? line) =>
        line is not null && line.StartsWith("pages ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
}
