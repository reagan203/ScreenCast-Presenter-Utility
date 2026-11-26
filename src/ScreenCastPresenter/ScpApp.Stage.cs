using System.Diagnostics;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// The stage tools (SRS 3.8, FR-74 to FR-85): hiding parts of a window, the audience view and
/// "get ready, then go", the spotlight, the name banner, your logo, a QR code, room profiles,
/// schedules, live captions and a second TV. Everything drawn on the TV goes through the capture
/// helper (it draws inside the output, so a meeting sharing it sees the same), and every window
/// these open is on the presenter's screen and hidden from capture.
/// </summary>
internal sealed partial class ScpApp
{
    // ───────────────────────────── hide part of a window (FR-74) ─────────────────────────────

    /// <summary>Ctrl+Alt+X: drag a box over what the audience mustn't see. It stays on that spot of the window, remembered per app.</summary>
    private async Task HidePartAsync()
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { } source)
        {
            Message("Show something first, then hide part of it from the audience.");
            return;
        }
        if (source.IsCamera)
        {
            Message("A camera's picture isn't on your screen to hide part of. Point the camera away from it instead.");
            return;
        }
        if (Redactions.For(Settings, source).Count >= Redactions.MaxPerSource)
        {
            Message($"“{SourceName(source)}” already has {Redactions.MaxPerSource} hidden parts, the most it can have.", BadgeDot.Warning,
                Button("Show the hidden parts", ShowHiddenPartsAsync, primary: false));
            return;
        }
        if (await ChoosePartAsync(source, "Drag over what the audience mustn't see, then let go", startFromCrop: false) is not { } choice) return;
        if (_session.Source is not { } still || !still.IsSameSource(source)) return; // something else is shown now
        CropRegion box = choice.Crop ?? CropRegion.Full;
        Redactions.Add(Settings, source, box);
        Save();
        await ApplyHiddenPartsAsync();
        string look = Settings.HiddenPartStyle == RedactStyle.Blurred ? "a blur" : "a solid box";
        string where = source.IsScreen ? "that part of the screen" : $"that spot of the window, and it's remembered for {source.AppName}";
        Message(Redactions.CoversAll(box, _session.View ?? source.Crop)
                ? "That covers everything shown, so the audience sees only a box. Undo it if that's not what you meant."
                : $"Hidden from the audience: they see {look} there. It stays on {where}.",
            BadgeDot.Accent, Button("Undo", UndoHiddenPartAsync, primary: false), Button("Show all hidden parts", ShowHiddenPartsAsync, primary: false));
    }

    /// <summary>The last box added goes.</summary>
    private async Task UndoHiddenPartAsync()
    {
        if (_session.Source is not { } source || Redactions.Key(source) is not { } key || !Settings.HiddenParts.TryGetValue(key, out List<CropRegion>? boxes) || boxes.Count == 0) return;
        boxes.RemoveAt(boxes.Count - 1);
        if (boxes.Count == 0) Settings.HiddenParts.Remove(key);
        Save();
        await ApplyHiddenPartsAsync();
        Message(boxes.Count == 0 ? "Nothing is hidden any more." : "The last hidden part shows again.");
    }

    /// <summary>Ctrl+Alt+Shift+X: everything hidden from what's shown is seen again (and forgotten for that app).</summary>
    private async Task ShowHiddenPartsAsync()
    {
        if (_session.Source is not { } source)
        {
            Message("Nothing is being shown right now.");
            return;
        }
        if (!Redactions.Clear(Settings, source))
        {
            Message("Nothing is hidden in what's shown.");
            return;
        }
        Save();
        await ApplyHiddenPartsAsync();
        Message($"Everything in “{SourceName(source)}” shows again.");
    }

    /// <summary>
    /// The boxes changed: both TVs cover (or uncover) them now. Shown the light way, which can't
    /// cover anything, it's shown again through the helper first, through black.
    /// </summary>
    private async Task ApplyHiddenPartsAsync()
    {
        if (!_session.UpdateHiddenParts()) await ReshowAsync();
        if (_second is { } second && !second.UpdateHiddenParts()) await second.RestartEngineAsync();
        _tray.Refresh();
    }

    /// <summary>Settings › Privacy › How hidden parts look.</summary>
    public void SetHiddenPartStyle(RedactStyle style)
    {
        Settings.HiddenPartStyle = style;
        Save();
        _ = GuardAsync(ApplyHiddenPartsAsync);
    }

    /// <summary>Settings › Privacy: forget the hidden parts kept for one app or screen.</summary>
    public void ForgetHiddenParts(string key)
    {
        if (!Settings.HiddenParts.Remove(key)) return;
        Save();
        _ = GuardAsync(ApplyHiddenPartsAsync);
    }

    private static string SourceName(SourceWindow source) =>
        source.Screen is { } screen ? screen.Name : TrayMenuPlan.Shorten(source.Title.Length > 0 ? source.Title : source.AppName, 40);

    // ───────────────────────────── what the audience sees, and what's next (FR-75, FR-76) ─────────────────────────────

    private StudioWindow? _studio;

    /// <summary>What's ready to be shown next (FR-76); null when nothing is.</summary>
    private SourceWindow? _next;

    public bool AudienceViewOpen => _studio is { IsDisposed: false };
    public SourceWindow? Next => _next;

    /// <summary>Ctrl+Alt+V: the private window that shows exactly what the audience sees, live; again closes it.</summary>
    private void ToggleAudienceView()
    {
        if (AudienceViewOpen)
        {
            _studio!.Close();
            return;
        }
        OpenStudio();
        Message(_session.State == SessionState.Off
            ? "This window shows what the audience sees, once you show something. Only you see it."
            : "This window shows exactly what the audience sees, live. Only you see it.");
    }

    private void OpenStudio()
    {
        if (AudienceViewOpen)
        {
            if (_studio!.WindowState == FormWindowState.Minimized) _studio.WindowState = FormWindowState.Normal;
            _studio.UpdateFrom(this);
            return;
        }
        var studio = new StudioWindow(this);
        studio.FormClosed += (_, _) => Guard(() =>
        {
            if (studio.WindowState == FormWindowState.Normal)
            {
                Settings.StudioWindow = WindowBounds.From(studio.Bounds);
                Save();
            }
            _studio = null; // a closed modeless window disposes itself
            UpdatePointerWork();
            ScheduleTrim();
        });
        _studio = studio;
        PlaceStudio(studio);
        studio.Show();
        studio.UpdateFrom(this);
        // A copy of the TV's window needs the helper to draw it (a copy of a copy shows nothing).
        ApplyMarksChange();
    }

    /// <summary>Where it was last, if that's still on a screen the presenter uses; else the bottom right of their screen.</summary>
    private void PlaceStudio(StudioWindow studio)
    {
        if (Settings.StudioWindow?.ToRectangle() is { } saved && PresenterScreens().Any(m => m.WorkArea.IntersectsWith(saved) && Rectangle.Intersect(m.WorkArea, saved).Width > 120))
        {
            studio.Bounds = saved;
            return;
        }
        if (PresenterScreen() is not { } screen) return;
        Rectangle area = screen.WorkArea;
        Size size = new(Math.Min(studio.Width, area.Width - 40), Math.Min(studio.Height, area.Height - 40));
        studio.Bounds = new Rectangle(area.Right - size.Width - 24, area.Bottom - size.Height - 24, size.Width, size.Height);
    }

    /// <summary>The audience view's left half: the TV's window, its size, and a few words on its state.</summary>
    public (nint Output, Size Size, string State) AudienceView()
    {
        SessionController s = _session;
        nint output = s.OutputHandle;
        Size size = output != 0 ? WindowEnumerator.ClientSize(output) : Size.Empty;
        string state = s.State switch
        {
            SessionState.Off => "Nothing is shown",
            SessionState.IdleScreen => $"{OutputNameCapitalised()} shows a blank screen",
            _ when s.IsBlanked => $"{OutputNameCapitalised()} is blank",
            _ when s.IsHeld => "Black for now (privacy)",
            _ when s.IsFrozen => $"Frozen · {SourceName(s.Source!)}",
            _ => $"{ShowingText(frozen: false)} · {SourceName(s.Source!)}",
        };
        return (output, size, state);
    }

    /// <summary>The audience view's right half: what's next and the part of it that will show.</summary>
    public (SourceWindow? Next, Rectangle Part) NextView()
    {
        if (_next is not { } next) return (null, Rectangle.Empty);
        if (next.IsWindow && !WindowEnumerator.IsAlive(next.Handle))
        {
            _next = null; // it closed
            return (null, Rectangle.Empty);
        }
        if (!next.IsWindow) return (next, Rectangle.Empty);
        Size client = WindowEnumerator.ClientSize(next.Handle);
        CropRegion? crop = next.Crop ?? (Settings.SavedCrops.TryGetValue(next.AppName, out CropRegion? saved) ? saved : null);
        Rectangle part = crop?.ToPixels(client) ?? new Rectangle(Point.Empty, client);
        return (next, part);
    }

    /// <summary>Ctrl+Alt+Shift+G: choose what's next. It's made ready privately and shown with Go.</summary>
    private void ChooseNext() => OpenPicker(PickerMode.Next);

    /// <summary>Makes <paramref name="source"/> what's next: you see it in the audience view; the audience doesn't, until Go.</summary>
    public Task GetReadyAsync(SourceWindow source)
    {
        if (IsNeverShown(source))
        {
            Message($"“{source.AppName}” is on your never-show list, so it can't be shown.", BadgeDot.Warning);
            return Task.CompletedTask;
        }
        if (source.IsWindow && !WindowEnumerator.IsAlive(source.Handle))
        {
            Message($"“{TrayMenuPlan.Shorten(source.Title, 40)}” has closed.");
            return Task.CompletedTask;
        }
        _next = source;
        OpenStudio();
        _studio?.UpdateFrom(this);
        _tray.Refresh();
        Message($"Ready: “{SourceName(source)}”. The audience doesn't see it yet{OrPressGo()}.", BadgeDot.Accent, Button("Show it now", GoAsync));
        return Task.CompletedTask;
    }

    private string OrPressGo() => ActiveShortcut("go") is { } key ? $"; press {key} to show it" : "";

    /// <summary>Ctrl+Alt+G: shows what's ready, through black like any switch.</summary>
    private async Task GoAsync()
    {
        if (_next is not { } next)
        {
            Message($"Nothing is ready. Get something ready first{OrPress("get-ready")} and it waits here until you show it.", BadgeDot.Accent,
                Button("Get something ready…", ChooseNext));
            return;
        }
        if (next.IsWindow)
        {
            if (WindowEnumerator.Refresh(next) is not { } alive)
            {
                _next = null;
                _studio?.UpdateFrom(this);
                Message($"“{TrayMenuPlan.Shorten(next.Title, 40)}” has closed, so there's nothing to show.");
                return;
            }
            next = alive with { Crop = next.Crop };
        }
        _next = null;
        await PresentAsync(next);
        _studio?.UpdateFrom(this);
        _tray.Refresh();
    }

    public void ClearNext()
    {
        if (_next is null) return;
        _next = null;
        _studio?.UpdateFrom(this);
        _tray.Refresh();
        Message("Nothing is ready now.");
    }

    // ───────────────────────────── spotlight (FR-77) ─────────────────────────────

    private bool _spotlightOn;
    private PointF? _sentSpotlight;

    public bool IsSpotlightOn => _spotlightOn;

    /// <summary>Ctrl+Alt+Shift+F: everything on the TV dims except a circle around the mouse.</summary>
    private void SetSpotlight(bool on)
    {
        if (on && _session.State != SessionState.Presenting)
        {
            Message("Show something first, then use the spotlight.");
            return;
        }
        if (on == _spotlightOn) return;
        _spotlightOn = on;
        if (!on)
        {
            _session.Effects?.Spotlight(null);
            _sentSpotlight = null;
        }
        ApplyMarksChange();
        Message(on ? "Spotlight on: the TV is dimmed except around your mouse." : "Spotlight off.", BadgeDot.Accent,
            Button(on ? "Turn it off" : "Turn it on", () => SetSpotlight(!on), primary: false));
        _tray.Refresh();
    }

    /// <summary>From the pointer tick: the spotlight follows the pointer over what's shown (null when it isn't over it).</summary>
    private void SendSpotlight(IEffectsEngine effects, PointF? at)
    {
        PointF? spot = _spotlightOn ? at : null;
        if (spot == _sentSpotlight) return;
        _sentSpotlight = spot;
        effects.Spotlight(spot);
    }

    // ───────────────────────────── name banner (FR-79) ─────────────────────────────

    private bool _captionOn;

    public bool IsCaptionOn => _captionOn;

    /// <summary>Ctrl+Alt+Shift+N: a name banner along the bottom of the TV, e.g. "Jane Doe · Head of Sales"; again takes it off.</summary>
    private void ToggleCaption()
    {
        if (_captionOn)
        {
            SetCaption(false);
            return;
        }
        if (_session.State != SessionState.Presenting)
        {
            Message("Show something first, then put your name on the TV.");
            return;
        }
        if (Settings.CaptionName.Length == 0 && !EditCaption()) return;
        SetCaption(true);
    }

    private void SetCaption(bool on)
    {
        _captionOn = on;
        if (on) _session.Effects?.Caption(Settings.CaptionName, Settings.CaptionSecond);
        else _session.Effects?.Caption(null, null);
        ApplyMarksChange();
        _tray.Refresh();
        if (on)
            Message($"“{NameCaption.Words(Settings.CaptionName, Settings.CaptionSecond)}” shows along the bottom of the TV.", BadgeDot.Accent,
                Button("Take it off", () => SetCaption(false), primary: false), Button("Change it…", () => { if (EditCaption()) SetCaption(true); }, primary: false));
        else Message("The name banner is off.");
    }

    /// <summary>Asks for the banner's two lines; true when they were set.</summary>
    public bool EditCaption()
    {
        using var form = new FieldsForm("Your name on the TV", "It shows along the bottom of the TV while you present, like on the news.", "Save");
        (_, TextBox name) = form.AddField("Name", Settings.CaptionName, NameCaption.MaxLength, "Jane Doe");
        (_, TextBox second) = form.AddField("Second line (optional)", Settings.CaptionSecond, NameCaption.MaxLength, "Head of Sales");
        form.Check = () => NameCaption.Tidy(name.Text).Length == 0 ? "Type a name." : null;
        if (!form.Ask()) return false;
        Settings.CaptionName = NameCaption.Tidy(name.Text);
        Settings.CaptionSecond = NameCaption.Tidy(second.Text);
        Save();
        if (_captionOn) _session.Effects?.Caption(Settings.CaptionName, Settings.CaptionSecond);
        return true;
    }

    /// <summary>Settings › Where it shows › Your name on the TV.</summary>
    public void SetCaptionName(string name)
    {
        string tidy = NameCaption.Tidy(name);
        if (tidy == Settings.CaptionName) return;
        Settings.CaptionName = tidy;
        Save();
        if (_captionOn)
        {
            if (tidy.Length == 0) SetCaption(false);
            else _session.Effects?.Caption(Settings.CaptionName, Settings.CaptionSecond);
        }
    }

    public void SetCaptionSecond(string second)
    {
        string tidy = NameCaption.Tidy(second);
        if (tidy == Settings.CaptionSecond) return;
        Settings.CaptionSecond = tidy;
        Save();
        if (_captionOn) _session.Effects?.Caption(Settings.CaptionName, Settings.CaptionSecond);
    }

    // ───────────────────────────── your logo on the blank screen (FR-80) ─────────────────────────────

    /// <summary>Settings › Appearance: a logo above the blank screen's message. Copied next to the settings, so moving the original doesn't lose it.</summary>
    public bool SetBlankLogo(string? original)
    {
        if (original is null)
        {
            string? old = Settings.BlankLogo;
            Settings.BlankLogo = null;
            Save();
            ApplyLogo();
            TryDeleteLogoCopy(old);
            Message("The blank screen shows just its message now.");
            return true;
        }
        if (LogoImage.Problem(original) is { } problem)
        {
            Message(problem, BadgeDot.Warning);
            return false;
        }
        string folder = Path.GetDirectoryName(SettingsFilePath) ?? ".";
        string copy = LogoImage.CopyPath(folder, original);
        try
        {
            Directory.CreateDirectory(folder);
            if (!string.Equals(Path.GetFullPath(original), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
            {
                foreach (string ext in LogoImage.Extensions) // only one copy is kept
                {
                    string other = Path.ChangeExtension(copy, ext);
                    if (!string.Equals(other, copy, StringComparison.OrdinalIgnoreCase)) TryDeleteLogoCopy(other);
                }
                File.Copy(original, copy, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Message("The logo couldn't be saved: " + SavedFiles.Explain(ex), BadgeDot.Warning);
            return false;
        }
        Settings.BlankLogo = copy;
        Save();
        ApplyLogo();
        Message("Your logo shows above the message when you blank the TV with a message.", BadgeDot.Accent,
            Button("See it", () => _ = RunAsync("blank-message"), primary: false));
        return true;
    }

    /// <summary>Both TVs take the logo now; a missing file is simply no logo.</summary>
    private void ApplyLogo()
    {
        string? logo = Settings.BlankLogo is { } path && File.Exists(path) ? path : null;
        _session.SetLogo(logo);
        _second?.SetLogo(logo);
    }

    private void TryDeleteLogoCopy(string? path)
    {
        if (path is null || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetDirectoryName(Path.GetFullPath(SettingsFilePath)), StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }

    // ───────────────────────────── QR code (FR-81) ─────────────────────────────

    /// <summary>What the QR code on the TV holds; null when there's none.</summary>
    private string? _qrText;

    public bool HasQr => _qrText is not null;

    /// <summary>Ctrl+Alt+Q: a QR code for a link or the Wi-Fi in a corner of the TV; again takes it off.</summary>
    private void ToggleQr()
    {
        if (_qrText is not null)
        {
            SetQr(null);
            Message("The QR code is off.");
            return;
        }
        if (_session.State != SessionState.Presenting)
        {
            Message("Show something first, then put a QR code on the TV.");
            return;
        }
        if (AskForQr() is { } text) SetQr(text);
    }

    /// <summary>Link or Wi-Fi. Made on this PC; the Wi-Fi password is never saved.</summary>
    private string? AskForQr()
    {
        using var form = new FieldsForm("A QR code on the TV", "People point their phone's camera at it. It's made on this PC, with no internet needed.", "Show it");
        (Control linkRow, TextBox link) = form.AddField("Link", Settings.QrLink ?? "", 400, "https://example.com");
        (Control nameRow, TextBox network) = form.AddField("Wi-Fi network", Settings.QrWifiName ?? "", 32, "Guest Wi-Fi");
        (Control passwordRow, TextBox password) = form.AddField("Password", "", 63, "", secret: true);
        CheckBox open = form.AddCheck("The network has no password", false);
        bool wifi = false;
        // Placed above the fields: the choice shows only the fields that apply.
        form.AddChoice(["Link", "Wi-Fi"], Settings.QrLink is null && Settings.QrWifiName is not null ? 1 : 0, choice =>
        {
            wifi = choice == 1;
            linkRow.Visible = !wifi;
            nameRow.Visible = passwordRow.Visible = open.Visible = wifi;
        });
        open.CheckedChanged += (_, _) => passwordRow.Enabled = !open.Checked;
        form.Check = () => QrInputProblem(wifi, link.Text, network.Text, password.Text, open.Checked);
        if (!form.Ask()) return null;
        if (wifi)
        {
            Settings.QrWifiName = network.Text.Trim();
            Settings.QrLink = null;
            Save();
            return QrCode.WiFi(network.Text.Trim(), open.Checked ? "" : password.Text, open.Checked);
        }
        Settings.QrLink = link.Text.Trim();
        Save();
        return link.Text.Trim();
    }

    /// <summary>Why what was typed can't be a QR code, or null when it can.</summary>
    internal static string? QrInputProblem(bool wifi, string link, string network, string password, bool open)
    {
        if (!wifi)
        {
            if (link.Trim().Length == 0) return "Type or paste a link.";
            return QrCode.Encode(link.Trim()) is null ? $"That's too long for a QR code on the TV (at most {QrCode.MaxBytes} letters)." : null;
        }
        if (network.Trim().Length == 0) return "Type the Wi-Fi network's name.";
        if (!open && password.Length is < 8 or > 63) return "A Wi-Fi password has 8 to 63 characters. Tick “no password” for an open network.";
        return QrCode.Encode(QrCode.WiFi(network.Trim(), open ? "" : password, open)) is null ? "That network name and password are too long for a QR code." : null;
    }

    private void SetQr(string? text)
    {
        _qrText = text;
        _session.Effects?.Qr(text, QrCorner());
        ApplyMarksChange();
        _tray.Refresh();
        if (text is null) return;
        Message($"The QR code shows in the {InsetLayout.Words(QrCorner())} corner of the TV.", BadgeDot.Accent, Button("Take it off", () => SetQr(null), primary: false));
    }

    /// <summary>The chosen corner, unless the small picture is there: then the other top corner.</summary>
    private InsetCorner QrCorner()
    {
        InsetCorner corner = Settings.QrPlace;
        if (_inset is null || InsetLayout.ParseCorner(Settings.InsetCorner) != corner) return corner;
        return corner is InsetCorner.TopRight ? InsetCorner.TopLeft : InsetCorner.TopRight;
    }

    /// <summary>Settings › Appearance › QR code corner.</summary>
    public void SetQrCorner(InsetCorner corner)
    {
        Settings.QrPlace = corner;
        Save();
        if (_qrText is { } text) _session.Effects?.Qr(text, QrCorner());
    }

    // ───────────────────────────── room profiles (FR-82) ─────────────────────────────

    /// <summary>One click: the screen, dimming, extras, the blank screen and favourites for a room.</summary>
    public async Task<bool> ApplyRoomAsync(string name)
    {
        if (RoomProfile.Find(Settings.Rooms, name) is not { } room)
        {
            Message($"There's no room called “{TrayMenuPlan.Shorten(name, 40)}”. Save one from the tray menu under Rooms.", BadgeDot.Warning);
            return false;
        }
        bool cursorBefore = Settings.ShowCursor;
        string? outputBefore = Settings.OutputDevice;
        bool windowBefore = Settings.OutputInWindow;
        room.ApplyTo(Settings);
        Save();
        ApplyLogo();
        if (_session.State != SessionState.Off) await _session.SetDimAsync(Settings.Dim);
        if (_captionOn) _session.Effects?.Caption(Settings.CaptionName, Settings.CaptionSecond);
        ApplyMarksChange();
        // Where it shows: moved now when showing, else from the next thing shown.
        if (_session.State != SessionState.Off)
        {
            if (Settings.OutputInWindow && !windowBefore) await SetOutputWindowAsync();
            else if (!Settings.OutputInWindow && (windowBefore || !string.Equals(outputBefore, Settings.OutputDevice, StringComparison.OrdinalIgnoreCase))
                     && _monitors.FirstOrDefault(m => m.DeviceName == Settings.OutputDevice) is { } screen)
                await SetOutputMonitorAsync(screen, quiet: true);
        }
        if (cursorBefore != Settings.ShowCursor && _session.State == SessionState.Presenting) await ReshowAsync();
        if (_second is { State: not SessionState.Off } second && SecondTvScreen is { } tv2 && second.FullScreenOutput is { } at && !at.IsSameScreen(tv2)) await StopSecondTvAsync();
        _tray.Refresh();
        Message($"Set up for “{room.Name}”.", BadgeDot.Accent);
        return true;
    }

    /// <summary>Saves how things are set up now as a room; asks for its name (and whether to keep the favourites with it).</summary>
    public void SaveRoom()
    {
        using var form = new FieldsForm("Save this setup as a room",
            "A room keeps the TV it uses, dimming, the extras on the TV, the blank screen's message and logo, and your name banner. Choose it later in one click.", "Save the room");
        (_, TextBox name) = form.AddField("Room name", "", RoomProfile.MaxNameLength, "Board room");
        CheckBox favourites = form.AddCheck("Keep my favourites with this room", true);
        form.Check = () =>
        {
            string tidy = RoomProfile.TidyName(name.Text);
            if (tidy.Length == 0) return "Type a name for the room.";
            if (RoomProfile.Find(Settings.Rooms, tidy) is null && Settings.Rooms.Count >= RoomProfile.Max) return $"You can keep {RoomProfile.Max} rooms. Remove one in Settings first.";
            return null;
        };
        if (!form.Ask()) return;
        RoomProfile room = RoomProfile.From(name.Text, Settings, favourites.Checked);
        int existing = Settings.Rooms.FindIndex(r => string.Equals(r.Name, room.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) Settings.Rooms[existing] = room;
        else Settings.Rooms.Add(room);
        Save();
        _tray.Refresh();
        Message(existing >= 0 ? $"“{room.Name}” is updated." : $"Saved “{room.Name}”. Choose it from the tray menu under Rooms.");
    }

    /// <summary>Settings › Rooms: remove one.</summary>
    public void DeleteRoom(string name)
    {
        if (Settings.Rooms.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Settings.Schedule.RemoveAll(e => e.What == ScheduleAction.Room && string.Equals(e.Room, name, StringComparison.OrdinalIgnoreCase));
        Save();
        _tray.Refresh();
        UpdateSchedule();
    }

    // ───────────────────────────── schedules (FR-83) ─────────────────────────────

    private readonly System.Windows.Forms.Timer _scheduleTimer = new() { Interval = (int)Scheduler.CheckEvery.TotalMilliseconds };

    /// <summary>The last check; a start-up catches up on the last few minutes only.</summary>
    private DateTime _scheduleChecked = DateTime.Now - Scheduler.CatchUp;

    /// <summary>The timer runs only while some entry is switched on (NFR-04: no work for nothing).</summary>
    private void UpdateSchedule()
    {
        bool want = !_quitting && Settings.Schedule.Any(e => e.Enabled);
        if (want == _scheduleTimer.Enabled) return;
        if (want)
        {
            _scheduleChecked = DateTime.Now - Scheduler.CatchUp;
            _scheduleTimer.Start();
        }
        else _scheduleTimer.Stop();
    }

    private async Task OnScheduleTickAsync()
    {
        DateTime now = DateTime.Now;
        List<ScheduleEntry> due = Scheduler.Due(Settings.Schedule, _scheduleChecked, now);
        _scheduleChecked = now;
        foreach (ScheduleEntry entry in due) await RunScheduledAsync(entry);
    }

    /// <summary>Does one scheduled thing. Each is harmless to repeat (starting a running slideshow does nothing).</summary>
    internal async Task RunScheduledAsync(ScheduleEntry entry)
    {
        Diag("schedule: " + entry.Words());
        switch (entry.What)
        {
            case ScheduleAction.StartSlideshow:
                if (!SlideshowOn) await ToggleSlideshowAsync();
                break;
            case ScheduleAction.StopSlideshow:
                StopSlideshow("The scheduled slideshow stopped.");
                break;
            case ScheduleAction.ShowFavourite:
                await ShowFavouriteAsync(entry.Slot);
                break;
            case ScheduleAction.Blank:
                if (_session.State == SessionState.Presenting && !_session.IsBlanked) await _session.ToggleBlankAsync(withMessage: true);
                break;
            case ScheduleAction.ShowAgain:
                if (_session.IsBlanked) await _session.ToggleBlankAsync(withMessage: false);
                break;
            case ScheduleAction.Stop:
                StopSlideshow(null);
                if (_session.State != SessionState.Off) await _session.StopAsync();
                break;
            case ScheduleAction.Room:
                if (entry.Room is { } room) await ApplyRoomAsync(room);
                break;
            case ScheduleAction.StartPlaylist:
                if (entry.Playlist is { } list && Playlist.Find(Settings.Playlists, list) is { } playlist && PlayingOn(playlist.Where) != playlist.Name) await StartPlaylistAsync(list);
                break;
            case ScheduleAction.StopPlaylist:
                StopPlaylists("The scheduled playlist stopped.");
                break;
            case ScheduleAction.Scene:
                await ShowSceneAsync(entry.Slot);
                break;
        }
    }

    /// <summary>Settings › Rooms and schedule: add one.</summary>
    public void AddScheduleEntry(ScheduleEntry entry)
    {
        if (Settings.Schedule.Count >= ScheduleEntry.Max) return;
        Settings.Schedule.Add(entry);
        Save();
        UpdateSchedule();
    }

    public void RemoveScheduleEntry(int index)
    {
        if (index < 0 || index >= Settings.Schedule.Count) return;
        Settings.Schedule.RemoveAt(index);
        Save();
        UpdateSchedule();
    }

    public void SetScheduleEntryEnabled(int index, bool on)
    {
        if (index < 0 || index >= Settings.Schedule.Count) return;
        Settings.Schedule[index].Enabled = on;
        Save();
        UpdateSchedule();
    }

    // ───────────────────────────── live captions (FR-84) ─────────────────────────────

    private CaptionsProcess? _captions;
    private readonly LiveCaptions _liveText = new();
    /// <summary>How often captions check for silence (they clear after <see cref="LiveCaptions.Silence"/>).</summary>
    public static readonly TimeSpan CaptionsCheckEvery = TimeSpan.FromSeconds(1);

    private readonly System.Windows.Forms.Timer _captionsTimer = new() { Interval = (int)CaptionsCheckEvery.TotalMilliseconds };
    private bool _captionsReady;

    public bool LiveCaptionsOn => _captions is not null;

    /// <summary>Ctrl+Alt+Shift+T: what you say appears along the bottom of the TV, worked out on this PC; again turns it off.</summary>
    private void ToggleLiveCaptions()
    {
        if (_captions is not null)
        {
            StopLiveCaptions("Live captions are off.");
            return;
        }
        if (_session.State != SessionState.Presenting && !Settings.SaveTranscript)
        {
            Message("Show something first, then turn on live captions. (With “Save a transcript” on in Settings, they can listen without showing anything.)");
            return;
        }
        (CaptionsProcess? process, string? problem) = CaptionsProcess.Start();
        if (process is null)
        {
            Message(problem ?? "Live captions couldn't start.", BadgeDot.Warning);
            return;
        }
        _captions = process;
        _captionsReady = false;
        _liveText.Reset();
        StartTranscript();
        process.Line += (_, line) => Guard(() => OnCaptionLine(process, line));
        process.Ended += (_, _) => Guard(() =>
        {
            if (!ReferenceEquals(_captions, process)) return;
            StopLiveCaptions(_captionsReady ? "Live captions stopped: Windows' speech recognition ended." : null);
        });
        _captionsTimer.Start();
        ApplyMarksChange();
        _tray.Refresh();
        Message("Starting live captions…");
    }

    private void OnCaptionLine(CaptionsProcess process, string line)
    {
        if (!ReferenceEquals(_captions, process)) return;
        (string kind, string text) = LiveCaptions.Split(line);
        switch (kind)
        {
            case "ready":
                _captionsReady = true;
                Message(Settings.CaptionsOnTv
                        ? "Live captions on: what you say shows on the TV. Windows works it out on this PC; nothing is sent anywhere."
                        : "Listening for the transcript: what you say is written down on this PC; nothing is sent anywhere.",
                    BadgeDot.Accent, Button("Turn them off", () => StopLiveCaptions("Live captions are off."), primary: false));
                break;
            case "error":
                StopLiveCaptions(null);
                Message(text.Length > 0 ? text : "Live captions couldn't start.", BadgeDot.Warning);
                break;
            default:
                if (_liveText.Heard(line, DateTime.UtcNow) && Settings.CaptionsOnTv) _session.Effects?.LiveCaption(_liveText.Shown);
                WriteTranscript();
                break;
        }
    }

    private void OnCaptionsTick()
    {
        if (_captions is null) return;
        if (_liveText.Tick(DateTime.UtcNow) && Settings.CaptionsOnTv) _session.Effects?.LiveCaption(_liveText.Shown);
    }

    private void StopLiveCaptions(string? why)
    {
        if (_captions is not { } process) return;
        _captions = null;
        _captionsTimer.Stop();
        _liveText.Reset();
        _session.Effects?.LiveCaption(null);
        process.Dispose();
        FinishTranscript();
        ApplyMarksChange();
        _tray.Refresh();
        if (why is not null) Message(why);
    }

    // ───────────────────────────── a second TV (FR-85) ─────────────────────────────

    private SessionController? _second;
    private SourceOutline? _secondOutline;

    public bool SecondTvOn => _second is { State: not SessionState.Off };
    public SourceWindow? SecondTvSource => _second is { State: SessionState.Presenting } s ? s.Source : null;

    /// <summary>The screen the second TV uses: where it's showing now, else the saved one if it can be used, else the first that can.</summary>
    public DisplayMonitor? SecondTvScreen =>
        _second?.FullScreenOutput is { } now ? now
        : SecondTvCandidates() is { Count: > 0 } candidates ? candidates.FirstOrDefault(m => m.DeviceName == Settings.SecondOutputDevice) ?? candidates[0]
        : null;

    /// <summary>Screens that can be the second TV: not the main TV, not a screen the presenter works on.</summary>
    public IReadOnlyList<DisplayMonitor> SecondTvCandidates()
    {
        DisplayMonitor? main = _session.FullScreenOutput ?? (ChosenPlacement is { IsFloating: false } p ? p.Monitor : null);
        DisplayMonitor? mine = OutputChooser.PresenterScreen(_monitors, main, null);
        return [.. _monitors.Where(m => (main is null || !m.IsSameScreen(main)) && (mine is null || !m.IsSameScreen(mine)) && !m.IsVirtual)];
    }

    /// <summary>True for the second TV's screen (while it shows, or the one about to be used): it can't show itself.</summary>
    public bool IsSecondTvOutput(DisplayMonitor m, bool choosing) =>
        (_second?.FullScreenOutput is { } on && m.IsSameScreen(on)) || choosing && SecondTvScreen is { } chosen && m.IsSameScreen(chosen);

    public void SetSecondTvScreen(DisplayMonitor m)
    {
        Settings.SecondOutputDevice = m.DeviceName;
        Save();
        if (_second is { State: not SessionState.Off } second && second.FullScreenOutput is { } at && !at.IsSameScreen(m))
            _ = GuardAsync(() => second.MoveOutputAsync(m, topMost: true));
    }

    /// <summary>"Second TV": takes it off, or opens the picker to choose what it shows.</summary>
    private async Task ToggleSecondTvAsync()
    {
        if (SecondTvOn)
        {
            await StopSecondTvAsync();
            Message("The second TV is off.");
            return;
        }
        if (SecondTvCandidates().Count == 0)
        {
            Message("Connect a second TV or screen first. The first TV keeps showing what it shows.");
            return;
        }
        OpenPicker(PickerMode.SecondTv);
    }

    /// <summary>Shows <paramref name="source"/> on the second TV, independently of the first.</summary>
    public async Task ShowOnSecondTvAsync(SourceWindow source)
    {
        if (IsNeverShown(source))
        {
            Message($"“{source.AppName}” is on your never-show list, so it isn't shown.", BadgeDot.Warning);
            return;
        }
        if (!await EnsureTvIsSeparateAsync()) return;
        if (SecondTvScreen is not { } screen)
        {
            Message("Connect a second TV or screen first. The first TV keeps showing what it shows.");
            return;
        }
        if (source.Screen is { } shownScreen && (shownScreen.IsSameScreen(screen) || _session.FullScreenOutput is { } tv && shownScreen.IsSameScreen(tv)))
        {
            Message($"{shownScreen.Name} is a TV here, so it can't be shown on a TV.");
            return;
        }
        if (source.IsWindow && !WindowEnumerator.IsAlive(source.Handle))
        {
            Message($"“{TrayMenuPlan.Shorten(source.Title, 40)}” has closed.");
            return;
        }
        if (source.IsWindow && source.Crop is null && Settings.SavedCrops.TryGetValue(source.AppName, out CropRegion? crop)) source = source with { Crop = crop };
        PresenterChose(PlaylistScreen.Second);
        SessionController second = EnsureSecondSession();
        Settings.SecondOutputDevice = screen.DeviceName;
        Save();
        await second.PresentAsync(source, new OutputPlacement(screen, true), onlyIfLive: _playlistSwitching);
        if (second.State == SessionState.Presenting && !_playlistSwitching)
            Message($"The second TV ({screen.Name}) shows “{SourceName(source)}”. The first keeps showing what it shows.", BadgeDot.Accent,
                Button("Stop the second TV", StopSecondTvAsync, primary: false));
    }

    private SessionController EnsureSecondSession()
    {
        if (_second is { } existing) return existing;
        var second = new SessionController(new Win32SessionPlatform(() => _store.Current)) { BlankMessage = Settings.BlankMessage };
        second.HiddenPartsFor = source => Redactions.PartsFor(Settings, source);
        second.HoldCheck = source => NeverShowAppOn(source) is not null ? HoldReason.PrivateApp : null;
        second.SetLogo(Settings.BlankLogo is { } logo && File.Exists(logo) ? logo : null);
        second.StateChanged += (_, _) => Guard(OnSecondChanged);
        second.SourceGeometryChanged += (_, _) => Guard(UpdateSecondOutline);
        second.Message += (_, text) => _badge.ShowMessage("Second TV: " + text);
        _second = second;
        return second;
    }

    private async Task StopSecondTvAsync()
    {
        if (_second is not { } second) return;
        await second.StopAsync();
    }

    private void OnSecondChanged()
    {
        if (_second is { State: SessionState.Off }) StopPlaylist(PlaylistScreen.Second, null);
        UpdateSecondOutline();
        UpdatePrivateAppGuard();
        _studio?.UpdateFrom(this);
        _tray.Refresh();
        ScheduleTrim();
    }

    /// <summary>A yellow outline round what the second TV shows too, labelled so the two can't be confused.</summary>
    private void UpdateSecondOutline()
    {
        Rectangle? bounds = null;
        if (Settings.ShowOutline && _second is { State: SessionState.Presenting, IsBlanked: false, IsSourceMinimized: false, Source: { } source })
        {
            if (source.Screen is { } screen) bounds = source.Crop?.ToScreen(screen.Bounds) ?? screen.Bounds;
            else if (WindowEnumerator.IsAlive(source.Handle))
                bounds = source.Crop is { } crop ? crop.ToScreen(WindowEnumerator.ClientBoundsOnScreen(source.Handle)) : DwmHelpers.GetVisibleBounds((Windows.Win32.Foundation.HWND)source.Handle);
        }
        if (bounds is null && _secondOutline is null) return;
        _secondOutline ??= new SourceOutline();
        _secondOutline.Update(bounds, "Showing on the second TV", _monitors, _second?.FullScreenOutput, _second?.Source is { IsScreen: false } w ? w.Handle : 0);
    }

    /// <summary>
    /// The first TV, a screen change or the settings moved things so the second TV can't stay
    /// where it is (its screen became the first TV's, or went away): it stops.
    /// </summary>
    private async Task CheckSecondTvAsync()
    {
        if (_second is not { State: not SessionState.Off } second) return;
        DisplayMonitor? at = second.FullScreenOutput;
        bool gone = at is null || !_monitors.Any(m => m.IsSameScreen(at));
        bool clash = at is not null && _session.FullScreenOutput is { } main && main.IsSameScreen(at);
        bool showsTv = second.Source?.Screen is { } shown && (_session.FullScreenOutput is { } tv && shown.IsSameScreen(tv) || !_monitors.Any(m => m.IsSameScreen(shown)));
        if (!gone && !clash && !showsTv) return;
        await second.StopAsync();
        Message(gone ? "The second TV was disconnected, so it stopped." : clash ? "The first TV moved onto the second TV's screen, so the second TV stopped."
            : "What the second TV showed isn't available any more, so it stopped.", BadgeDot.Warning);
    }

    /// <summary>A never-show app on the part of a screen <paramref name="source"/> shows; null for a window.</summary>
    private VisibleWindow? NeverShowAppOn(SourceWindow source)
    {
        if (ShownScreenArea(source) is not { } area || Settings.NeverShowApps.Count == 0) return null;
        return NeverShow.FindOnScreen(WindowEnumerator.GetVisibleWindows([]), area, Settings.NeverShowApps);
    }

    /// <summary>The second TV's own never-show check, run with the first's.</summary>
    private void CheckSecondPrivateApps()
    {
        if (_second is not { State: SessionState.Presenting, Source: { } source } second) return;
        if (NeverShowAppOn(source) is { } found)
        {
            if (!second.IsHeldFor(HoldReason.PrivateApp))
            {
                second.CutAway(HoldReason.PrivateApp);
                Message($"“{found.AppName}” is open on {source.Screen?.Name ?? "the screen"}, so the second TV shows black. Minimise it or move it away.", BadgeDot.Warning);
            }
            return;
        }
        if (second.IsHeldFor(HoldReason.PrivateApp)) _ = second.ComeBackAsync(HoldReason.PrivateApp);
    }

    // ───────────────────────────── shared ─────────────────────────────

    /// <summary>The extras that need the helper to draw them on the TV.</summary>
    private bool StageWantsHelper => _captionOn || _qrText is not null || _captions is not null && Settings.CaptionsOnTv || AudienceViewOpen || Settings.AutoHideKinds != SensitiveKinds.None;

    /// <summary>A new engine took over: it starts without the stage extras, so they're sent again.</summary>
    private void ReplayStage(IEffectsEngine effects)
    {
        _sentSpotlight = null;
        if (_captionOn) effects.Caption(Settings.CaptionName, Settings.CaptionSecond);
        if (_qrText is { } qr) effects.Qr(qr, QrCorner());
        if (_captions is not null && Settings.CaptionsOnTv && _liveText.Shown.Length > 0) effects.LiveCaption(_liveText.Shown);
        ReplayExtend(effects);
    }

    /// <summary>Showing stopped: the extras that belong to the picture go with it. Something changed: the windows that follow it update.</summary>
    private void UpdateStage()
    {
        if (_session.State == SessionState.Off)
        {
            if (_spotlightOn) SetSpotlightQuiet(false);
            if (_captionOn) _captionOn = false;
            _qrText = null;
            if (_captions is not null && !Settings.SaveTranscript) StopLiveCaptions(null); // a transcript keeps listening
            StopPlaylist(PlaylistScreen.Main, null);
        }
        PushRemoteStatus();
        _studio?.UpdateFrom(this);
        if (_second is { State: not SessionState.Off }) _ = GuardAsync(CheckSecondTvAsync);
    }

    private void SetSpotlightQuiet(bool on)
    {
        _spotlightOn = on;
        _sentSpotlight = null;
    }

    /// <summary>Starts the stage's timers and puts the logo on (from the constructor's end of start-up).</summary>
    private void StartStage()
    {
        _scheduleTimer.Tick += (_, _) => _ = GuardAsync(OnScheduleTickAsync);
        _captionsTimer.Tick += (_, _) => Guard(OnCaptionsTick);
        _session.HiddenPartsFor = source => Redactions.PartsFor(Settings, source);
        ApplyLogo();
        UpdateSchedule();
        StartExtend();
    }

    private void DisposeStage()
    {
        DisposeExtend();
        _scheduleTimer.Dispose();
        _captionsTimer.Dispose();
        _captions?.Dispose();
        _captions = null;
        _studio?.Dispose();
        _second?.Dispose();
        _secondOutline?.Dispose();
    }
}
