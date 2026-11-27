using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenCastPresenter.Core;

/// <summary>Something said while rehearsing, as live captions heard it.</summary>
internal sealed record SaidLine(string Text, DateTime Started, DateTime Ended);

/// <summary>One part of a rehearsal: what was shown, how long it took against its budget, and how fast it was spoken.</summary>
internal sealed record SectionReport(string Name, TimeSpan Spent, TimeSpan Budget, int Words, double WordsPerMinute)
{
    public TimeSpan Over => Budget > TimeSpan.Zero && Spent > Budget ? Spent - Budget : TimeSpan.Zero;
}

/// <summary>What a rehearsal found (FR-127).</summary>
internal sealed record RehearsalReport(
    DateTime Started,
    TimeSpan Total,
    int TalkMinutes,
    int Words,
    double WordsPerMinute,
    IReadOnlyList<(string Word, int Count)> Fillers,
    IReadOnlyList<SectionReport> Sections,
    TimeSpan LongestPause);

/// <summary>
/// Rehearsal mode (FR-127): practise the talk privately, then see how it went: the time it took
/// against the talk's length and each part's budget (FR-121), how fast it was spoken (words a
/// minute, overall and per part), filler words ("um", "you know"), and the longest pause. Worked
/// out on this PC from what live captions heard; nothing is recorded or sent anywhere. Windows'
/// recognizer leaves some fillers out, so their count is a floor, not exact.
/// </summary>
internal static partial class Rehearsal
{
    /// <summary>Words and phrases people say to fill a gap, matched whole, ignoring capitals.</summary>
    public static IReadOnlyList<string> FillerWords { get; } =
        ["um", "uh", "er", "erm", "ah", "hmm", "you know", "kind of", "sort of", "basically", "actually", "literally", "i mean", "like i said"];

    /// <summary>A comfortable speaking pace for a room: slower than this drags, faster loses people.</summary>
    public const int SlowWpm = 110, FastWpm = 170;

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    public static int CountWords(string text) => WordPattern().Count(text);

    /// <summary>Each filler and how often it was said (most first), from <paramref name="lines"/>.</summary>
    public static List<(string Word, int Count)> Fillers(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string all = " " + string.Join(" ", lines.Select(l => l.ToLowerInvariant())) + " ";
        foreach (string filler in FillerWords)
        {
            string pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(filler).Replace(@"\ ", @"\s+", StringComparison.Ordinal) + @"(?![\p{L}\p{N}])";
            int n = Regex.Count(all, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (n > 0) counts[filler] = n;
        }
        return [.. counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, c.Value))];
    }

    /// <summary>
    /// The report. <paramref name="switches"/> says what was shown from when (in order), so each
    /// thing said belongs to the part shown as it began; <paramref name="sections"/> has each part's
    /// time and budget (time stops while blank, so it's the time the room saw it).
    /// </summary>
    public static RehearsalReport Build(DateTime started, DateTime ended, IReadOnlyList<SectionTime> sections, IReadOnlyList<(DateTime At, string Key)> switches,
        IReadOnlyList<SaidLine> said, int talkMinutes)
    {
        TimeSpan total = ended > started ? ended - started : TimeSpan.Zero;
        int words = said.Sum(s => CountWords(s.Text));
        double minutes = Math.Max(total.TotalMinutes, 1.0 / 60);
        var wordsBy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SaidLine line in said)
        {
            string? key = null;
            foreach ((DateTime at, string k) in switches)
            {
                if (at > line.Started) break;
                key = k;
            }
            if (key is null) continue;
            wordsBy[key] = wordsBy.GetValueOrDefault(key) + CountWords(line.Text);
        }
        var parts = sections.Select(s =>
        {
            int w = wordsBy.GetValueOrDefault(s.Key);
            return new SectionReport(s.Name, s.Spent, s.Budget, w, s.Spent.TotalMinutes > 0.05 ? w / s.Spent.TotalMinutes : 0);
        }).ToList();
        TimeSpan pause = TimeSpan.Zero;
        for (int i = 1; i < said.Count; i++)
        {
            TimeSpan gap = said[i].Started - said[i - 1].Ended;
            if (gap > pause) pause = gap;
        }
        return new RehearsalReport(started, total, talkMinutes, words, said.Count == 0 ? 0 : words / minutes, Fillers(said.Select(s => s.Text)), parts, pause);
    }

    /// <summary>One line for the message after a rehearsal.</summary>
    public static string Summary(RehearsalReport r)
    {
        var parts = new List<string> { $"You took {BlankCountdown.Clock(r.Total)}" + (r.TalkMinutes > 0 ? $" of {r.TalkMinutes} minutes" : "") };
        if (r.Words > 0) parts.Add($"{Math.Round(r.WordsPerMinute):0} words a minute ({Pace(r.WordsPerMinute)})");
        int fillers = r.Fillers.Sum(f => f.Count);
        if (fillers > 0) parts.Add($"{fillers} filler word{(fillers == 1 ? "" : "s")}, most often “{r.Fillers[0].Word}”");
        int over = r.Sections.Count(s => s.Over > TimeSpan.Zero);
        if (over > 0) parts.Add($"{over} part{(over == 1 ? "" : "s")} over time");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>"comfortable", "a little slow", "a little fast".</summary>
    public static string Pace(double wpm) => wpm < SlowWpm ? "a little slow" : wpm > FastWpm ? "a little fast" : "comfortable";

    /// <summary>The report as a text file, plain enough to read anywhere.</summary>
    public static string Text(RehearsalReport r)
    {
        var b = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;
        b.AppendLine(inv, $"Rehearsal, {r.Started.ToLocalTime():yyyy-MM-dd HH:mm}");
        b.AppendLine();
        b.AppendLine(inv, $"Time taken:      {BlankCountdown.Clock(r.Total)}{(r.TalkMinutes > 0 ? $" (the talk is {r.TalkMinutes} minutes{(r.Total > TimeSpan.FromMinutes(r.TalkMinutes) ? $", {BlankCountdown.Clock(r.Total - TimeSpan.FromMinutes(r.TalkMinutes))} over" : "")})" : "")}");
        b.AppendLine(inv, $"Words spoken:    {r.Words}");
        b.AppendLine(inv, $"Pace:            {Math.Round(r.WordsPerMinute):0} words a minute ({Pace(r.WordsPerMinute)}; {SlowWpm}–{FastWpm} suits most rooms)");
        b.AppendLine(inv, $"Longest pause:   {BlankCountdown.Clock(r.LongestPause)}");
        b.AppendLine();
        b.AppendLine("Filler words (Windows' speech recognition leaves some out, so there may be more):");
        if (r.Fillers.Count == 0) b.AppendLine("  none heard");
        foreach ((string word, int count) in r.Fillers) b.AppendLine(inv, $"  “{word}”: {count}");
        b.AppendLine();
        b.AppendLine("Each part:");
        if (r.Sections.Count == 0) b.AppendLine("  nothing was shown");
        foreach (SectionReport s in r.Sections)
        {
            string budget = s.Budget > TimeSpan.Zero ? $" of {BlankCountdown.Clock(s.Budget)}{(s.Over > TimeSpan.Zero ? $", {BlankCountdown.Clock(s.Over)} over" : "")}" : "";
            b.AppendLine(inv, $"  {s.Name}: {BlankCountdown.Clock(s.Spent)}{budget}; {s.Words} words{(s.WordsPerMinute > 0 ? $", {Math.Round(s.WordsPerMinute):0} a minute" : "")}");
        }
        return b.ToString();
    }

    /// <summary>"Rehearsal 2026-11-26 14.05.txt".</summary>
    public static string FileName(DateTime started) => $"Rehearsal {started.ToLocalTime():yyyy-MM-dd HH.mm.ss}.txt";
}
