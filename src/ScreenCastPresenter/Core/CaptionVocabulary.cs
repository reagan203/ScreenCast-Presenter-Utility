namespace ScreenCastPresenter.Core;

/// <summary>
/// Words for live captions (FR-128): names of people, products and places that Windows' speech
/// recognizer doesn't know ("Ninthmind", "Kubernetes", "Wanjiru"). They're added to the recognizer's
/// word list while captions or voice commands listen, and taken out again when they stop, so the
/// presenter's Windows is left as it was. Spelled as they should appear on the TV.
/// </summary>
internal static class CaptionVocabulary
{
    public const int MaxWords = 100;
    public const int MaxLength = 40;

    /// <summary>Tidied: trimmed, one line each, no blanks or repeats (ignoring case), at most <see cref="MaxWords"/>.</summary>
    public static List<string> Normalize(IEnumerable<string?>? words)
    {
        var result = new List<string>();
        foreach (string? word in words ?? [])
        {
            string tidy = InkStroke.TidyText(word ?? "", MaxLength).Trim();
            if (tidy.Length == 0 || result.Any(r => string.Equals(r, tidy, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(tidy);
            if (result.Count == MaxWords) break;
        }
        return result;
    }

    /// <summary>A comma- or line-separated list typed in Settings, as words.</summary>
    public static List<string> FromText(string? text) =>
        Normalize((text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The words as one protocol token for the captions helper (the same escaping as automatic hiding's words).</summary>
    public static string Token(IReadOnlyList<string>? words) => SensitiveText.WordsToken(words);

    public static List<string> FromToken(string? token) => Normalize(SensitiveText.ParseWordsToken(token ?? ""));

    /// <summary>
    /// What the recognizer heard, with known words put back as they're spelled in the list ("ninth
    /// mind" → "Ninthmind" when the list has it): whole words, ignoring capitals and spacing.
    /// </summary>
    public static string Respell(string heard, IReadOnlyList<string> words)
    {
        if (heard.Length == 0 || words.Count == 0) return heard;
        string result = heard;
        foreach (string word in words.OrderByDescending(w => w.Length))
        {
            // "Ninthmind" matches "ninthmind", "ninth mind" and "Ninth-Mind"; letters only between, never across a word.
            var letters = word.Where(char.IsLetterOrDigit).Select(c => System.Text.RegularExpressions.Regex.Escape(c.ToString())).ToList();
            if (letters.Count < 3) continue;
            string pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"[\s\-]?", letters) + @"(?![\p{L}\p{N}])";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, word, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        return result;
    }
}
