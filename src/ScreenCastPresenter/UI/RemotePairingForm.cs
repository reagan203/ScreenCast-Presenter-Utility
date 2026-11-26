using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Pairing a phone as a remote (FR-90): the pairing code on the presenter's own screen, never on
/// the TV (anyone who scans it can control the presentation), hidden from capture like every SCP
/// window, so it doesn't reach a meeting either. Shows when a phone connects; "New code" makes a
/// new key, so phones paired before stop working.
/// </summary>
internal sealed class RemotePairingForm : ThemedForm
{
    /// <summary>The code's side on screen, in logical pixels: easy for a phone camera at arm's length.</summary>
    public const int CodeSide = 260;

    private readonly Font _headingFont = Theme.NewFont(13f, FontStyle.Bold);
    private readonly Panel _code = new() { Margin = new Padding(0, 8, 0, 8) };
    private readonly TextBox _address = new() { ReadOnly = true, BorderStyle = BorderStyle.None, Width = 420, TabStop = false };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(440, 0), UseMnemonic = false, Tag = "dim", Margin = new Padding(0, 6, 0, 10) };
    private bool[,]? _modules;

    public RemotePairingForm()
    {
        Text = "ScreenCast Presenter: phone remote";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(24, 20, 24, 16) };
        layout.Controls.Add(new Label { Text = "Use your phone as a remote", Font = _headingFont, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 6) });
        layout.Controls.Add(new Label
        {
            Text = "Point your phone's camera at the code and open the link. Your phone and this PC need to be on the same Wi-Fi. Only you see this code: it isn't on the TV. If Windows asks whether to allow ScreenCast Presenter on private networks, choose Allow.",
            AutoSize = true, MaximumSize = new Size(440, 0), UseMnemonic = false, Tag = "dim", Margin = new Padding(0, 0, 0, 4),
        });
        _code.Size = new Size(LogicalToDeviceUnits(CodeSide), LogicalToDeviceUnits(CodeSide));
        _code.Paint += (_, e) => PaintCode(e.Graphics);
        layout.Controls.Add(_code);
        _address.AccessibleName = "The remote's address";
        layout.Controls.Add(_address);
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var done = new PillButton("Done", PillKind.Primary) { DialogResult = DialogResult.OK };
        var newCode = new PillButton("New code") { Margin = new Padding(0, 0, 8, 0), AccessibleName = "Make a new code: phones paired before stop working" };
        var off = new PillButton("Turn the remote off", PillKind.Ghost) { Margin = new Padding(0, 0, 8, 0) };
        foreach (PillButton b in new[] { done, newCode, off })
        {
            b.Size = b.Measure();
            buttons.Controls.Add(b);
        }
        newCode.Click += (_, _) => NewCodeRequested?.Invoke(this, EventArgs.Empty);
        off.Click += (_, _) =>
        {
            TurnOffRequested?.Invoke(this, EventArgs.Empty);
            Close();
        };
        done.Click += (_, _) => Close();
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = done;
        ApplyPalette(Theme.Current);
    }

    /// <summary>"New code": a new key (phones paired before stop working).</summary>
    public event EventHandler? NewCodeRequested;

    public event EventHandler? TurnOffRequested;

    /// <summary>The address the code holds (with the key in its #fragment), or a reason there's none yet.</summary>
    public void Show(string? address, string status)
    {
        _modules = address is null ? null : QrCode.Encode(address);
        // The address without its key, for typing in by hand on the same network.
        int hash = address?.IndexOf('#') ?? -1;
        _address.Text = address is null ? "" : hash >= 0 ? address[..hash] : address;
        _status.Text = status;
        _code.Invalidate();
    }

    public void SetStatus(string status) => _status.Text = status;

    private void PaintCode(Graphics g)
    {
        g.Clear(Color.White);
        if (_modules is not { } m)
        {
            TextRenderer.DrawText(g, "Starting…", Font, _code.ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        int n = m.GetLength(0);
        int module = Math.Max(1, _code.Width / (n + 8)); // whole pixels and a quiet zone of four modules
        int offset = (_code.Width - module * n) / 2;
        using var black = new SolidBrush(Color.Black);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
            if (m[y, x]) g.FillRectangle(black, offset + x * module, offset + y * module, module, module);
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls) Recolour(c, palette);
    }

    private static void Recolour(Control control, Palette p)
    {
        if (control is PillButton || control is Panel { Parent: FlowLayoutPanel } && control.Controls.Count == 0) return;
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
