using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.UI;

/// <summary>
/// The check before sharing (FR-102): a private picture of what's about to be shown, with what was
/// found outlined, on the presenter's own screen (hidden from capture like every SCP window). The
/// presenter shows it as it is, turns on hiding for what was found first, or doesn't show it.
/// </summary>
internal sealed class CheckForm : ThemedForm
{
    public enum Choice { Cancel, Show, HideThenShow }

    /// <summary>The preview's longest side on screen, in logical pixels.</summary>
    public const int PreviewSide = 640;

    private readonly Font _headingFont = Theme.NewFont(13f, FontStyle.Bold);
    private readonly Image? _preview;

    /// <param name="preview">The preview's file: read into memory, so the caller can delete it at once.</param>
    public CheckForm(string sourceName, string? preview, CheckReport report, SensitiveKinds missing)
    {
        Text = "ScreenCast Presenter: check before showing";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _preview = LoadPreview(preview);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(24, 20, 24, 16) };
        layout.Controls.Add(new Label
        {
            Text = report.Clean ? $"Nothing private found in “{sourceName}”" : $"Found in “{sourceName}”",
            Font = _headingFont, AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(PreviewSide), 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, 6),
        });
        layout.Controls.Add(new Label
        {
            Text = Summary(report, missing),
            AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(PreviewSide), 0), UseMnemonic = false, Tag = "dim", Margin = new Padding(0, 0, 0, 10),
        });
        if (_preview is not null)
        {
            Size fit = Fit(_preview.Size, LogicalToDeviceUnits(PreviewSide));
            layout.Controls.Add(new PictureBox { Image = _preview, SizeMode = PictureBoxSizeMode.Zoom, Size = fit, Margin = new Padding(0, 0, 0, 12), AccessibleName = "What was found, outlined" });
        }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        bool offer = missing != SensitiveKinds.None;
        var show = new PillButton(report.Clean ? "Show it" : "Show it as it is", offer ? PillKind.Secondary : PillKind.Primary) { Tag = Choice.Show };
        var hide = new PillButton("Hide these automatically, then show it", PillKind.Primary) { Tag = Choice.HideThenShow, Margin = new Padding(0, 0, 8, 0), Visible = offer };
        var cancel = new PillButton("Don't show it", PillKind.Ghost) { Tag = Choice.Cancel, Margin = new Padding(0, 0, 8, 0) };
        foreach (PillButton b in offer ? new[] { hide, show, cancel } : [show, cancel])
        {
            b.Size = b.Measure();
            b.Click += (_, _) =>
            {
                Result = (Choice)b.Tag!;
                Close();
            };
            buttons.Controls.Add(b);
        }
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = offer ? hide : show;
        CancelButton = cancel;
        ApplyPalette(Theme.Current);
    }

    public Choice Result { get; private set; } = Choice.Cancel;

    /// <summary>What was found, in words, and what the buttons do.</summary>
    internal static string Summary(CheckReport report, SensitiveKinds missing) =>
        report.Clean
            ? "Automatic hiding looked for email addresses, numbers, keys and passwords, your own words, password boxes and faces. Only you see this."
            : $"{char.ToUpperInvariant(report.Words()[0])}{report.Words()[1..]}, outlined below. Only you see this."
              + (missing == SensitiveKinds.None ? " Automatic hiding already covers all of it on the TV." : " Automatic hiding can cover these on the TV before it's shown.");

    private static Image? LoadPreview(string? path)
    {
        if (path is null) return null;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var image = Image.FromStream(stream);
            return new Bitmap(image); // a copy: the file can go
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A picture of <paramref name="size"/> scaled to at most <paramref name="side"/> a side.</summary>
    internal static Size Fit(Size size, int side)
    {
        if (size.Width <= 0 || size.Height <= 0) return Size.Empty;
        double scale = Math.Min(1, (double)side / Math.Max(size.Width, size.Height));
        return new Size(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview?.Dispose();
            _headingFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
