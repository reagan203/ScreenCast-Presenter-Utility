using Microsoft.Win32;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.UI;

/// <summary>Colours for one appearance (SRS 6.1). No pure white anywhere.</summary>
internal sealed record Palette(
    string Name,
    bool IsDark,
    Color Background,
    Color Surface,
    Color Raised,
    Color Selected,
    Color Border,
    Color Text,
    Color TextDim,
    Color TextFaint,
    Color Accent,
    Color OnAccent,
    Color Outline,
    Color Recording,
    Color Warning,
    Color Scrim);

/// <summary>
/// Light/dark appearance (FR-44): Match Windows (default), Dark or Light; follows live changes of
/// the Windows setting and switches to system colours in High Contrast. The output never uses
/// this: it is always <see cref="OutputBlack"/>.
/// </summary>
internal static class Theme
{
    /// <summary>Output window and idle screen, whatever the theme (NFR-10).</summary>
    public static readonly Color OutputBlack = Color.FromArgb(0x0B, 0x0B, 0x0C);

    public static readonly Palette Dark = new("Dark", true,
        Hex(0x0B0B0C), Hex(0x17171A), Hex(0x24242A), Hex(0x1E1E24), Hex(0x2C2C32),
        Hex(0xE6E6E6), Hex(0x9A9AA2), Hex(0x8A8A92), Hex(0x4C8DFF), Hex(0x0B0B0C),
        Hex(0xFFC83D), Hex(0xFF5A5F), Hex(0xE8C468), Color.FromArgb(153, 0, 0, 0));

    public static readonly Palette Light = new("Light", false,
        Hex(0xF3F3F5), Hex(0xFAFAFB), Hex(0xE6E6EA), Hex(0xE4ECFB), Hex(0xD9D9DF),
        Hex(0x1B1B1F), Hex(0x5F5F68), Hex(0x63636C), Hex(0x2563D9), Hex(0xFAFAFB),
        Hex(0xF2B200), Hex(0xD93036), Hex(0xA86F00), Color.FromArgb(90, 20, 22, 28));

    private static ThemeMode _mode = ThemeMode.System;
    private static Palette? _current;
    private static SynchronizationContext? _ui;
    private static Font? _uiFont, _titleFont, _smallFont, _boldFont;

    public static event EventHandler? Changed;

    public static ThemeMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Refresh();
        }
    }

    public static Palette Current => _current ??= Resolve();

    public static Font UiFont => _uiFont ??= CreateFont(10f, FontStyle.Regular);
    public static Font BoldFont => _boldFont ??= CreateFont(10f, FontStyle.Bold);
    public static Font TitleFont => _titleFont ??= CreateFont(12f, FontStyle.Bold);
    public static Font SmallFont => _smallFont ??= CreateFont(9f, FontStyle.Regular);

    /// <summary>Call once on the UI thread at startup.</summary>
    public static void Initialize(ThemeMode mode)
    {
        _ui = SynchronizationContext.Current;
        _mode = mode;
        _current = Resolve();
        ApplyControlColorMode();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    public static void Refresh()
    {
        Palette next = Resolve();
        if (next == _current) return;
        _current = next;
        ApplyControlColorMode();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Opposite of what is showing now (for the "switch light / dark" shortcut).</summary>
    public static ThemeMode Opposite => Current.IsDark ? ThemeMode.Light : ThemeMode.Dark;

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or
            UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)) return;
        if (_ui is not null) _ui.Post(_ => Refresh(), null);
        else Refresh();
    }

    private static Palette Resolve()
    {
        if (SystemInformation.HighContrast) return HighContrast();
        return _mode switch
        {
            ThemeMode.Dark => Dark,
            ThemeMode.Light => Light,
            _ => WindowsUsesLightApps() ? Light : Dark,
        };
    }

    /// <summary>Windows' "app mode" (Settings › Personalisation › Colours). Missing value → dark.</summary>
    private static bool WindowsUsesLightApps()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>High Contrast: use the user's system colours so SCP stays readable for them.</summary>
    private static Palette HighContrast()
    {
        Color window = SystemColors.Window;
        bool dark = window.GetBrightness() < 0.5f;
        return new Palette("High contrast", dark,
            window, window, SystemColors.Control, SystemColors.Highlight, SystemColors.WindowFrame,
            SystemColors.WindowText, SystemColors.WindowText, SystemColors.GrayText, SystemColors.Highlight, SystemColors.HighlightText,
            SystemColors.Highlight, SystemColors.Highlight, SystemColors.WindowText, Color.FromArgb(120, SystemColors.WindowText));
    }

    private static void ApplyControlColorMode()
    {
#pragma warning disable WFO5001 // Dark colour mode is marked experimental; it gives native scrollbars and text boxes the right colours.
        try
        {
            Application.SetColorMode(Current.IsDark ? SystemColorMode.Dark : SystemColorMode.Classic);
        }
        catch (InvalidOperationException)
        {
            // Some WinForms versions only allow this before the first window; custom-drawn UI doesn't depend on it.
        }
#pragma warning restore WFO5001
    }

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>Segoe UI Variable ships with Windows 11; Windows 10 falls back to Segoe UI.</summary>
    private static Font CreateFont(float size, FontStyle style)
    {
        foreach (string family in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                using var probe = new FontFamily(family);
                return new Font(probe, size, style);
            }
            catch (ArgumentException)
            {
                // Not installed; try the next one.
            }
        }
        return new Font(FontFamily.GenericSansSerif, size, style);
    }

    /// <summary>Pixel-sized font for custom-drawn surfaces at a given monitor scale.</summary>
    public static Font PixelFont(float logicalPixels, double scale, FontStyle style = FontStyle.Regular) =>
        new(UiFont.FontFamily, (float)(logicalPixels * scale), style, GraphicsUnit.Pixel);

    /// <summary>Flat button in the current palette, with an outline while it has keyboard focus.</summary>
    public static void StyleButton(Button button, bool primary)
    {
        Palette p = Current;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? p.Accent : p.Surface;
        button.ForeColor = primary ? p.OnAccent : p.Text;
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.BorderColor = primary ? p.Accent : p.Border;
        button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(p.Accent, 0.2f) : p.Raised;
        button.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(p.Accent, 0.1f) : p.Border;
        button.Cursor = Cursors.Hand;
        button.AutoSize = true;
        button.Padding = new Padding(10, 4, 10, 4);
        button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = primary ? Current.Text : Current.Accent;
        button.LostFocus += (_, _) => button.FlatAppearance.BorderColor = primary ? Current.Accent : Current.Border;
    }
}
