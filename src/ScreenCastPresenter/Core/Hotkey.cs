namespace ScreenCastPresenter.Core;

/// <summary>Same bit values as Win32 MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN, so they pass straight to RegisterHotKey.</summary>
[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global key combination such as Ctrl+Alt+P.</summary>
internal readonly record struct Hotkey(HotkeyModifiers Modifiers, Keys Key)
{
    // Only keys whose meaning doesn't depend on the keyboard layout. Punctuation keys (OEM keys)
    // move around between layouts, so they are not offered.
    private static readonly Dictionary<Keys, string> KeyNames = BuildKeyNames();
    private static readonly Dictionary<string, Keys> KeysByName = BuildKeysByName();

    public static Hotkey CtrlAlt(Keys key) => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, key);

    /// <summary>A key with a name that doesn't depend on the keyboard layout (so it can be shown or used).</summary>
    public static bool CanName(Keys key) => KeyNames.ContainsKey(key);

    /// <summary>
    /// A usable global shortcut needs Ctrl, Alt or Win (Shift alone would block normal typing)
    /// and one supported non-modifier key.
    /// </summary>
    public bool IsValid =>
        KeyNames.ContainsKey(Key) &&
        (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;

    public Hotkey WithShift() => this with { Modifiers = Modifiers | HotkeyModifiers.Shift };

    /// <summary>Top-row digits also work from the number pad.</summary>
    public Hotkey? NumpadAlias =>
        Key is >= Keys.D0 and <= Keys.D9 ? this with { Key = Keys.NumPad0 + (Key - Keys.D0) } : null;

    /// <summary>Separate key names for drawing keycaps: ["Ctrl", "Alt", "P"].</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyNames.TryGetValue(Key, out string? name) ? name : Key.ToString());
            return parts;
        }
    }

    public override string ToString() => string.Join("+", Parts);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        Keys key = Keys.None;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= HotkeyModifiers.Control; break;
                case "ALT": modifiers |= HotkeyModifiers.Alt; break;
                case "SHIFT": modifiers |= HotkeyModifiers.Shift; break;
                case "WIN" or "WINDOWS": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (key != Keys.None || !KeysByName.TryGetValue(raw.ToUpperInvariant(), out key)) return false;
                    break;
            }
        }

        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }

    private static Dictionary<Keys, string> BuildKeyNames()
    {
        var names = new Dictionary<Keys, string>();
        for (Keys k = Keys.A; k <= Keys.Z; k++) names[k] = k.ToString();
        for (int i = 0; i <= 9; i++)
        {
            names[Keys.D0 + i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names[Keys.NumPad0 + i] = "Num " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        for (int i = 1; i <= 24; i++) names[Keys.F1 + i - 1] = "F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        names[Keys.Space] = "Space";
        names[Keys.Back] = "Backspace";
        names[Keys.Enter] = "Enter";
        names[Keys.Tab] = "Tab";
        names[Keys.PageUp] = "PgUp";
        names[Keys.PageDown] = "PgDn";
        names[Keys.Home] = "Home";
        names[Keys.End] = "End";
        names[Keys.Insert] = "Insert";
        names[Keys.Delete] = "Delete";
        names[Keys.Up] = "Up";
        names[Keys.Down] = "Down";
        names[Keys.Left] = "Left";
        names[Keys.Right] = "Right";
        names[Keys.PrintScreen] = "PrtScn";
        names[Keys.Pause] = "Pause";
        return names;
    }

    private static Dictionary<string, Keys> BuildKeysByName()
    {
        var byName = new Dictionary<string, Keys>();
        foreach ((Keys key, string name) in BuildKeyNames()) byName[name.ToUpperInvariant()] = key;
        // Friendly spellings people type.
        byName["PAGEUP"] = Keys.PageUp;
        byName["PAGEDOWN"] = Keys.PageDown;
        byName["BACK"] = Keys.Back;
        byName["RETURN"] = Keys.Enter;
        byName["DEL"] = Keys.Delete;
        byName["INS"] = Keys.Insert;
        byName["PRINTSCREEN"] = Keys.PrintScreen;
        for (int i = 0; i <= 9; i++) byName["NUMPAD" + i] = Keys.NumPad0 + i;
        return byName;
    }
}
