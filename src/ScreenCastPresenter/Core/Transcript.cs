using System.Globalization;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>How big live captions are on the TV (FR-89), as a share of the TV's height.</summary>
internal enum CaptionSize { Normal, Large, ExtraLarge }

/// <summary>Where live captions sit on the TV (FR-89).</summary>
internal enum CaptionPlace { Bottom, Top }

/// <summary>The look of live captions: chosen in Settings, sent to the helper as one line.</summary>
internal sealed record CaptionLook(CaptionSize Size, CaptionPlace Place)
{
    public static CaptionLook Default { get; } = new(CaptionSize.Normal, CaptionPlace.Bottom);

    /// <summary>Letter height as a share of the TV's height.</summary>
    public float TextHeight => Size switch
    {
        CaptionSize.Large => 0.056f,
        CaptionSize.ExtraLarge => 0.072f,
        _ => 0.042f,
    };

    /// <summary>Characters that fit on two lines across the TV at this size.</summary>
    public int MaxShown => Size switch
    {
        CaptionSize.Large => 84,
        CaptionSize.ExtraLarge => 64,
        _ => LiveCaptions.MaxShown,
    };

    public static CaptionSize ParseSize(string? text) =>
        Enum.TryParse(text, ignoreCase: true, out CaptionSize s) && Enum.IsDefined(s) ? s : CaptionSize.Normal;

    public static CaptionPlace ParsePlace(string? text) =>
        Enum.TryParse(text, ignoreCase: true, out CaptionPlace p) && Enum.IsDefined(p) ? p : CaptionPlace.Bottom;
}

/// <summary>One thing said, from when it began to when it was recognized.</summary>
internal sealed record TranscriptLine(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// A transcript of the talk (FR-89): what live captions heard, written as it's said, so a crash
/// keeps everything up to that point. A plain text file with times ("[00:01:23] text") and, when
/// it ends, subtitles beside it (.srt) to put under a recording. Only while transcripts are
/// switched on; written on this PC, never sent anywhere.
/// </summary>
internal sealed class TranscriptWriter : IDisposable
{
    /// <summary>A subtitle shows at least this long, so short words can be read.</summary>
    public static readonly TimeSpan MinimumShown = TimeSpan.FromSeconds(1.5);

    private readonly StreamWriter _text;
    private readonly List<TranscriptLine> _lines = [];
    private readonly DateTime _startUtc;
    private bool _disposed;

    private TranscriptWriter(string path, DateTime startUtc)
    {
        Path = path;
        _startUtc = startUtc;
        _text = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
        _text.WriteLine($"Transcript · {startUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)}");
        _text.WriteLine();
    }

    public string Path { get; }

    /// <summary>The subtitles' path, beside the text.</summary>
    public string SubtitlePath => System.IO.Path.ChangeExtension(Path, ".srt");

    /// <summary>The web subtitles' path (WebVTT, for web players and slide tools), beside the text.</summary>
    public string WebSubtitlePath => System.IO.Path.ChangeExtension(Path, ".vtt");

    /// <summary>The lines as WebVTT: the same timing as the SubRip file, in the web's format.</summary>
    public static string Vtt(IReadOnlyList<TranscriptLine> lines)
    {
        var vtt = new StringBuilder("WEBVTT\n\n");
        string srt = Srt(lines);
        foreach (string block in srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = block.Split('\n', 3);
            if (parts.Length < 3) continue;
            vtt.Append(parts[1].Replace(',', '.')).Append('\n').Append(parts[2]).Append("\n\n"); // the cue's time line uses a dot
        }
        return vtt.ToString();
    }

    public IReadOnlyList<TranscriptLine> Lines => _lines;

    /// <summary>A new transcript in <paramref name="folder"/>, named like recordings; null (with why) when it can't be written.</summary>
    public static (TranscriptWriter? Writer, string? Problem) Start(string folder, DateTime nowUtc)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = SavedFiles.NextPath(folder, "Transcript", nowUtc.ToLocalTime(), ".txt", File.Exists);
            return (new TranscriptWriter(path, nowUtc), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, "The transcript can't be saved: " + SavedFiles.Explain(ex));
        }
    }

    /// <summary>Something said: from <paramref name="startedUtc"/> (its first guess) to <paramref name="endedUtc"/>.</summary>
    public void Add(string text, DateTime startedUtc, DateTime endedUtc)
    {
        if (_disposed || (text = InkStroke.TidyText(text, 2000)).Length == 0) return;
        TimeSpan start = Max(startedUtc - _startUtc, TimeSpan.Zero), end = Max(endedUtc - _startUtc, start);
        _lines.Add(new TranscriptLine(start, end, text));
        try
        {
            _text.WriteLine($"[{Clock(start)}] {text}");
        }
        catch (IOException)
        {
            // the disk filled or went away: what was written so far stays
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string Clock(TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>The lines as SubRip subtitles: numbered, "00:00:01,500 --> 00:00:03,000", never overlapping, each shown long enough to read.</summary>
    public static string Srt(IReadOnlyList<TranscriptLine> lines)
    {
        var srt = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            TimeSpan start = lines[i].Start;
            TimeSpan end = lines[i].End - start < MinimumShown ? start + MinimumShown : lines[i].End;
            if (i + 1 < lines.Count && end > lines[i + 1].Start) end = Max(lines[i + 1].Start, start + TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond)); // never zero length
            srt.Append(i + 1).Append('\n')
               .Append(SrtTime(start)).Append(" --> ").Append(SrtTime(end)).Append('\n')
               .Append(lines[i].Text).Append("\n\n");
        }
        return srt.ToString();
    }

    private static string SrtTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");

    /// <summary>Finishes: the subtitles are written beside the text (when anything was said).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _text.Dispose();
            if (_lines.Count > 0)
            {
                File.WriteAllText(SubtitlePath, Srt(_lines), new UTF8Encoding(false));
                File.WriteAllText(WebSubtitlePath, Vtt(_lines), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
