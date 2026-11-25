namespace ScreenCastPresenter.Core;

/// <summary>
/// "Use my Windows accent colour" (Settings › Appearance): the colour from Windows' accent palette
/// that Windows 11 itself uses for buttons (a darker shade in light mode, a lighter one in dark
/// mode), adjusted only as much as needed so button text stays readable (4.5:1, WCAG AA) and the
/// buttons stand out from the page (3:1). Pure, so tests can check any accent a user might pick.
/// </summary>
internal static class AccentColour
{
    public static readonly Color LightText = Color.FromArgb(0xFA, 0xFA, 0xFB);
    public static readonly Color DarkText = Color.FromArgb(0x0B, 0x0B, 0x0C);

    /// <summary>
    /// Explorer's AccentPalette: eight RGBA colours, lightest to darkest (Light3, Light2, Light1,
    /// the accent, Dark1, Dark2, Dark3, unused). Null when the value is missing or malformed.
    /// </summary>
    public static Color? FromPalette(byte[]? palette, bool dark)
    {
        if (palette is not { Length: >= 32 }) return null;
        int index = dark ? 1 : 4; // Light2 in dark mode, Dark1 in light mode, as WinUI does
        return Color.FromArgb(palette[index * 4], palette[index * 4 + 1], palette[index * 4 + 2]);
    }

    /// <summary>DWM's AccentColor value (0xAABBGGRR), used when there is no palette.</summary>
    public static Color FromDwm(int abgr) => Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);

    /// <summary>The accent and its button text for a page of colour <paramref name="background"/>.</summary>
    public static (Color Accent, Color OnAccent) Fit(Color accent, Color background)
    {
        accent = Color.FromArgb(255, accent);
        bool darkPage = Luminance(background) < 0.2;
        for (int step = 0; step < 40; step++)
        {
            Color text = Ratio(accent, LightText) >= Ratio(accent, DarkText) ? LightText : DarkText;
            if (Ratio(accent, text) >= 4.5 && Ratio(accent, background) >= 3) return (accent, text);
            // Move away from the page: lighter on a dark page, darker on a light one.
            accent = darkPage ? Mix(accent, Color.White, 0.08) : Mix(accent, Color.Black, 0.08);
        }
        return (accent, Ratio(accent, LightText) >= Ratio(accent, DarkText) ? LightText : DarkText);
    }

    /// <summary>The selected-item tint: the accent faintly over the page.</summary>
    public static Color Tint(Color accent, Color background, bool dark) => Mix(background, accent, dark ? 0.2 : 0.12);

    public static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        int Channel(int x, int y) => (int)Math.Round(x + (y - x) * amount);
        return Color.FromArgb(255, Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(Color c)
    {
        static double Linear(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Ratio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
