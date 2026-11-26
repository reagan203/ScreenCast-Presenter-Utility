namespace ScreenCastPresenter.UI;

/// <summary>
/// A short form in SCP's own look: a heading, a sentence, a few labelled fields (and maybe a
/// choice or a tick box), and two buttons with the action on them. Enter does it, Esc cancels.
/// Used for the name banner (FR-79), a QR code (FR-81) and saving a room (FR-82). Excluded from
/// capture like every SCP window, so what's typed (a Wi-Fi password) never reaches a meeting.
/// </summary>
internal sealed class FieldsForm : ThemedForm
{
    private readonly Font _headingFont = Theme.NewFont(13f, FontStyle.Bold);
    private readonly Font _fieldFont = Theme.NewFont(11f);
    private readonly TableLayoutPanel _layout;
    private readonly Label _problem;
    private readonly PillButton _yes;
    private int _row = 2;

    public FieldsForm(string heading, string body, string yes, string no = "Cancel")
    {
        Text = "ScreenCast Presenter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;

        _layout = new TableLayoutPanel { Location = Point.Empty, ColumnCount = 1, Padding = new Padding(24, 20, 24, 16), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _layout.Controls.Add(new Label { Text = heading, Font = _headingFont, AutoSize = true, MaximumSize = new Size(460, 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        _layout.Controls.Add(new Label { Text = body, AutoSize = true, MaximumSize = new Size(460, 0), UseMnemonic = false, Tag = "dim", Margin = new Padding(0, 0, 0, 12) }, 0, 1);
        _problem = new Label { AutoSize = true, MaximumSize = new Size(460, 0), UseMnemonic = false, Tag = "warn", Visible = false, Margin = new Padding(0, 4, 0, 4) };

        _yes = new PillButton(yes, PillKind.Primary);
        _yes.Size = _yes.Measure();
        _yes.Click += (_, _) => TryAccept();
        var cancel = new PillButton(no) { DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 8, 0) };
        cancel.Size = cancel.Measure();
        Buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        Buttons.Controls.Add(_yes);
        Buttons.Controls.Add(cancel);
        CancelButton = cancel;
        Controls.Add(_layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    private FlowLayoutPanel Buttons { get; }

    /// <summary>Checked when the action is chosen: a reason it can't be done yet (shown under the fields), or null to go ahead.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string?>? Check { get; set; }

    /// <summary>A labelled one-line field; returns its row (to show or hide) and its box.</summary>
    public (Control Row, TextBox Box) AddField(string label, string value, int maxLength, string? placeholder = null, bool secret = false)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.Add(new Label { Text = label, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 3) });
        var box = new TextBox
        {
            Text = value,
            MaxLength = maxLength,
            Width = 420,
            Font = _fieldFont,
            PlaceholderText = placeholder ?? "",
            UseSystemPasswordChar = secret,
            AccessibleName = label,
        };
        row.Controls.Add(box);
        Add(row);
        return (row, box);
    }

    /// <summary>A labelled drop-down list.</summary>
    public ComboBox AddList(string label, IReadOnlyList<string> items, int selected)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.Add(new Label { Text = label, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 3) });
        var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Font = _fieldFont, AccessibleName = label, FlatStyle = FlatStyle.Flat };
        foreach (string item in items) list.Items.Add(item);
        if (items.Count > 0) list.SelectedIndex = Math.Clamp(selected, 0, items.Count - 1);
        row.Controls.Add(list);
        Add(row);
        return list;
    }

    /// <summary>A tick box.</summary>
    public CheckBox AddCheck(string text, bool on)
    {
        var check = new CheckBox { Text = text, Checked = on, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 0, 8) };
        Add(check);
        return check;
    }

    /// <summary>A choice between a few options as pills; <paramref name="changed"/> runs when another is picked (and once now).</summary>
    public void AddChoice(IReadOnlyList<string> options, int selected, Action<int> changed)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        var pills = new List<PillButton>();
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var pill = new PillButton(options[i], i == selected ? PillKind.Selected : PillKind.Secondary) { Margin = new Padding(0, 0, 6, 0), AccessibleName = options[i] };
            pill.Size = pill.Measure();
            pill.Click += (_, _) =>
            {
                for (int j = 0; j < pills.Count; j++) pills[j].Kind = j == index ? PillKind.Selected : PillKind.Secondary;
                foreach (PillButton p in pills) p.Invalidate();
                changed(index);
                _problem.Visible = false;
            };
            pills.Add(pill);
            row.Controls.Add(pill);
        }
        Add(row);
        changed(selected);
    }

    private void Add(Control control) => _layout.Controls.Add(control, 0, _row++);

    private void TryAccept()
    {
        if (Check?.Invoke() is { } problem)
        {
            _problem.Text = problem;
            _problem.Visible = true;
            return;
        }
        DialogResult = DialogResult.OK;
    }

    /// <summary>Shows the form; true when the action was chosen (and passed <see cref="Check"/>).</summary>
    public bool Ask()
    {
        _layout.Controls.Add(_problem, 0, _row++);
        _layout.Controls.Add(Buttons, 0, _row++);
        ApplyPalette(Theme.Current);
        return ShowDialog() == DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && ActiveControl is not CheckBox and not Button)
        {
            TryAccept();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_layout.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<TextBox>()).FirstOrDefault(b => b.Visible && b.Parent!.Visible) is { } first)
        {
            first.Focus();
            first.SelectAll();
        }
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls) Recolour(c, palette);
    }

    private static void Recolour(Control control, Palette p)
    {
        if (control is PillButton) return;
        if (control is ComboBox combo)
        {
            combo.BackColor = p.IsDark ? p.Background : p.Surface;
            combo.ForeColor = p.Text;
            return;
        }
        if (control is TextBox box)
        {
            box.BackColor = p.IsDark ? p.Background : p.Surface;
            box.ForeColor = p.Text;
            return;
        }
        control.BackColor = p.Background;
        control.ForeColor = control.Tag switch { "dim" => p.TextDim, "warn" => Color.FromArgb(0xD1, 0x34, 0x38), _ => p.Text };
        foreach (Control child in control.Controls) Recolour(child, p);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headingFont.Dispose();
            _fieldFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
