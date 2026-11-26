namespace ScreenCastPresenter.Core;

/// <summary>
/// Live captions on the TV (FR-84): what Windows' own speech recognizer heard, worked on the PC
/// itself with nothing sent anywhere. The recognizer runs in a helper process (the same program
/// started with <see cref="Switch"/>) that prints one line per guess; this turns those lines into
/// the words the TV shows: the last couple of sentences, with the current guess after them.
/// </summary>
internal sealed class LiveCaptions
{
    /// <summary>Starts the program as the speech helper.</summary>
    public const string Switch = "--captions";

    /// <summary>About two lines of text across the TV.</summary>
    public const int MaxShown = 110;

    /// <summary>Words fade from the TV after this long without anything new being said.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(6);

    private readonly List<string> _finals = [];
    private string _partial = "";
    private DateTime _last = DateTime.MinValue;
    private DateTime? _utteranceStart;

    /// <summary>How many characters the TV shows (bigger captions fit fewer, FR-89).</summary>
    public int Limit { get; set; } = MaxShown;

    /// <summary>The last thing said in full, with when it began and ended (for the transcript); null until something is.</summary>
    public (string Text, DateTime Started, DateTime Ended)? LastSaid { get; private set; }

    /// <summary>What the TV shows now; empty when nothing has been said lately.</summary>
    public string Shown { get; private set; } = "";

    /// <summary>
    /// One line from the helper: "partial words" (a guess still being made), "final words" (what
    /// was said) or "error reason". Returns true when <see cref="Shown"/> changed.
    /// </summary>
    public bool Heard(string line, DateTime now)
    {
        (string kind, string text) = Split(line);
        text = InkStroke.TidyText(text, 2000);
        switch (kind)
        {
            case "partial":
                _partial = text;
                if (text.Length > 0) _utteranceStart ??= now;
                break;
            case "final":
                _partial = "";
                if (text.Length > 0)
                {
                    _finals.Add(text);
                    LastSaid = (text, _utteranceStart ?? now, now);
                }
                _utteranceStart = null;
                if (_finals.Count > 3) _finals.RemoveAt(0);
                break;
            default:
                return false;
        }
        if (text.Length > 0) _last = now;
        return Update();
    }

    /// <summary>Called every so often: clears the words after <see cref="Silence"/>. True when <see cref="Shown"/> changed.</summary>
    public bool Tick(DateTime now)
    {
        if (Shown.Length == 0 || now - _last < Silence) return false;
        _finals.Clear();
        _partial = "";
        return Update();
    }

    public void Reset()
    {
        _finals.Clear();
        _partial = "";
        _utteranceStart = null;
        LastSaid = null;
        Shown = "";
    }

    private bool Update()
    {
        string all = string.Join(" ", _finals.Append(_partial).Where(t => t.Length > 0));
        string shown = Tail(all, Limit);
        if (shown == Shown) return false;
        Shown = shown;
        return true;
    }

    /// <summary>A helper line's kind and words.</summary>
    public static (string Kind, string Text) Split(string? line)
    {
        if (string.IsNullOrEmpty(line)) return ("", "");
        int space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }

    /// <summary>The end of <paramref name="text"/> that fits, starting at a whole word, with "…" in front when cut.</summary>
    public static string Tail(string text, int max = MaxShown)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        string end = text[^(max - 2)..];
        int space = end.IndexOf(' ');
        if (space >= 0 && space < end.Length - 1) end = end[(space + 1)..];
        return "… " + end;
    }
}
