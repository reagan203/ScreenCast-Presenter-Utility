using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Remote;
using ScreenCastPresenter.UI;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-16: when what's shown closes, the TV can show the message and logo instead of plain black.</summary>
public class IdleScreenTests
{
    [Fact]
    public async Task The_idle_screen_shows_the_message_when_asked_and_stays_black_otherwise()
    {
        foreach (string? text in new[] { "We'll be right back", null })
        {
            var platform = new FakePlatform();
            var session = new SessionController(platform) { IdleText = () => text };
            await session.PresentAsync(TestData.Window(1, "Slides"), TestData.Tv, true);
            platform.Watchers[^1].RaiseClosed();
            await Task.Delay(50);
            Assert.Equal(SessionState.IdleScreen, session.State);
            Assert.Equal(text, platform.Outputs[^1].Message);
            if (text is not null) Assert.Equal(1, platform.Outputs[^1].MessageBrightness, 3); // faded all the way in
            await session.PresentAsync(TestData.Window(2, "Agenda"), TestData.Tv, true);
            Assert.Null(platform.Outputs[^1].Message); // and gone again when something is shown
        }
    }

    [Fact]
    public void It_is_off_unless_chosen_and_kept() =>
        Assert.False(new AppSettings().IdleShowsMessage);
}

/// <summary>FR-59: a recording can stop at a set time.</summary>
public class RecordingStopAtTests
{
    private static readonly DateTime Nine = new(2025, 3, 3, 9, 0, 0);

    [Fact]
    public void The_next_time_is_later_today_or_tomorrow()
    {
        Assert.Equal(Nine.Date.AddHours(17.5), RecordingStop.Next(TimeSpan.FromHours(17.5), Nine));
        Assert.Equal(Nine.Date.AddDays(1).AddHours(8), RecordingStop.Next(TimeSpan.FromHours(8), Nine));
        Assert.Equal(Nine.Date.AddDays(1).AddHours(9), RecordingStop.Next(TimeSpan.FromHours(9), Nine)); // now isn't "later"
        Assert.Equal("at 17:30", RecordingStop.Words(Nine.Date.AddHours(17.5), Nine));
        Assert.Equal("at 08:00 tomorrow", RecordingStop.Words(Nine.Date.AddDays(1).AddHours(8), Nine));
    }

    [Fact]
    public void The_tray_offers_it_and_shows_when_its_set()
    {
        var recording = new TrayMenuState { Session = SessionState.Off, Status = "Recording", Recording = Recording.RecordingStatus.Recording };
        List<MenuEntry> menu = [.. TrayMenuPlan.Flatten(TrayMenuPlan.Build(recording))];
        MenuEntry at = menu.Single(e => e.Command == "stop-at");
        Assert.Equal("At a set time…", at.Text);
        Assert.False(at.Checked);
        Assert.True(menu.Single(e => e.Command == "stop-after:never").Checked);
        List<MenuEntry> set = [.. TrayMenuPlan.Flatten(TrayMenuPlan.Build(recording with { StopAt = Nine.Date.AddHours(17) }))];
        Assert.Equal("At 17:00 (change…)", set.Single(e => e.Command == "stop-at").Text);
        Assert.True(set.Single(e => e.Command == "stop-at").Checked);
        Assert.False(set.Single(e => e.Command == "stop-after:never").Checked);
        Assert.True(TrayMenuPlan.IsKnownCommand("stop-at"));
        Assert.NotNull(Icons.ForCommand("stop-at"));
    }

    [Fact]
    public void A_schedule_can_stop_a_recording()
    {
        Assert.Equal("stop the recording", new ScheduleEntry { Action = "StopRecording" }.WhatWords());
        Assert.Contains(ScheduleChoices.Actions(new Favourite?[AppSettings.SlotCount], []), a => a.Action == ScheduleAction.StopRecording);
    }
}

/// <summary>FR-51: the screenshot's preview.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class ScreenshotPreviewTests
{
    [Fact]
    public void The_thumbnail_is_small_and_keeps_the_shape()
    {
        using var big = new Bitmap(3840, 2160);
        using Bitmap thumb = ScreenshotPreview.Thumbnail(big, 280);
        Assert.Equal(new Size(280, 158), thumb.Size);
        using var small = new Bitmap(100, 50);
        using Bitmap same = ScreenshotPreview.Thumbnail(small, 280);
        Assert.Equal(new Size(100, 50), same.Size); // never blown up
    }

    [Fact]
    public void The_preview_never_takes_the_focus_is_hidden_from_capture_and_its_buttons_work()
    {
        var done = new List<string>();
        using var preview = new ScreenshotPreview(new Bitmap(200, 120), "Screenshot saved and copied", "Screenshot 2025-03-03 090000.png",
            () => done.Add("open"), () => done.Add("copy"), () => done.Add("folder"));
        preview.ShowAt(Screen.PrimaryScreen!.WorkingArea); // shown without taking the focus, as after a screenshot
        var ex = (int)Windows.Win32.PInvoke.GetWindowLong((HWND)preview.Handle, Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        Assert.True((ex & 0x08000000) != 0, "WS_EX_NOACTIVATE: the presenter keeps typing where they were");
        Assert.True((ex & 0x00000080) != 0, "not in Alt+Tab");
        if (Environment.GetEnvironmentVariable("SCP_DEV_ALLOW_CAPTURE") != "1")
            Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)preview.Handle), "never in a meeting or the TV");
        List<PillButton> buttons = [.. All(preview).OfType<PillButton>()];
        Assert.Equal(["Open", "Copy", "Show in folder"], buttons.Select(b => b.Text));
        buttons[1].PerformClick();
        Assert.Equal(["copy"], done);
        Assert.True(preview.IsDisposed || !preview.Visible, "a button closes it");
    }

    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(child => All(child).Prepend(child));
}

/// <summary>FR-88 in still pictures: a screenshot covers what automatic hiding finds.</summary>
public class AutoHideInStillsTests
{
    private static Bitmap Page(int width, int height, out Rectangle emailAt, out Rectangle titleAt)
    {
        var page = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using Graphics g = Graphics.FromImage(page);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float scale = width / 900f;
        using var font = new Font("Segoe UI", 28 * scale, GraphicsUnit.Pixel);
        g.DrawString("Quarterly results", font, Brushes.Black, 20 * scale, 20 * scale);
        titleAt = Rectangle.Round(new RectangleF(new PointF(20 * scale, 20 * scale), g.MeasureString("Quarterly results", font)));
        SizeF before = g.MeasureString("Contact: ", font);
        g.DrawString("Contact: jane.doe@example.com", font, Brushes.Black, 20 * scale, 110 * scale);
        SizeF email = g.MeasureString("jane.doe@example.com", font);
        emailAt = Rectangle.Round(new RectangleF(20 * scale + before.Width, 110 * scale, email.Width, email.Height));
        return page;
    }

    private static bool IsRedactFill(Color c) => Math.Abs(c.R - EffectStyle.RedactFill.R) <= 2 && Math.Abs(c.G - EffectStyle.RedactFill.G) <= 2 && Math.Abs(c.B - EffectStyle.RedactFill.B) <= 2;

    [Theory]
    [InlineData(900, 220)]    // a window
    [InlineData(5200, 1280)]  // bigger than the recognizer takes: scanned smaller, covered at full size
    public void A_screenshot_covers_the_address_and_leaves_the_rest(int width, int height)
    {
        using Bitmap page = Page(width, height, out Rectangle emailAt, out Rectangle titleAt);
        int covered = AutoRedactor.CoverInStill(page, SensitiveKinds.All);
        if (covered == 0 && Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null) return; // no recognizer here
        Assert.Equal(1, covered);
        Assert.True(IsRedactFill(page.GetPixel(emailAt.X + emailAt.Width / 2, emailAt.Y + emailAt.Height / 2)), "the address is covered");
        Assert.False(IsRedactFill(page.GetPixel(titleAt.X + 2, titleAt.Y + titleAt.Height / 2)), "the title isn't");
    }

    [Fact]
    public void With_automatic_hiding_off_nothing_changes()
    {
        using Bitmap page = Page(900, 220, out Rectangle emailAt, out _);
        Color before = page.GetPixel(emailAt.X + 3, emailAt.Y + emailAt.Height / 2);
        Assert.Equal(0, AutoRedactor.CoverInStill(page, SensitiveKinds.None));
        Assert.Equal(before, page.GetPixel(emailAt.X + 3, emailAt.Y + emailAt.Height / 2));
    }

    [Fact]
    public void A_recording_is_told_what_to_hide_automatically()
    {
        var request = new Recording.RecordingRequest(TestData.Window(5, "Inbox"), false, Recording.MeetingSound.None, false, @"C:\My Videos\a b.mp4",
            [CropRegion.Create(0.1, 0.1, 0.2, 0.2)!], SensitiveKinds.Emails | SensitiveKinds.Cards);
        Recording.RecordingRequest back = Recording.RecordingProtocol.ParseStart(Recording.RecordingProtocol.Start(request))!;
        Assert.Equal(SensitiveKinds.Emails | SensitiveKinds.Cards, back.AutoHide);
        Assert.Equal(@"C:\My Videos\a b.mp4", back.Path);
        Assert.Single(back.Hidden!);
        Assert.Equal(SensitiveKinds.None, Recording.RecordingProtocol.ParseStart(Recording.RecordingProtocol.Start(request with { AutoHide = SensitiveKinds.None }))!.AutoHide);
    }
}

/// <summary>FR-90: the pairing window shows the code but never the key as text; a phone can stop a playlist.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class RemotePairingTests
{
    [Fact]
    public void The_address_shown_as_text_has_no_key_and_the_window_is_hidden_from_capture()
    {
        using var form = new RemotePairingForm();
        form.CreateControl();
        form.Show("http://192.168.1.20:47820/#k=SECRETKEY_abc", "Waiting for your phone…");
        TextBox address = All(form).OfType<TextBox>().Single();
        Assert.Equal("http://192.168.1.20:47820/", address.Text);
        Assert.DoesNotContain(All(form), c => c.Text.Contains("SECRETKEY", StringComparison.Ordinal));
        if (Environment.GetEnvironmentVariable("SCP_DEV_ALLOW_CAPTURE") != "1")
            Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)form.Handle), "the pairing code never reaches the TV or a meeting");
        form.Show(null, "This PC isn't connected to a network your phone can join.");
        Assert.Equal("", address.Text);
    }

    [Fact]
    public void Stopping_a_playlist_is_allowed_from_the_phone() => Assert.True(RemoteCommands.IsAllowed("stop-playlist"));

    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(child => All(child).Prepend(child));
}

/// <summary>
/// FR-90 end to end, as a phone does it: the page's own script (taken from the page the helper
/// serves) runs in Node with a small stand-in for the browser, against the real remote helper
/// process. It pairs from the #fragment, asks for what's on, presses buttons, recovers from a
/// stale counter, and says so when the key is wrong.
/// </summary>
[Collection(SpeedCollection.Name)]
public class PhoneSimulationTests
{
    private const string Shim = """
        const [,, base, keyText, clockText] = process.argv;
        class El {
          constructor(id) { this.id = id; this._text = ""; this.className = ""; this.hidden = false; this.children = []; this.listeners = {}; this.dataset = {}; }
          get textContent() { return this._text; }
          set textContent(v) { this._text = v; this.children = []; }
          appendChild(c) { this.children.push(c); }
          get childElementCount() { return this.children.length; }
          addEventListener(type, f) { (this.listeners[type] ??= []).push(f); }
          click() { for (const f of this.listeners.click || []) f(); }
        }
        const byId = {};
        const actions = ["previous-slide", "next-slide", "blank", "freeze", "spotlight", "go", "previous-source", "stop", "stop-playlist", "zoom-in", "caption", "live-captions", "blank-countdown"];
        const buttons = actions.map(a => { const b = new El(); b.dataset.a = a; return b; });
        globalThis.document = { hidden: false, getElementById: id => byId[id] ??= new El(id), querySelectorAll: () => buttons, createElement: () => new El() };
        globalThis.location = { hash: keyText === "-" ? "" : "#k=" + keyText, pathname: "/" };
        globalThis.history = { replaceState() { globalThis.location.hash = ""; } };
        const store = new Map();
        globalThis.localStorage = { getItem: k => store.has(k) ? store.get(k) : null, setItem: (k, v) => store.set(k, v) };
        globalThis.navigator = {};
        const realFetch = globalThis.fetch;
        globalThis.fetch = (url, options) => realFetch(base + url, options);
        globalThis.setInterval = () => 0;
        if (clockText && clockText !== "-") { const fixed = Number(clockText); Date.now = () => fixed; }
        """;

    private const string Driver = """

        const press = a => {
          if (a.startsWith("list:")) { const [, id, n] = a.split(":"); document.getElementById(id).children[Number(n) - 1].click(); return; }
          buttons.find(b => b.dataset.a === a).click();
        };
        const settle = () => new Promise(r => setTimeout(r, 400));
        (async () => {
          await settle(); // the first "what's on"
          for (const a of (process.argv[5] || "").split(",").filter(x => x)) { press(a); await settle(); }
          const $ = id => document.getElementById(id);
          console.log("STATUS " + $("status").textContent);
          console.log("PROBLEM " + ($("status").className === "problem"));
          console.log("FAVOURITES " + $("favourites").children.map(c => c.textContent).join("|"));
          console.log("PLAYLIST " + ($("playlist-section").hidden ? "-" : $("playlist-name").textContent));
          console.log("PLAYLISTS " + $("playlists").children.map(c => c.textContent).join("|"));
          console.log("NOTES " + ($("notes-section").hidden ? "-" : $("notes").textContent.replace(/\n/g, "/")));
          console.log("KEY-IN-ADDRESS " + location.hash.includes("k="));
        })();
        """;

    [Fact]
    public async Task A_phone_pairs_presses_buttons_and_recovers_like_the_real_page_would()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "ScreenCastPresenter.exe");
        if (PhoneRemoteHttpTests.FindNode() is not { } node || !File.Exists(exe)) return;
        string html = RemotePage.Html;
        string script = html[(html.IndexOf("<script>", StringComparison.Ordinal) + 8)..html.IndexOf("</script>", StringComparison.Ordinal)];
        string file = Path.Combine(Path.GetTempPath(), $"scp-phone-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, Shim + "\n" + script + "\n" + Driver);

        byte[] key = RemoteAuth.NewKey();
        var commands = new List<string>();
        var info = new ProcessStartInfo(exe, RemoteHost.Switch) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using Process helper = Process.Start(info)!;
        try
        {
            helper.StandardInput.WriteLine($"start {RemoteAuth.Base64Url(key)} 0 1");
            helper.StandardInput.Flush();
            string listening = (await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
            string port = listening.Split(' ')[1];
            // The app's side: each command it's told about is answered with what's on.
            Task answering = Task.Run(async () =>
            {
                while (await helper.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (!line.StartsWith("cmd ", StringComparison.Ordinal)) continue;
                    lock (commands) commands.Add(line[4..]);
                    var status = new JsonObject
                    {
                        ["status"] = "Showing on TV: Slides",
                        ["favourites"] = new JsonArray("Deck", null, "Budget"),
                        ["scenes"] = new JsonArray(),
                        ["playlist"] = line == "cmd stop-playlist" ? null : "Lobby",
                        ["playlists"] = new JsonArray("Lobby", "Waiting room"),
                    };
                    (string n, string c, string t) = NotesCipher.Encrypt(key, "Start with Q3\nThen the demo");
                    status["notes"] = new JsonObject { ["n"] = n, ["c"] = c, ["t"] = t };
                    if (line == "cmd caption") status["note"] = "Type the name for the banner on the PC first.";
                    helper.StandardInput.WriteLine("status " + status.ToJsonString());
                    helper.StandardInput.Flush();
                }
            });

            async Task<Dictionary<string, string>> Phone(string keyText, string clock, string presses)
            {
                var run = new ProcessStartInfo(node) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (string a in new[] { file, $"http://127.0.0.1:{port}", keyText, clock, presses }) run.ArgumentList.Add(a);
                using Process p = Process.Start(run)!;
                string output = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
                await p.WaitForExitAsync();
                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split(' ', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            }

            // Pairs from the fragment, asks what's on, presses Next and "Stop the playlist".
            Dictionary<string, string> first = await Phone(RemoteAuth.Base64Url(key), "-", "next-slide,stop-playlist");
            Assert.Equal("Showing on TV: Slides", first["STATUS"]);
            Assert.Equal("false", first["PROBLEM"]);
            Assert.Equal("1  Deck|3  Budget", first["FAVOURITES"]);
            Assert.Equal("-", first["PLAYLIST"]); // gone after stopping it
            Assert.Equal("false", first["KEY-IN-ADDRESS"]); // the key left the address bar
            lock (commands) Assert.Equal(["status", "next-slide", "stop-playlist"], commands);

            // A phone whose clock is behind: refused as old, catches up, and the press still works.
            Dictionary<string, string> slow = await Phone(RemoteAuth.Base64Url(key), "1000000000000", "blank");
            Assert.Equal("false", slow["PROBLEM"]);
            lock (commands) Assert.Equal("blank", commands[^1]);

            // The newer buttons: zoom, a playlist from the list, and the name banner, which the PC answers with a note.
            Dictionary<string, string> more = await Phone(RemoteAuth.Base64Url(key), "-", "zoom-in,list:playlists:2,blank-countdown,caption");
            Assert.Equal("1  Lobby|2  Waiting room", more["PLAYLISTS"]);
            Assert.Equal("Start with Q3/Then the demo", more["NOTES"]); // decrypted on the phone with its key
            Assert.Equal("Type the name for the banner on the PC first.", more["STATUS"]);
            Assert.Equal("true", more["PROBLEM"]);
            lock (commands) Assert.Equal(["zoom-in", "playlist-2", "blank-countdown", "caption"], commands[^4..]);

            // A phone with the wrong key: told to pair again; nothing runs.
            int before;
            lock (commands) before = commands.Count;
            Dictionary<string, string> stranger = await Phone(RemoteAuth.Base64Url(RemoteAuth.NewKey()), "-", "stop");
            Assert.Equal("true", stranger["PROBLEM"]);
            Assert.Contains("isn't paired", stranger["STATUS"]);
            Assert.True(!stranger.TryGetValue("NOTES", out string? strangerNotes) || strangerNotes == "-", "another key: no notes shown"); // nothing to read
            lock (commands) Assert.Equal(before, commands.Count);

            // No key at all: says how to pair.
            Dictionary<string, string> none = await Phone("-", "-", "");
            Assert.Contains("Scan the code", none["STATUS"]);

            helper.StandardInput.Close();
            Assert.True(helper.WaitForExit(5000));
            Assert.Equal(0, helper.ExitCode); // stopped cleanly, not crashed
            await answering.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!helper.HasExited) helper.Kill();
            File.Delete(file);
        }
    }
}
