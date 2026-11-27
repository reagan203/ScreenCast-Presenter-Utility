namespace ScreenCastPresenter.Core;

/// <summary>
/// Copy the text from what's shown (FR-130): the words in it, read on this PC, put on the
/// presenter's clipboard ready to paste into notes or a chat. Words under hidden parts, and
/// anything automatic hiding covers, are covered before reading, so they're never copied.
/// </summary>
internal static class CopiedText
{
    /// <summary>At most this much is copied: a page of text, not a novel.</summary>
    public const int MaxCharacters = 100_000;

    /// <summary>The recognizer's lines as text: words joined by spaces, empty lines dropped, one protocol-safe line each.</summary>
    public static IEnumerable<string> Lines(IEnumerable<OcrLine> lines) =>
        lines.Select(l => string.Join(' ', l.Words.Select(w => w.Text.Trim()).Where(t => t.Length > 0)))
             .Select(t => new string([.. t.Select(c => char.IsControl(c) ? ' ' : c)]).Trim())
             .Where(t => t.Length > 0);

    /// <summary>The text for the clipboard: lines joined with line breaks, cut at <see cref="MaxCharacters"/>; empty when there's none.</summary>
    public static string Join(IReadOnlyList<string> lines)
    {
        string text = string.Join(Environment.NewLine, lines.Select(l => l.Trim()).Where(l => l.Length > 0));
        return text.Length <= MaxCharacters ? text : text[..MaxCharacters];
    }

    /// <summary>"Copied 12 lines of text.", "Copied one line of text."</summary>
    public static string Said(int lines) => lines == 1 ? "Copied one line of text." : $"Copied {lines} lines of text.";
}
