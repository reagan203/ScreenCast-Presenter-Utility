namespace ScreenCastPresenter.UI;

/// <summary>
/// The screenshot's preview (FR-51), like the Snipping Tool's: a small picture of it near the
/// corner of the presenter's screen with Open, Copy and Show in folder. It never takes the focus,
/// is hidden from capture like every SCP window, and closes by itself after
/// <see cref="ShowFor"/> unless the pointer is on it. It holds only a small copy of the picture.
/// </summary>
internal sealed class ScreenshotPreview : ThemedForm
{
    /// <summary>How long it stays when left alone.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(8);

    /// <summary>The picture's largest side in the preview, in logical pixels.</summary>
    public const int ThumbnailSide = 280;

    /// <summary>How far from the screen's working-area corner it sits, in logical pixels.</summary>
    public const int CornerGap = 16;

    private readonly Bitmap _thumbnail;
    private readonly System.Windows.Forms.Timer _close = new();
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 0, 8) };

    /// <param name="thumbnail">A small copy of the screenshot; the preview owns and disposes it.</param>
    public ScreenshotPreview(Bitmap thumbnail, string title, string fileName, Action open, Action copy, Action showInFolder)
    {
        _thumbnail = thumbnail;
        Text = "ScreenCast Presenter: screenshot";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(12));

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        _picture.Image = thumbnail;
        _picture.Size = new Size(LogicalToDeviceUnits(Math.Min(ThumbnailSide, thumbnail.Width)), LogicalToDeviceUnits(Math.Min(ThumbnailSide, thumbnail.Height)));
        _picture.AccessibleName = "The screenshot";
        layout.Controls.Add(_picture);
        layout.Controls.Add(new Label { Text = title, AutoSize = true, UseMnemonic = false, Font = Theme.UiFont });
        layout.Controls.Add(new Label { Text = fileName, AutoSize = true, UseMnemonic = false, Tag = "dim", MaximumSize = new Size(LogicalToDeviceUnits(ThumbnailSide), 0), Margin = new Padding(0, 0, 0, 8) });
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        foreach ((string label, Action act, PillKind kind) in new[] { ("Open", open, PillKind.Primary), ("Copy", copy, PillKind.Secondary), ("Show in folder", showInFolder, PillKind.Secondary) })
        {
            var b = new PillButton(label, kind) { Radius = 10, Margin = new Padding(0, 0, LogicalToDeviceUnits(6), 0) };
            b.Size = b.Measure();
            b.Click += (_, _) =>
            {
                act();
                Close();
            };
            buttons.Controls.Add(b);
        }
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        _close.Interval = (int)ShowFor.TotalMilliseconds;
        _close.Tick += (_, _) =>
        {
            if (Bounds.Contains(Cursor.Position)) return; // being looked at: it waits
            Close();
        };
        ApplyPalette(Theme.Current);
    }

    protected override bool ShowWithoutActivation => true; // the presenter keeps typing where they were

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */;
            return cp;
        }
    }

    /// <summary>Shows it at the bottom right of <paramref name="workArea"/> (the presenter's screen) and starts its timer.</summary>
    public void ShowAt(Rectangle workArea)
    {
        CreateControl();
        PerformLayout();
        int gap = LogicalToDeviceUnits(CornerGap);
        Location = new Point(workArea.Right - Width - gap, workArea.Bottom - Height - gap);
        Show();
        _close.Start();
    }

    /// <summary>A small copy of <paramref name="image"/> for the preview (its largest side at most <paramref name="side"/> pixels).</summary>
    public static Bitmap Thumbnail(Image image, int side)
    {
        double scale = Math.Min(1, (double)side / Math.Max(1, Math.Max(image.Width, image.Height)));
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var thumb = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(thumb);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, new Rectangle(0, 0, w, h));
        return thumb;
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        BackColor = palette.Surface;
        foreach (Control c in Controls) Recolour(c, palette);
    }

    private static void Recolour(Control control, Palette p)
    {
        if (control is PillButton) return;
        control.BackColor = p.Surface;
        control.ForeColor = control.Tag is "dim" ? p.TextDim : p.Text;
        foreach (Control child in control.Controls) Recolour(child, p);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Current.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _close.Dispose();
            _picture.Image = null;
            _thumbnail.Dispose();
        }
        base.Dispose(disposing);
    }
}
