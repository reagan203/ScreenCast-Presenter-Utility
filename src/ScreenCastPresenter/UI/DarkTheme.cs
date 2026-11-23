namespace ScreenCastPresenter.UI;

/// <summary>The one and only theme (SRS 6.1). No pure white anywhere.</summary>
internal static class DarkTheme
{
    public static readonly Color Background = Color.FromArgb(0x0B, 0x0B, 0x0C);
    public static readonly Color Surface = Color.FromArgb(0x17, 0x17, 0x1A);
    public static readonly Color SurfaceHover = Color.FromArgb(0x24, 0x24, 0x29);
    public static readonly Color Border = Color.FromArgb(0x2C, 0x2C, 0x32);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xE6, 0xE6);
    public static readonly Color TextDim = Color.FromArgb(0x9A, 0x9A, 0xA2);
    public static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);

    private static Font? _uiFont, _titleFont;

    public static Font UiFont => _uiFont ??= CreateFont(10f, FontStyle.Regular);
    public static Font TitleFont => _titleFont ??= CreateFont(12f, FontStyle.Bold);

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

    /// <summary>Flat dark button with an accent outline while it has keyboard focus.</summary>
    public static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Accent : Surface;
        // Dark text on the accent fill reads better than light text (contrast ~6.5:1).
        button.ForeColor = primary ? Background : Text;
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.2f) : SurfaceHover;
        button.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(Accent, 0.1f) : Border;
        button.Cursor = Cursors.Hand;
        button.AutoSize = true;
        button.Padding = new Padding(10, 4, 10, 4);

        Color restBorder = button.FlatAppearance.BorderColor;
        button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = primary ? Text : Accent;
        button.LostFocus += (_, _) => button.FlatAppearance.BorderColor = restBorder;
    }
}
