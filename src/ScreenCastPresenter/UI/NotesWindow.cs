namespace ScreenCastPresenter.UI;

/// <summary>
/// Speaker notes (FR-111) on the presenter's own screen: a small window that stays on top, shows
/// the notes for what's on the TV in large type, and follows each switch. Hidden from capture like
/// every SCP window, so it never reaches the TV or a meeting.
/// </summary>
internal sealed class NotesWindow : ThemedForm
{
    private readonly Font _titleFont = Theme.NewFont(11f, FontStyle.Bold);
    private readonly Font _notesFont = Theme.NewFont(15f);
    private readonly Label _title = new() { AutoSize = false, Dock = DockStyle.Top, Height = 34, UseMnemonic = false, Padding = new Padding(12, 8, 12, 0) };
    private readonly TextBox _notes = new()
    {
        Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, TabStop = false,
        AccessibleName = "Your notes",
    };

    public NotesWindow(Action edit)
    {
        Text = "ScreenCast Presenter: my notes";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        MinimumSize = new Size(LogicalToDeviceUnits(260), LogicalToDeviceUnits(160));
        Size = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(300));
        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(work.Right - Width - LogicalToDeviceUnits(16), work.Bottom - Height - LogicalToDeviceUnits(16));
        _title.Font = _titleFont;
        _notes.Font = _notesFont;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        var editButton = new PillButton("Edit") { AccessibleName = "Edit the notes for what's shown" };
        editButton.Size = editButton.Measure();
        editButton.Click += (_, _) => edit();
        bottom.Controls.Add(editButton);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        body.Controls.Add(_notes);
        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(_title);
        ApplyPalette(Theme.Current);
    }

    /// <summary>The notes for <paramref name="title"/>; plain words when there are none.</summary>
    public void ShowNotes(string? title, string notes, bool showing)
    {
        _title.Text = !showing ? "Nothing is shown" : title is null ? "What's shown has no notes" : $"Notes: {title}";
        _notes.Text = notes.Length > 0 ? notes.Replace("\n", Environment.NewLine)
            : !showing ? "Notes appear here for whatever is on the TV."
            : title is null ? "Notes belong to favourites and scenes. Save what's shown as one, then choose Edit."
            : "No notes yet. Choose Edit to add some.";
    }

    /// <summary>What it says now (for tests).</summary>
    internal (string Title, string Notes) Showing => (_title.Text, _notes.Text);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _notesFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
