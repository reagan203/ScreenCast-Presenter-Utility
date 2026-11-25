namespace ScreenCastPresenter.UI;

/// <summary>
/// A short yes/no question in SCP's own look (instead of a grey system message box): a heading,
/// a sentence or two, and two buttons with the actual action on them ("Set it up", not "OK").
/// Enter chooses the action, Esc cancels. Excluded from capture like every SCP window.
/// </summary>
internal sealed class ConfirmForm : ThemedForm
{
    private readonly Font _headingFont = Theme.NewFont(13f, FontStyle.Bold);

    internal ConfirmForm(string heading, string body, string yes, string? no)
    {
        Text = "ScreenCast Presenter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true; // can be found again if it ends up behind another window
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;

        // Sized by its content (a docked layout gives an auto-sizing window nothing to measure).
        var layout = new TableLayoutPanel { Location = Point.Empty, ColumnCount = 1, RowCount = 3, Padding = new Padding(24, 20, 24, 16), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var headingLabel = new Label { Text = heading, Font = _headingFont, AutoSize = true, MaximumSize = new Size(440, 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, 8) };
        var bodyLabel = new Label { Text = body, AutoSize = true, MaximumSize = new Size(440, 0), UseMnemonic = false, Tag = "dim", Margin = new Padding(0, 0, 0, 16) };
        var yesButton = new PillButton(yes, PillKind.Primary) { DialogResult = DialogResult.OK };
        yesButton.Size = yesButton.Measure();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false };
        buttons.Controls.Add(yesButton);
        // A notice has one button; Esc closes it like the button does.
        var noButton = no is null ? yesButton : new PillButton(no) { DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 8, 0) };
        if (no is not null)
        {
            noButton.Size = noButton.Measure();
            buttons.Controls.Add(noButton);
        }
        layout.Controls.Add(headingLabel, 0, 0);
        layout.Controls.Add(bodyLabel, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = yesButton;
        CancelButton = noButton;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ApplyPalette(Theme.Current);
    }

    /// <summary>A notice with a single button.</summary>
    public static void Tell(string heading, string body, string ok = "OK")
    {
        using var form = new ConfirmForm(heading, body, ok, null);
        form.ShowDialog();
    }

    /// <summary>True when the user chose <paramref name="yes"/>.</summary>
    public static bool Ask(string heading, string body, string yes, string no = "Cancel")
    {
        using var form = new ConfirmForm(heading, body, yes, no);
        return form.ShowDialog() == DialogResult.OK;
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls) Recolour(c, palette);
    }

    private static void Recolour(Control control, Palette p)
    {
        if (control is PillButton) return;
        control.BackColor = p.Background;
        control.ForeColor = control.Tag is "dim" ? p.TextDim : p.Text;
        foreach (Control child in control.Controls) Recolour(child, p);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _headingFont.Dispose();
        base.Dispose(disposing);
    }
}
