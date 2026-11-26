using System.Globalization;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Recording;

/// <summary>Which sound goes into a recording besides the microphone (FR-53, FR-58).</summary>
internal enum MeetingSound
{
    /// <summary>No computer sound.</summary>
    None,

    /// <summary>Only the recorded app's sound (falls back to all sound where Windows can't separate it).</summary>
    App,

    /// <summary>Everything the computer plays.</summary>
    All,
}

/// <summary>Everything the recording helper needs to start.</summary>
/// <param name="Hidden">Parts hidden from the audience (FR-74), covered in the recording too.</param>
/// <param name="AutoHide">Automatic hiding (FR-88): what it finds is covered in the recording too.</param>
/// <param name="AutoHideWords">The presenter's own words automatic hiding covers.</param>
internal sealed record RecordingRequest(SourceWindow Source, bool Cursor, MeetingSound Sound, bool Microphone, string Path, IReadOnlyList<CropRegion>? Hidden = null,
    SensitiveKinds AutoHide = SensitiveKinds.None, IReadOnlyList<string>? AutoHideWords = null);

/// <summary>Why a recording ended.</summary>
internal enum RecordingEnd
{
    /// <summary>The user stopped it (or SCP closed).</summary>
    Stopped,

    /// <summary>The recorded window closed (FR-59).</summary>
    SourceClosed,

    /// <summary>The disk was nearly full (FR-60).</summary>
    DiskFull,

    /// <summary>Something broke; whatever was recorded so far is kept.</summary>
    Error,
}

/// <summary>
/// The line protocol between SCP and the recording helper (same exe, --record). One line each.
/// SCP → helper: "record &lt;source&gt; &lt;cursor&gt; &lt;crop&gt; &lt;sound&gt; &lt;mic&gt; &lt;hidden parts&gt; &lt;automatic hiding&gt; &lt;own words&gt; &lt;path…&gt;", then pause / resume / stop.
/// Helper → SCP: started &lt;w&gt; &lt;h&gt; · progress &lt;seconds&gt; &lt;bytes&gt; &lt;paused&gt; · warning &lt;code&gt; &lt;text&gt; ·
/// finished &lt;end&gt; &lt;seconds&gt; &lt;bytes&gt; &lt;text&gt; · failed &lt;text&gt;.
/// The helper runs on its own (FR-61): if SCP goes away it finishes the file and exits.
/// </summary>
internal static class RecordingProtocol
{
    public const string RecordSwitch = "--record";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The path goes last so it may contain spaces.</summary>
    public static string Start(RecordingRequest r) =>
        string.Create(Inv, $"record {CaptureProtocol.SourceSpec(r.Source)} {(r.Cursor ? 1 : 0)} {CaptureProtocol.Crop(r.Source.Crop)} {r.Sound.ToString().ToLowerInvariant()} {(r.Microphone ? 1 : 0)} {Hidden(r.Hidden)} {(int)r.AutoHide} {SensitiveText.WordsToken(r.AutoHideWords)} {r.Path}");

    /// <summary>The hidden parts as one word: "-" for none, else boxes separated by ";".</summary>
    private static string Hidden(IReadOnlyList<CropRegion>? hidden) =>
        hidden is not { Count: > 0 } ? "-" : string.Join(';', hidden.Take(Redactions.MaxPerSource).Select(CaptureProtocol.Crop));

    private static List<CropRegion> ParseHidden(string text) =>
        text == "-" ? [] : [.. text.Split(';').Select(CaptureProtocol.ParseCrop).OfType<CropRegion>().Take(Redactions.MaxPerSource)];

    public static RecordingRequest? ParseStart(string? line, IReadOnlyList<DisplayMonitor>? monitors = null)
    {
        string[] p = (line ?? "").TrimStart('\uFEFF').TrimEnd('\r').Split(' ', 10); // tolerate a byte-order mark and CRLF
        if (p.Length < 10 || p[0] != "record" || p[9].Trim().Length == 0) return null;
        SourceWindow? source = CaptureProtocol.ParseSource(p[1], monitors);
        if (source is null) return null;
        if (!Enum.TryParse(p[4], ignoreCase: true, out MeetingSound sound) || !Enum.IsDefined(sound)) return null;
        return new RecordingRequest(source with { Crop = CaptureProtocol.ParseCrop(p[3]) }, p[2] == "1", sound, p[5] == "1", p[9], ParseHidden(p[6]),
            CaptureProtocol.ParseAutoHide(p[7], "0").Kinds, SensitiveText.ParseWordsToken(p[8]));
    }

    public static string Started(int width, int height) => string.Create(Inv, $"started {width} {height}");

    public static string Progress(TimeSpan elapsed, long bytes, bool paused) =>
        string.Create(Inv, $"progress {(long)elapsed.TotalSeconds} {bytes} {(paused ? 1 : 0)}");

    public static string Warning(string code, string text) => $"warning {code} {Flatten(text)}";

    public static string Finished(RecordingEnd end, TimeSpan elapsed, long bytes, string text) =>
        string.Create(Inv, $"finished {end} {(long)elapsed.TotalSeconds} {bytes} {Flatten(text)}");

    public static string Failed(string text) => "failed " + Flatten(text);

    private static string Flatten(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>A parsed helper → SCP line.</summary>
internal sealed record RecordingReply(string Kind, long A, long B, bool Paused, string Code, string Text, RecordingEnd End)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static RecordingReply? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        string[] p = line.Split(' ');
        long N(int i) => p.Length > i && long.TryParse(p[i], NumberStyles.Integer, Inv, out long v) ? v : 0;
        string Rest(int from) => p.Length > from ? string.Join(' ', p[from..]) : "";
        return p[0] switch
        {
            "started" => new RecordingReply("started", N(1), N(2), false, "", "", default),
            "progress" => new RecordingReply("progress", N(1), N(2), p.Length > 3 && p[3] == "1", "", "", default),
            "warning" when p.Length > 1 => new RecordingReply("warning", 0, 0, false, p[1], Rest(2), default),
            "finished" when p.Length > 1 && Enum.TryParse(p[1], out RecordingEnd end) && Enum.IsDefined(end) =>
                new RecordingReply("finished", N(2), N(3), false, "", Rest(4), end),
            "failed" => new RecordingReply("failed", 0, 0, false, "", Rest(1), RecordingEnd.Error),
            _ => null,
        };
    }
}
