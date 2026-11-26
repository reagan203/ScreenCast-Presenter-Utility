using System.Globalization;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>One chapter of a recording: when it starts and what was shown.</summary>
internal sealed record Chapter(TimeSpan Start, string Title);

/// <summary>
/// Chapters for recordings (FR-105): each time what's shown changes while recording (another
/// window, a scene, a break), a chapter starts. Saved beside the video when it stops, in the
/// forms people use: WebVTT chapters (web players), a YouTube-style list (paste into a
/// description), and an FFmpeg metadata file (to put them inside the video with one command).
/// </summary>
internal sealed class ChapterLog
{
    /// <summary>A chapter shorter than this is merged into the next (a quick look elsewhere isn't a chapter).</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(5);

    public const int MaxChapters = 500, MaxTitleLength = 80;

    private readonly List<Chapter> _chapters = [];

    public IReadOnlyList<Chapter> Chapters => _chapters;

    /// <summary>What's shown changed at <paramref name="at"/> (time into the recording).</summary>
    public void Add(TimeSpan at, string title)
    {
        title = InkStroke.TidyText(title, MaxTitleLength);
        if (title.Length == 0 || at < TimeSpan.Zero) return;
        at = TimeSpan.FromSeconds(Math.Floor(at.TotalSeconds));
        if (_chapters.Count > 0)
        {
            Chapter last = _chapters[^1];
            if (last.Title == title) return; // the same thing again: one chapter
            if (at < last.Start) at = last.Start; // never backwards
            if (at - last.Start < Shortest)
            {
                // The last one was too short to be a chapter: this one takes its place (and its start).
                _chapters[^1] = last with { Title = title };
                if (_chapters.Count > 1 && _chapters[^2].Title == title) _chapters.RemoveAt(_chapters.Count - 1);
                return;
            }
        }
        else at = TimeSpan.Zero; // the first chapter starts the recording
        if (_chapters.Count < MaxChapters) _chapters.Add(new Chapter(at, title));
    }

    private static string Clock(TimeSpan t, bool millis) =>
        millis ? t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
        : t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>WebVTT chapters: each runs until the next starts (the last until <paramref name="length"/>).</summary>
    public string WebVtt(TimeSpan length)
    {
        var vtt = new StringBuilder("WEBVTT\n\n");
        for (int i = 0; i < _chapters.Count; i++)
        {
            TimeSpan end = i + 1 < _chapters.Count ? _chapters[i + 1].Start : Max(length, _chapters[i].Start + TimeSpan.FromSeconds(1));
            vtt.Append("Chapter ").Append(i + 1).Append('\n')
               .Append(Clock(_chapters[i].Start, true)).Append(" --> ").Append(Clock(end, true)).Append('\n')
               .Append(_chapters[i].Title).Append("\n\n");
        }
        return vtt.ToString();
    }

    /// <summary>"0:00 Slides" lines, as video sites read chapters from a description.</summary>
    public string List() => string.Concat(_chapters.Select(c => $"{Clock(c.Start, false)} {c.Title}\n"));

    /// <summary>FFmpeg's metadata format: <c>ffmpeg -i video.mp4 -i this.txt -map_metadata 1 -codec copy out.mp4</c>.</summary>
    public string FfMetadata(TimeSpan length)
    {
        var meta = new StringBuilder(";FFMETADATA1\n");
        for (int i = 0; i < _chapters.Count; i++)
        {
            TimeSpan end = i + 1 < _chapters.Count ? _chapters[i + 1].Start : Max(length, _chapters[i].Start + TimeSpan.FromSeconds(1));
            meta.Append("\n[CHAPTER]\nTIMEBASE=1/1000\n")
                .Append("START=").Append((long)_chapters[i].Start.TotalMilliseconds).Append('\n')
                .Append("END=").Append((long)end.TotalMilliseconds).Append('\n')
                .Append("title=").Append(EscapeMeta(_chapters[i].Title)).Append('\n');
        }
        return meta.ToString();
    }

    private static string EscapeMeta(string s) => new([.. s.SelectMany(c => c is '=' or ';' or '#' or '\\' ? new[] { '\\', c } : [c])]);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>Where each file goes, beside the video.</summary>
    public static (string Vtt, string List, string Meta) PathsFor(string video)
    {
        string stem = Path.Combine(Path.GetDirectoryName(video) ?? ".", Path.GetFileNameWithoutExtension(video));
        return (stem + ".chapters.vtt", stem + ".chapters.txt", stem + ".ffmetadata.txt");
    }

    /// <summary>Writes the three files beside <paramref name="video"/>; false when there's nothing worth writing or it can't.</summary>
    public bool Save(string video, TimeSpan length)
    {
        if (_chapters.Count < 2) return false; // one chapter is no chapters
        (string vtt, string list, string meta) = PathsFor(video);
        try
        {
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(vtt, WebVtt(length), utf8);
            File.WriteAllText(list, List(), utf8);
            File.WriteAllText(meta, FfMetadata(length), utf8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }
}

/// <summary>
/// A timeline of a presentation (FR-105): what was shown when, written line by line as it happens
/// ("[09:31:05] Showing “Q3 results”"), so a crash keeps it. Only when switched on; names only.
/// </summary>
internal sealed class TimelineWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private string _last = "";

    private TimelineWriter(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    public string Path { get; }

    /// <summary>A new timeline in <paramref name="folder"/>, named by when it started; or why it can't be written.</summary>
    public static (TimelineWriter? Writer, string? Problem) Start(string folder, DateTime nowLocal)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string name = string.Create(CultureInfo.InvariantCulture, $"Timeline {nowLocal:yyyy-MM-dd HHmm}");
            string path = System.IO.Path.Combine(folder, name + ".txt");
            for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name} ({n}).txt"));
            var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            return (new TimelineWriter(path, writer), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, $"The timeline can't be saved in “{folder}”. Choose another folder in Settings.");
        }
    }

    /// <summary>What happened at <paramref name="nowLocal"/>; the same thing twice in a row is written once.</summary>
    public void Add(DateTime nowLocal, string what)
    {
        what = InkStroke.TidyText(what, 160);
        if (what.Length == 0 || what == _last) return;
        _last = what;
        try
        {
            _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{nowLocal:HH:mm:ss}] {what}"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(ex); // a disk that went away: the lines so far are kept
        }
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
