using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// The real capture engine on the real GPU, drawing into a hidden output window: every mark on the
/// TV (FR-64–FR-72) is drawn for a few frames without the engine failing. It captures the main
/// screen for about a second; nothing is saved or shown. One engine per test run, as in the app,
/// where every engine runs in its own short-lived helper process.
/// </summary>
[Collection(GpuEngineCollection.Name)]
public class GpuMarksTests
{
    /// <summary>
    /// A hidden output window on its own thread with a message loop, as in the app: a swap chain
    /// talks to its window's thread, so that thread must keep processing messages.
    /// </summary>
    private sealed class HiddenOutput : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private OutputWindow? _window;

        public HiddenOutput(DisplayMonitor screen, Size size)
        {
            _thread = new Thread(() =>
            {
                _window = new OutputWindow(new OutputPlacement(screen, false, new Rectangle(screen.WorkArea.Location, size)));
                _window.CreateControl(); // never shown
                _ready.Set();
                Application.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(10));
        }

        public nint Handle => _window!.Handle;
        public Size ClientSize => _window!.ClientSize;

        public void Dispose()
        {
            _window?.BeginInvoke(() =>
            {
                _window.Dispose();
                Application.ExitThread();
            });
            _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
        }
    }

    [Fact]
    public async Task Every_mark_draws_on_a_real_GPU()
    {
        if (!Direct3D.IsCaptureSupported) return;
        DisplayMonitor screen = DisplayHelpers.GetMonitors().First(m => m.IsPrimary);
        using var output = new HiddenOutput(screen, new Size(640, 360));
        using var engine = new WgcEngine(showCursor: true);
        var problems = new List<string>();
        engine.Failed += (_, why) => problems.Add("failed: " + why);
        engine.InsetFailed += (_, why) => problems.Add("small picture: " + why);

        await engine.StartAsync(SourceWindow.ForScreen(screen), output.Handle, output.ClientSize).WaitAsync(TimeSpan.FromSeconds(10));
        engine.SetOpacity(1);

        // Clicks, the pointer ring, the laser with its trail, a drawn line and a highlighter line.
        engine.Click(new PointF(0.5f, 0.5f), secondary: false);
        engine.Click(new PointF(0.6f, 0.4f), secondary: true);
        engine.Pointer(new PointF(0.3f, 0.3f));
        for (int i = 0; i < 10; i++) engine.Laser(new PointF(0.2f + i * 0.02f, 0.7f));
        var pen = new InkStroke(1, InkTool.Pen, Color.Red.ToArgb(), 0.005f);
        var marker = new InkStroke(2, InkTool.Highlighter, Color.Yellow.ToArgb(), 0.02f);
        engine.InkStart(pen);
        engine.InkAdd(1, [new PointF(0.1f, 0.1f), new PointF(0.3f, 0.2f), new PointF(0.5f, 0.1f)]);
        engine.InkStart(marker);
        engine.InkAdd(2, [new PointF(0.2f, 0.5f)]); // a single dot
        // The small picture (the same screen, small), a zoomed view, and the countdown's two lines.
        engine.Inset(SourceWindow.ForScreen(screen), InsetCorner.TopLeft, 28);
        await Task.Delay(700);
        engine.SetCrop(new CropRegion(0.25, 0.25, 0.5, 0.5));
        engine.SetIdleMessage("We'll be right back" + CaptureProtocol.LineSeparator + "Back in 4:59", 1);
        engine.ShowCursor(false);
        await Task.Delay(400);

        // The stage tools (FR-74–FR-84): hidden parts (solid, then blurred), the spotlight, an arrow,
        // a box and text, the name banner, a QR code, live captions, and a logo over the message.
        string logo = Path.Combine(Path.GetTempPath(), $"scp-gpu-logo-{Guid.NewGuid():N}.png");
        using (var picture = new Bitmap(240, 80))
        {
            using (Graphics g = Graphics.FromImage(picture)) g.Clear(Color.SteelBlue);
            picture.Save(logo, System.Drawing.Imaging.ImageFormat.Png);
        }
        try
        {
            engine.Redact([CropRegion.Create(0.1, 0.1, 0.3, 0.3)!, CropRegion.Create(0.6, 0.6, 0.2, 0.2)!], RedactStyle.Solid);
            engine.Spotlight(new PointF(0.5f, 0.5f));
            engine.InkStart(new InkStroke(3, InkTool.Arrow, Color.Red.ToArgb(), 0.006f));
            engine.InkAdd(3, [new PointF(0.2f, 0.8f)]);
            engine.InkSet(3, [new PointF(0.2f, 0.8f), new PointF(0.4f, 0.6f)]);
            engine.InkStart(new InkStroke(4, InkTool.Box, Color.Yellow.ToArgb(), 0.006f));
            engine.InkAdd(4, [new PointF(0.5f, 0.2f), new PointF(0.7f, 0.4f)]);
            engine.InkStart(new InkStroke(5, InkTool.Text, Color.White.ToArgb(), 0.045f) { Text = "Look here" });
            engine.InkAdd(5, [new PointF(0.3f, 0.3f)]);
            engine.Caption("Jane Doe", "Head of Sales");
            engine.Qr("https://example.com/agenda", InsetCorner.TopRight);
            engine.LiveCaption("and that is why the numbers went up");
            engine.Logo(logo);
            await Task.Delay(400);
            engine.Redact([CropRegion.Create(0.1, 0.1, 0.3, 0.3)!], RedactStyle.Blurred);
            engine.SetFrozen(true); // hidden parts stay while frozen; the spotlight doesn't follow
            await Task.Delay(300);
            engine.SetFrozen(false);
            engine.Logo(Path.Combine(Path.GetTempPath(), "no-such-logo.png")); // a missing file is just no logo
            await Task.Delay(200);
            engine.Redact([], RedactStyle.Solid);
            engine.Spotlight(null);
            engine.Caption(null, null);
            engine.Qr(null, InsetCorner.TopRight);
            engine.LiveCaption(null);
            engine.Logo(null);
            // Running the room (FR-88, FR-89): automatic hiding reads the real screen on the GPU and
            // the text recognizer; bigger captions at the top. Then off again.
            engine.CaptionStyle(new CaptionLook(CaptionSize.ExtraLarge, CaptionPlace.Top));
            engine.LiveCaption("captions at the top, extra large");
            engine.AutoHide(SensitiveKinds.All, RedactStyle.Blurred);
            await Task.Delay(1500);
            engine.SetFrozen(true); // no scans while frozen
            await Task.Delay(300);
            engine.SetFrozen(false);
            engine.AutoHide(SensitiveKinds.None, RedactStyle.Solid);
            engine.LiveCaption(null);
            await Task.Delay(300);
        }
        finally
        {
            File.Delete(logo); // read into memory: never locked
        }
        Assert.Empty(problems);

        // Freezing holds both pictures; taking things away again.
        engine.SetFrozen(true);
        engine.Click(new PointF(0.5f, 0.5f), secondary: false); // not drawn while frozen, but accepted
        await Task.Delay(200);
        engine.SetFrozen(false);
        engine.InkRemove(1);
        engine.InkClear();
        engine.Laser(null);
        engine.Pointer(null);
        engine.Inset(null, InsetCorner.BottomRight, 28);
        engine.SetIdleMessage(null, 0);
        await Task.Delay(300);
        Assert.Empty(problems);

        // Split screen (FR-104): another window beside the screen, with its hidden parts, and the new
        // kinds of automatic hiding (password boxes, faces) on every picture; every layout, a
        // second pane, the small picture with its covers, then back to one picture.
        using (var beside = new PasswordForm())
        {
            SourceWindow other = beside.Source;
            engine.HiddenFor(other, [CropRegion.Create(0.1, 0.1, 0.3, 0.3)!], RedactStyle.Blurred);
            engine.AutoHide(SensitiveKinds.PasswordFields | SensitiveKinds.Faces | SensitiveKinds.Emails, RedactStyle.Solid);
            engine.Split(SplitLayout.SideBySide, [other]);
            for (int i = 0; i < 30 && engine.PanesShown != 1; i++) await Task.Delay(100);
            Assert.Equal(1, engine.PanesShown);
            foreach (SplitLayout layout in SplitScreen.All)
            {
                engine.Split(layout, [other, SourceWindow.ForScreen(screen)]);
                await Task.Delay(300);
            }
            Assert.Equal(2, engine.PanesShown);
            // A picture file beside it (FR-103), and a shortcut on the TV (FR-101).
            string card = TextCard.Render(Path.Combine(Path.GetTempPath(), "scp-gpu-cards"), "Welcome" + Environment.NewLine + "To the test")!;
            engine.Split(SplitLayout.Focus, [other, SourceWindow.ForPicture(card)]);
            engine.KeyCombo("Ctrl + C");
            await Task.Delay(500);
            Assert.Equal(2, engine.PanesShown);
            engine.KeyCombo(null);
            engine.Split(SplitLayout.Grid, [SourceWindow.ForPicture(Path.Combine(Path.GetTempPath(), "no-such-picture.png"))]); // can't be read: says so
            for (int i = 0; i < 30 && problems.Count == 0; i++) await Task.Delay(100);
            Assert.Contains("can't be shown", Assert.Single(problems));
            problems.Clear();
            engine.Split(SplitLayout.SideBySide, [other, SourceWindow.ForScreen(screen)]);
            await Task.Delay(300);
            engine.SetFrozen(true); // panes hold still too
            await Task.Delay(200);
            engine.SetFrozen(false);
            engine.Split(SplitLayout.Grid, []);
            engine.Inset(other, InsetCorner.BottomLeft, 28);
            await Task.Delay(800);
            Assert.Equal(0, engine.PanesShown);
            engine.Inset(null, InsetCorner.BottomLeft, 28);
            engine.HiddenFor(other, [], RedactStyle.Solid);
            engine.AutoHide(SensitiveKinds.None, RedactStyle.Solid);
            await Task.Delay(300);
        }
        Assert.Empty(problems);

        // A small picture that can't start (its window is gone) says so; the main picture carries on.
        engine.Inset(TestData.Window(unchecked((nint)0x7FFF_FFF0), "Gone"), InsetCorner.BottomRight, 28);
        for (int i = 0; i < 30 && problems.Count == 0; i++) await Task.Delay(100);
        Assert.Equal("small picture: ", Assert.Single(problems)[..15]);
        await Task.Delay(300);
        Assert.Single(problems); // and the engine didn't fail
    }
}
