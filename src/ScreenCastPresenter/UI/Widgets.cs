using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ScreenCastPresenter.UI;

/// <summary>Drawing helpers shared by the custom-drawn screens (picker, palette, shortcuts, first run).</summary>
internal static class Draw
{
    private static readonly Color[] LetterTints =
    [
        Color.FromArgb(0xE0, 0x7A, 0x5F), Color.FromArgb(0x7F, 0xB2, 0xFF), Color.FromArgb(0x6C, 0xC0, 0x8B),
        Color.FromArgb(0xE8, 0xC4, 0x68), Color.FromArgb(0x9B, 0x8C, 0xFF), Color.FromArgb(0xF2, 0x9B, 0xB4),
        Color.FromArgb(0x6C, 0xB4, 0xEE), Color.FromArgb(0xB7, 0xB7, 0xC0),
    ];

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, Color colour, RectangleF r, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = Rounded(r, radius);
        using var brush = new SolidBrush(colour);
        g.FillPath(brush, path);
    }

    public static void Stroke(Graphics g, Color colour, float width, RectangleF r, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = Rounded(RectangleF.Inflate(r, -width / 2, -width / 2), radius);
        using var pen = new Pen(colour, width);
        g.DrawPath(pen, path);
    }

    /// <summary>A stable colour per app name, for the letter badge.</summary>
    public static Color LetterTint(string app)
    {
        int hash = 17;
        foreach (char c in app.ToUpperInvariant()) hash = hash * 31 + c;
        return LetterTints[(hash & 0x7FFFFFFF) % LetterTints.Length];
    }

    /// <summary>Rounded square with the app's first letter (the mockup's app badge; no icon handles to leak).</summary>
    public static void LetterBadge(Graphics g, string app, RectangleF r, Font font)
    {
        Fill(g, LetterTint(app), r, r.Width * 0.25f);
        string letter = app.Length > 0 ? char.ToUpperInvariant(app[0]).ToString() : "?";
        TextRenderer.DrawText(g, letter, font, Rectangle.Round(r), Theme.OutputBlack,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>Draws keycaps right-aligned ending at <paramref name="right"/>; returns their total width.</summary>
    public static int KeyCaps(Graphics g, IReadOnlyList<string> parts, Font font, int right, int centreY, Palette p, float scale, Color? border = null, Color? text = null)
    {
        int pad = (int)(7 * scale), gap = (int)(4 * scale), height = (int)(24 * scale);
        var widths = parts.Select(part => Math.Max(TextRenderer.MeasureText(part, font).Width - (int)(6 * scale) + 2 * pad, (int)(24 * scale))).ToList();
        int total = widths.Sum() + gap * (widths.Count - 1);
        int x = right - total;
        for (int i = 0; i < parts.Count; i++)
        {
            var r = new Rectangle(x, centreY - height / 2, widths[i], height);
            Fill(g, p.Background, r, 6 * scale);
            Stroke(g, border ?? p.Border, Math.Max(1, scale), r, 6 * scale);
            TextRenderer.DrawText(g, parts[i], font, r, text ?? p.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            x += widths[i] + gap;
        }
        return total;
    }

    public static void Text(Graphics g, string text, Font font, Rectangle r, Color colour, bool centre = false)
    {
        TextRenderer.DrawText(g, text, font, r, colour,
            (centre ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    public static void HighQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

internal enum PillKind { Primary, Secondary, Ghost, Selected }

/// <summary>
/// A rounded button drawn in the current palette. It is a real Button underneath, so Tab, Enter,
/// Space and screen readers work as usual; the focus ring is drawn for keyboard users.
/// </summary>
internal class PillButton : Button
{
    private bool _hover;
    private bool _pressed;
    private PillKind _kind;

    public PillButton(string text, PillKind kind = PillKind.Secondary)
    {
        Text = text;
        _kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.BoldFont;
        Theme.Changed += OnThemeChanged;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PillKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Invalidate();
        }
    }

    /// <summary>Optional dim text after the label ("Display 2 · HDMI").</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Detail { get; set; } = "";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Radius { get; set; } = -1; // -1 = fully round

    public Size Measure()
    {
        Size text = TextRenderer.MeasureText(Text, Font);
        int detail = Detail.Length > 0 ? TextRenderer.MeasureText(Detail, Theme.UiFont).Width + LogicalToDeviceUnits(4) : 0;
        return new Size(text.Width + detail + LogicalToDeviceUnits(28), Math.Max(LogicalToDeviceUnits(40), text.Height + LogicalToDeviceUnits(14)));
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Invalidate();

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Background);
        Draw.HighQuality(g);

        (Color back, Color fore, Color? border) = _kind switch
        {
            PillKind.Primary => (p.Accent, p.OnAccent, null),
            PillKind.Selected => (p.Accent, p.OnAccent, null),
            PillKind.Ghost => (_hover ? p.Raised : Color.Transparent, p.Text, null),
            _ => (_hover ? p.Raised : p.Surface, p.Text, (Color?)p.Border),
        };
        if (_kind is PillKind.Primary or PillKind.Selected && (_hover || _pressed)) back = _pressed ? ControlPaint.Dark(p.Accent, 0.05f) : ControlPaint.Light(p.Accent, 0.12f);
        if (!Enabled)
        {
            back = p.Surface;
            fore = p.TextFaint;
        }

        RectangleF r = new(0.5f, 0.5f, Width - 1, Height - 1);
        float radius = Radius < 0 ? r.Height / 2 : LogicalToDeviceUnits((int)Radius);
        if (back.A > 0) Draw.Fill(g, back, r, radius);
        if (border is { } b) Draw.Stroke(g, b, 1, r, radius);
        if (Focused && ShowFocusCues) Draw.Stroke(g, _kind is PillKind.Primary or PillKind.Selected ? p.Text : p.Accent, LogicalToDeviceUnits(2), r, radius);

        if (Detail.Length == 0)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return;
        }
        Size label = TextRenderer.MeasureText(Text, Font);
        Size detail = TextRenderer.MeasureText(Detail, Theme.UiFont);
        int x = (Width - label.Width - detail.Width) / 2;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, label.Width, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        Color detailColour = _kind is PillKind.Selected or PillKind.Primary ? Color.FromArgb(200, fore) : p.TextDim;
        TextRenderer.DrawText(g, Detail, Theme.UiFont, new Rectangle(x + label.Width, 0, detail.Width, Height), detailColour, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>Rounded search field: a real TextBox (typing, IME, Narrator) inside a drawn frame with an icon.</summary>
internal sealed class SearchBox : Panel
{
    public SearchBox(string placeholder)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            PlaceholderText = placeholder,
            Font = new Font(Theme.UiFont.FontFamily, 11f),
            AccessibleName = placeholder,
        };
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        ApplyPalette();
        Theme.Changed += OnThemeChanged;
    }

    public TextBox Box { get; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Hint { get; set; } = "";

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyPalette();

    /// <summary>
    /// In dark mode Windows draws text boxes with its own near-black background whatever BackColor
    /// says, so the field is filled with the page background to match; in light mode, the surface colour.
    /// </summary>
    private static Color FieldColour(Palette p) => p.IsDark ? p.Background : p.Surface;

    private void ApplyPalette()
    {
        Box.BackColor = FieldColour(Theme.Current);
        Box.ForeColor = Theme.Current.Text;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int left = LogicalToDeviceUnits(42);
        int right = Hint.Length > 0 ? TextRenderer.MeasureText(Hint, Theme.SmallFont).Width + LogicalToDeviceUnits(24) : LogicalToDeviceUnits(14);
        Box.SetBounds(left, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - left - right), Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Background);
        Draw.HighQuality(g);
        RectangleF r = new(0.5f, 0.5f, Width - 1, Height - 1);
        float radius = LogicalToDeviceUnits(10);
        Draw.Fill(g, FieldColour(p), r, radius);
        Draw.Stroke(g, Box.Focused ? p.Accent : p.Border, Box.Focused ? LogicalToDeviceUnits(2) : 1, r, radius);

        // Magnifier icon.
        float s = DeviceDpi / 96f;
        using var pen = new Pen(p.TextDim, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = 22 * s, cy = Height / 2f - 2 * s;
        g.DrawEllipse(pen, cx - 6 * s, cy - 6 * s, 11 * s, 11 * s);
        g.DrawLine(pen, cx + 3.5f * s, cy + 3.5f * s, cx + 8 * s, cy + 8 * s);

        if (Hint.Length > 0)
        {
            Size hint = TextRenderer.MeasureText(Hint, Theme.SmallFont);
            TextRenderer.DrawText(g, Hint, Theme.SmallFont, new Rectangle(Width - hint.Width - LogicalToDeviceUnits(14), 0, hint.Width, Height), p.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Box.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            Font boxFont = Box.Font;
            base.Dispose(disposing);
            boxFont.Dispose(); // created by us in the constructor
            return;
        }
        base.Dispose(disposing);
    }
}
