using System.Text.RegularExpressions;

namespace ScreenCastPresenter.Core;

/// <summary>What automatic hiding looks for (FR-88).</summary>
[Flags]
internal enum SensitiveKinds
{
    None = 0,
    Emails = 1,
    Phones = 2,
    Cards = 4,
    Ibans = 8,
    IdNumbers = 16,
    All = Emails | Phones | Cards | Ibans | IdNumbers,
}

/// <summary>A word the on-device text recognizer read, and where it is in the picture it read.</summary>
internal sealed record OcrWord(string Text, RectangleF Box);

/// <summary>A line of words, in reading order.</summary>
internal sealed record OcrLine(IReadOnlyList<OcrWord> Words);

/// <summary>Something found, and the box (in the recognized picture's pixels) that covers it.</summary>
internal sealed record SensitiveMatch(SensitiveKinds Kind, RectangleF Box);

/// <summary>
/// Automatic hiding (FR-88): finds email addresses, phone numbers, payment card numbers (checked
/// with the Luhn sum), IBANs (checked with mod 97) and ID numbers (US SSN, UK National Insurance)
/// in text read from the picture, and says which words to cover. Pure, so every rule is tested;
/// the reading itself happens in the capture helper, on the PC.
/// </summary>
internal static partial class SensitiveText
{
    /// <summary>Boxes grow by this share of their height on each side, so letters' edges never peek out.</summary>
    public const float Padding = 0.25f;

    /// <summary>The fewest digits any number looked for has (an ID number: 6; a phone: 9): a line with fewer is skipped.</summary>
    public const int MinDigits = 6;

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // A run of digits with the separators people write numbers with.
    [GeneratedRegex(@"(?<![\w+])[+(]?\d[\d ().\-/]{5,}\d(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Iban();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b|\b[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z] ?\d{2} ?\d{2} ?\d{2} ?[A-D]\b", RegexOptions.CultureInvariant)]
    private static partial Regex IdNumber();

    [GeneratedRegex(@"^\d{4}[\-/.]\d{1,2}[\-/.]\d{1,2}$|^\d{1,2}[\-/.]\d{1,2}[\-/.]\d{2,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex Date();

    /// <summary>Everything of <paramref name="kinds"/> in <paramref name="lines"/>, each as one box over its words.</summary>
    public static List<SensitiveMatch> Find(IReadOnlyList<OcrLine> lines, SensitiveKinds kinds)
    {
        var found = new List<SensitiveMatch>();
        if (kinds == SensitiveKinds.None) return found;
        // Kept across lines: a page of text makes no garbage per line.
        var text = new System.Text.StringBuilder();
        var spans = new List<(int Start, int End)>();
        var covered = new List<(int Start, int End)>();
        foreach (OcrLine line in lines)
        {
            if (line.Words.Count == 0) continue;
            // The line's text with each word's place in it, so a match can be mapped back to words.
            text.Clear();
            spans.Clear();
            covered.Clear();
            int digits = 0;
            bool at = false;
            for (int i = 0; i < line.Words.Count; i++)
            {
                if (i > 0) text.Append(' ');
                string word = line.Words[i].Text;
                spans.Add((text.Length, text.Length + word.Length));
                text.Append(word);
                foreach (char c in word)
                {
                    if (char.IsAsciiDigit(c)) digits++;
                    else if (c == '@') at = true;
                }
            }
            // Most lines are words: only the checks a line could pass are run.
            if (!at && digits < MinDigits) continue;
            string s = text.ToString();

            void Add(SensitiveKinds kind, int start, int length)
            {
                int end = start + length;
                if (covered.Any(c => start < c.End && end > c.Start)) return; // already covered as something else
                covered.Add((start, end));
                RectangleF? box = null;
                for (int i = 0; i < spans.Count; i++)
                {
                    if (spans[i].End <= start || spans[i].Start >= end) continue;
                    RectangleF w = line.Words[i].Box;
                    box = box is { } b ? RectangleF.Union(b, w) : w;
                }
                if (box is { } r) found.Add(new SensitiveMatch(kind, Pad(r)));
            }

            if (at && kinds.HasFlag(SensitiveKinds.Emails))
            {
                // An address is one word to the recognizer: only words with an @ are looked at.
                for (int i = 0; i < line.Words.Count; i++)
                {
                    string word = line.Words[i].Text;
                    if (word.Contains('@') && Email().Match(word) is { Success: true } m) Add(SensitiveKinds.Emails, spans[i].Start + m.Index, m.Length);
                }
            }
            if (digits < MinDigits) continue;
            if (kinds.HasFlag(SensitiveKinds.Ibans))
                foreach (Match m in Iban().Matches(s)) if (IsIban(m.Value)) Add(SensitiveKinds.Ibans, m.Index, m.Length);
            if (kinds.HasFlag(SensitiveKinds.IdNumbers))
                foreach (Match m in IdNumber().Matches(s)) Add(SensitiveKinds.IdNumbers, m.Index, m.Length);
            if ((kinds & (SensitiveKinds.Cards | SensitiveKinds.Phones)) != 0)
            {
                foreach (Match m in DigitRun().Matches(s))
                {
                    string run = m.Value.TrimEnd(' ', '.', '-', '/');
                    if (kinds.HasFlag(SensitiveKinds.Cards) && IsCard(run)) Add(SensitiveKinds.Cards, m.Index, run.Length);
                    else if (kinds.HasFlag(SensitiveKinds.Phones) && IsPhone(run)) Add(SensitiveKinds.Phones, m.Index, run.Length);
                }
            }
        }
        return found;
    }

    private static RectangleF Pad(RectangleF r)
    {
        float pad = r.Height * Padding;
        return RectangleF.Inflate(r, pad, pad);
    }

    private static int DigitCount(ReadOnlySpan<char> text)
    {
        int n = 0;
        foreach (char c in text) if (char.IsAsciiDigit(c)) n++;
        return n;
    }

    /// <summary>13 to 19 digits in groups as cards are written, passing the Luhn check.</summary>
    public static bool IsCard(string text)
    {
        if (text.AsSpan().IndexOfAny("()+/.") >= 0) return false;
        int count = DigitCount(text);
        if (count is < 13 or > 19) return false;
        int sum = 0;
        bool twice = false;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(text[i])) continue;
            int d = text[i] - '0';
            if (twice && (d *= 2) > 9) d -= 9;
            sum += d;
            twice = !twice;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// 9 to 15 digits that look like a phone number: starting with +, 00, 0 or a bracket, or written
    /// in groups. A plain number (an amount, an order number) or a date isn't one.
    /// </summary>
    public static bool IsPhone(string text)
    {
        int count = DigitCount(text);
        if (count is < 9 or > 15 || Date().IsMatch(text)) return false;
        int separators = text.Count(c => c is ' ' or '-' or '.' or '(' or ')');
        bool marked = text.StartsWith('+') || text.StartsWith('(') || text.StartsWith("00", StringComparison.Ordinal) || text.StartsWith('0');
        if (text.Contains('/')) return false; // fractions and dates
        if (!marked && text.Contains('.') && !text.Any(c => c is ' ' or '-' or '(' or ')'))
        {
            // Dots only: 555.010.9999 is a phone; 10.0.19041.5072 (a version) and 192.168.10.20 (an address) aren't.
            string[] groups = text.Split('.');
            if (groups.Length == 4 && groups.All(g => g.Length <= 3)) return false;
            if (groups.Any(g => g.Length is < 2 or > 4)) return false;
        }
        return marked || separators >= 2;
    }

    /// <summary>An IBAN whose check digits are right (ISO 13616: mod 97 equals 1).</summary>
    public static bool IsIban(string text)
    {
        string iban = new([.. text.Where(c => c != ' ')]);
        if (iban.Length is < 15 or > 34) return false;
        string moved = iban[4..] + iban[..4];
        int remainder = 0;
        foreach (char c in moved)
        {
            if (char.IsAsciiDigit(c)) remainder = (remainder * 10 + (c - '0')) % 97;
            else if (char.IsAsciiLetterUpper(c)) remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
            else return false;
        }
        return remainder == 1;
    }

    /// <summary>"2 email addresses and a phone number".</summary>
    public static string Words(IReadOnlyList<SensitiveMatch> found)
    {
        var parts = new List<string>();
        foreach ((SensitiveKinds kind, string one, string many) in new[]
        {
            (SensitiveKinds.Emails, "an email address", "email addresses"),
            (SensitiveKinds.Phones, "a phone number", "phone numbers"),
            (SensitiveKinds.Cards, "a card number", "card numbers"),
            (SensitiveKinds.Ibans, "a bank account number", "bank account numbers"),
            (SensitiveKinds.IdNumbers, "an ID number", "ID numbers"),
        })
        {
            int n = found.Count(f => f.Kind == kind);
            if (n > 0) parts.Add(n == 1 ? one : $"{n} {many}");
        }
        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    /// <summary>Boxes in a picture of <paramref name="size"/> pixels as fractions of it, for the TV.</summary>
    public static List<CropRegion> ToRegions(IEnumerable<SensitiveMatch> found, SizeF size, int max = Redactions.MaxAutomatic)
    {
        var regions = new List<CropRegion>();
        if (size.Width <= 0 || size.Height <= 0) return regions;
        foreach (SensitiveMatch m in found)
        {
            if (CropRegion.Create(m.Box.X / size.Width, m.Box.Y / size.Height, m.Box.Width / size.Width, m.Box.Height / size.Height, Redactions.MinAutomaticSize) is { } r) regions.Add(r);
            if (regions.Count == max) break;
        }
        return regions;
    }
}
