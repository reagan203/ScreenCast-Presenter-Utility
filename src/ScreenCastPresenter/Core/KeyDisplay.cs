namespace ScreenCastPresenter.Core;

/// <summary>
/// Shortcuts on the TV (FR-101), for tutorials: "Ctrl + Shift + S" appears briefly when the
/// presenter presses a key combination in what's shown. Typing is never shown: only combinations
/// with Ctrl, Alt or the Windows key, and the function keys and Esc on their own. A combination
/// that types a character on the presenter's keyboard (AltGr: Ctrl+Alt+Q is @ on a German
/// keyboard) isn't shown either, so an email address or a password can't be spelled out.
/// </summary>
internal static class KeyDisplay
{
    /// <summary>How long a combination stays on the TV.</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(1.8);

    /// <summary>How often the keyboard is looked at while it's on.</summary>
    public const int PollMs = 25;

    /// <summary>The keys watched: every key a shortcut can be named with (letters, digits, F-keys, navigation).</summary>
    public static IReadOnlyList<Keys> Watched { get; } = BuildWatched();

    private static List<Keys> BuildWatched()
    {
        var keys = new List<Keys>();
        for (Keys k = Keys.A; k <= Keys.Z; k++) keys.Add(k);
        for (Keys k = Keys.D0; k <= Keys.D9; k++) keys.Add(k);
        for (Keys k = Keys.NumPad0; k <= Keys.NumPad9; k++) keys.Add(k);
        for (Keys k = Keys.F1; k <= Keys.F24; k++) keys.Add(k);
        keys.AddRange([Keys.Space, Keys.Back, Keys.Tab, Keys.Return, Keys.Escape, Keys.Delete, Keys.Insert, Keys.Home, Keys.End,
            Keys.PageUp, Keys.PageDown, Keys.Left, Keys.Right, Keys.Up, Keys.Down]);
        return [.. keys.Where(Nameable)];
    }

    /// <summary>
    /// What to show for <paramref name="key"/> pressed with <paramref name="modifiers"/>, or null
    /// when it's typing (or types a character, per <paramref name="typesCharacter"/>) and must not be shown.
    /// </summary>
    public static string? Describe(Keys key, HotkeyModifiers modifiers, Func<Hotkey, bool>? typesCharacter = null)
    {
        if (!Nameable(key)) return null;
        bool command = (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;
        bool alone = key is >= Keys.F1 and <= Keys.F24 or Keys.Escape;
        if (!command && !alone) return null; // letters, Shift+letters, Enter, arrows…: typing and moving about, never shown
        var hotkey = new Hotkey(modifiers, key);
        bool altGr = (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt)) == (HotkeyModifiers.Control | HotkeyModifiers.Alt) && !modifiers.HasFlag(HotkeyModifiers.Win);
        if (altGr && typesCharacter?.Invoke(hotkey) == true) return null;
        IReadOnlyList<string> parts = hotkey.Parts;
        return string.Join(" + ", key == Keys.Escape ? [.. parts.Take(parts.Count - 1), "Esc"] : parts);
    }

    /// <summary>Keys with a name on every keyboard layout, and Esc (shown, though it can't be a global shortcut).</summary>
    private static bool Nameable(Keys key) => key == Keys.Escape || Hotkey.CanName(key);

    /// <summary>The keys down now that weren't before (in the order watched).</summary>
    public static List<Keys> NewlyDown(IReadOnlySet<Keys> before, IReadOnlySet<Keys> now) => [.. Watched.Where(k => now.Contains(k) && !before.Contains(k))];
}

/// <summary>What zooming in follows (FR-99).</summary>
internal enum ZoomFollows
{
    /// <summary>The mouse (as before).</summary>
    Pointer,

    /// <summary>Where the presenter types (the text cursor).</summary>
    Typing,

    /// <summary>The text cursor while typing, the mouse when it moves.</summary>
    Both,
}

/// <summary>Zoom that follows typing (FR-99): which point the zoomed view should move to. Pure, so every rule is tested.</summary>
internal static class ZoomFollow
{
    /// <summary>The text cursor counts as "being typed at" for this long after it last moved.</summary>
    public static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(2);

    public static ZoomFollows Parse(string? text) =>
        Enum.TryParse(text, ignoreCase: true, out ZoomFollows f) && Enum.IsDefined(f) ? f : ZoomFollows.Pointer;

    public static string Words(ZoomFollows f) => f switch
    {
        ZoomFollows.Typing => "where you type",
        ZoomFollows.Both => "where you type, and the mouse when it moves",
        _ => "the mouse",
    };

    /// <summary>
    /// Where the view goes now, or null to stay. <paramref name="pointer"/> is over what's shown (or
    /// null); <paramref name="caret"/> is the text cursor on what's shown (or null). Ages are since
    /// each last moved.
    /// </summary>
    public static PointF? Choose(ZoomFollows mode, PointF? pointer, TimeSpan pointerAge, PointF? caret, TimeSpan caretAge) => mode switch
    {
        ZoomFollows.Typing => caret,
        ZoomFollows.Both => caret is { } c && caretAge < TypingWindow && caretAge <= pointerAge ? c : pointer,
        _ => pointer,
    };

    /// <summary>The middle of a text cursor's box as a part of <paramref name="area"/>, or null when it isn't in it.</summary>
    public static PointF? On(Rectangle caret, Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0 || caret.Width < 0 || caret.Height <= 0) return null;
        var middle = new PointF(caret.X + caret.Width / 2f, caret.Y + caret.Height / 2f);
        if (middle.X < area.Left || middle.X > area.Right || middle.Y < area.Top || middle.Y > area.Bottom) return null;
        return new PointF((middle.X - area.X) / area.Width, (middle.Y - area.Y) / area.Height);
    }
}
