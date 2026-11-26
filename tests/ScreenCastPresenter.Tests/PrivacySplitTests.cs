using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>A real window with a name box and a password box, on its own UI thread (UI Automation asks that thread).</summary>
internal sealed class PasswordForm : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Form? _form;

    public PasswordForm(bool withPassword = true)
    {
        _thread = new Thread(() =>
        {
            _form = new Form
            {
                Text = "SCP password test " + Guid.NewGuid().ToString("N")[..6],
                StartPosition = FormStartPosition.Manual,
                Location = new Point(60, 60),
                ClientSize = new Size(420, 200),
                ShowInTaskbar = false,
                BackColor = Color.White,
            };
            _form.Controls.Add(new TextBox { Location = new Point(20, 20), Width = 300, Text = "jane" });
            if (withPassword)
            {
                Password = new TextBox { Location = new Point(20, 80), Width = 300, UseSystemPasswordChar = true, Text = "hunter22" };
                _form.Controls.Add(Password);
            }
            _form.Shown += (_, _) => _ready.Set();
            Application.Run(_form);
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)), "the window opened");
        Thread.Sleep(200);
    }

    public TextBox? Password { get; private set; }

    public nint Handle => (nint)_form!.Invoke(() => _form.Handle);

    public Rectangle PasswordOnScreen => (Rectangle)_form!.Invoke(() => Password!.RectangleToScreen(Password.ClientRectangle));

    public void Run(Action action) => _form!.Invoke(action);

    public SourceWindow Source => new(Handle, _form!.Invoke(() => _form.Text), "testhost", false);

    public void Dispose()
    {
        _form?.BeginInvoke(() => _form.Close());
        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }
}

/// <summary>FR-98: password boxes found exactly by Windows, covered where they are.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class PasswordBoxTests
{
    [Fact]
    public void Boxes_become_parts_of_what_is_shown_grown_a_little_and_clipped()
    {
        var client = new Rectangle(100, 100, 400, 200);
        List<CropRegion> regions = PasswordFieldFinder.ToRegions([new Rectangle(150, 150, 100, 20), new Rectangle(480, 290, 50, 50), new Rectangle(0, 0, 10, 10)], client, 4);
        Assert.Equal(2, regions.Count); // the one outside is left out
        CropRegion first = regions[0];
        Assert.Equal((150 - 4 - 100) / 400.0, first.X, 6);
        Assert.Equal((100 + 8) / 400.0, first.Width, 6);
        CropRegion clipped = regions[1];
        Assert.True(clipped.X + clipped.Width <= 1.0000001 && clipped.Y + clipped.Height <= 1.0000001);
        Assert.Empty(PasswordFieldFinder.ToRegions([new Rectangle(1, 1, 5, 5)], Rectangle.Empty, 4));
        Assert.Equal(UiAutomation.MaxBoxes, PasswordFieldFinder.ToRegions(Enumerable.Range(0, 100).Select(i => new Rectangle(110, 110 + i, 5, 1)), client, 0).Count);
    }

    [Fact]
    public void Windows_says_where_the_password_box_is_and_only_that_one()
    {
        using var form = new PasswordForm();
        using UiAutomation? uia = UiAutomation.Create();
        Assert.NotNull(uia);
        Rectangle box = Assert.Single(uia.PasswordBoxes(form.Handle));
        Rectangle expected = form.PasswordOnScreen;
        Assert.True(Math.Abs(box.X - expected.X) <= 4 && Math.Abs(box.Y - expected.Y) <= 4 && Math.Abs(box.Width - expected.Width) <= 8,
            $"Windows put it at {box}; it's at {expected}");
        CropRegion region = Assert.Single(PasswordFieldFinder.Find(uia, form.Source));
        Assert.InRange(region.Y, 0.3, 0.45); // 80 of 200 pixels down, less the padding
    }

    [Fact]
    public void A_window_without_password_boxes_has_none_and_nonsense_is_safe()
    {
        using var form = new PasswordForm(withPassword: false);
        using UiAutomation uia = UiAutomation.Create()!;
        Assert.Empty(uia.PasswordBoxes(form.Handle));
        Assert.Empty(uia.PasswordBoxes(0));
        Assert.Empty(uia.PasswordBoxes(unchecked((nint)0x7FFF0001))); // no such window
        Assert.Empty(PasswordFieldFinder.FindOnce(SourceWindow.ForCamera(new CameraDevice("cam", "Camera"))));
    }

    [Fact]
    public void The_finder_follows_the_box_as_it_appears_and_goes()
    {
        using var form = new PasswordForm();
        int changes = 0;
        using var finder = new PasswordFieldFinder(form.Source, () => Interlocked.Increment(ref changes));
        Assert.True(SpinWait.SpinUntil(() => finder.FirstScanDone && finder.Regions.Count == 1, TimeSpan.FromSeconds(5)), "found");
        form.Run(() => form.Password!.Visible = false);
        Assert.True(SpinWait.SpinUntil(() => finder.Regions.Count == 0, TimeSpan.FromSeconds(5)), "gone when hidden");
        form.Run(() =>
        {
            form.Password!.Visible = true;
            form.Password.Top = 140;
        });
        Assert.True(SpinWait.SpinUntil(() => finder.Regions is [{ Y: > 0.5 }], TimeSpan.FromSeconds(5)), "followed where it moved");
        Assert.True(changes >= 3);
    }

    [Fact]
    public void Asking_often_is_quick_and_leaves_no_memory_behind()
    {
        using var form = new PasswordForm();
        using UiAutomation uia = UiAutomation.Create()!;
        for (int i = 0; i < 50; i++) uia.PasswordBoxes(form.Handle); // warm up
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = Process.GetCurrentProcess().PrivateMemorySize64;
        var clock = Stopwatch.StartNew();
        const int asks = 600;
        for (int i = 0; i < asks; i++) Assert.Single(uia.PasswordBoxes(form.Handle));
        double each = clock.Elapsed.TotalMilliseconds / asks;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long grew = Process.GetCurrentProcess().PrivateMemorySize64 - before;
        Assert.True(each < 25, $"one look took {each:F2} ms (budget 25 ms)");
        Assert.True(grew < 4 * 1024 * 1024, $"{asks} looks grew memory by {grew / 1024} KB");
    }

    [Fact]
    public async Task A_screenshot_covers_the_password_box_where_windows_says_it_is()
    {
        if (!Direct3D.IsCaptureSupported) return;
        using var form = new PasswordForm();
        using Bitmap? shot = await StillCapture.CaptureAsync(form.Source, false, TimeSpan.FromSeconds(3), null, SensitiveKinds.PasswordFields);
        Assert.NotNull(shot);
        Rectangle client = WindowEnumerator.ClientBoundsOnScreen(form.Handle);
        Rectangle box = form.PasswordOnScreen;
        Color middle = shot.GetPixel(box.X - client.X + box.Width / 2, box.Y - client.Y + box.Height / 2);
        Assert.True(Math.Abs(middle.R - EffectStyle.RedactFill.R) <= 2 && Math.Abs(middle.G - EffectStyle.RedactFill.G) <= 2, $"covered: {middle}");
        Color clear = shot.GetPixel(10, client.Height - 10);
        Assert.True(clear.R > 200 && clear.G > 200, $"the rest isn't: {clear}");
    }
}

/// <summary>FR-100: faces blurred, found by Windows' own face detection.</summary>
public class FaceBlurTests
{
    private static string Painting => Path.Combine(AppContext.BaseDirectory, "Assets", "face-painting.jpg");

    private static (byte[] Bgra, int W, int H) Pixels(Bitmap image)
    {
        using var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(copy)) g.DrawImage(image, 0, 0, image.Width, image.Height);
        BitmapData data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var bgra = new byte[copy.Width * copy.Height * 4];
        for (int y = 0; y < copy.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * copy.Width * 4, copy.Width * 4);
        copy.UnlockBits(data);
        return (bgra, copy.Width, copy.Height);
    }

    [Fact]
    public void Boxes_grow_round_each_face_and_the_largest_can_stay_clear()
    {
        RectangleF[] faces = [new(100, 100, 40, 40), new(300, 50, 100, 100), new(10, 10, 20, 20)];
        var size = new SizeF(500, 400);
        List<CropRegion> all = FaceFinder.ToRegions(faces, size, keepLargest: false, 0.3f);
        Assert.Equal(3, all.Count);
        CropRegion first = all[0];
        Assert.Equal((100 - 12) / 500.0, first.X, 5);
        Assert.Equal((100 - 18) / 400.0, first.Y, 5); // half again above, for hair
        Assert.Equal((40 + 24) / 500.0, first.Width, 5);
        List<CropRegion> mine = FaceFinder.ToRegions(faces, size, keepLargest: true, 0.3f);
        Assert.Equal(2, mine.Count);
        Assert.DoesNotContain(mine, r => r.X > 0.5); // the large one at x 300 stays clear
        Assert.Empty(FaceFinder.ToRegions([new RectangleF(1, 1, 10, 10)], size, keepLargest: true, 0.3f)); // only me: nothing to blur
        Assert.Empty(FaceFinder.ToRegions(faces, SizeF.Empty, false, 0.3f));
        Assert.Equal(FaceFinder.MaxFaces, FaceFinder.ToRegions([.. Enumerable.Range(0, 60).Select(i => new RectangleF(i * 5, 5, 4, 4))], size, false, 0).Count);
    }

    [Fact]
    public async Task Windows_finds_the_face_in_a_painting()
    {
        if (!FaceFinder.IsSupported) return;
        using var image = new Bitmap(Painting);
        (byte[] bgra, int w, int h) = Pixels(image);
        List<RectangleF>? faces = await FaceFinder.DetectAsync(bgra, w, h);
        RectangleF face = Assert.Single(faces!);
        Assert.InRange(face.X + face.Width / 2, w * 0.35f, w * 0.65f); // the middle of the picture
        Assert.InRange(face.Y + face.Height / 2, h * 0.1f, h * 0.45f); // in its upper part
    }

    [Fact]
    public async Task A_screenshot_pixelates_faces_unless_it_is_only_me()
    {
        if (!FaceFinder.IsSupported) return;
        using var image = new Bitmap(Painting);
        (byte[] bgra, int w, int h) = Pixels(image);
        RectangleF face = (await FaceFinder.DetectAsync(bgra, w, h))!.Single();
        var inside = Rectangle.Round(RectangleF.Inflate(face, -face.Width * 0.2f, -face.Height * 0.2f));

        using var blurred = new Bitmap(image);
        Assert.Equal(1, AutoRedactor.CoverInStill(blurred, SensitiveKinds.Faces));
        Assert.True(Distinct(blurred, inside) < Distinct(image, inside) / 4, "the face is now a few blocks of colour");
        Assert.Equal(image.GetPixel(5, h - 5), blurred.GetPixel(5, h - 5)); // the rest is untouched

        using var mine = new Bitmap(image);
        Assert.Equal(0, AutoRedactor.CoverInStill(mine, SensitiveKinds.Faces | SensitiveKinds.KeepLargestFace));
        Assert.Equal(Distinct(image, inside), Distinct(mine, inside));
    }

    private static int Distinct(Bitmap b, Rectangle r)
    {
        var colours = new HashSet<int>();
        for (int y = r.Top; y < r.Bottom; y += 2)
        for (int x = r.Left; x < r.Right; x += 2) colours.Add(b.GetPixel(x, y).ToArgb());
        return colours.Count;
    }

    [Fact]
    public void Pixelating_stays_inside_the_picture()
    {
        using var image = new Bitmap(50, 40);
        using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.Red);
        AutoRedactor.Pixelate(image, new Rectangle(40, 30, 100, 100)); // runs off the edge
        AutoRedactor.Pixelate(image, new Rectangle(-10, -10, 5, 5)); // entirely outside
        AutoRedactor.Pixelate(image, Rectangle.Empty);
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(45, 35).ToArgb());
    }

    [Fact]
    public void Faces_and_boxes_are_kinds_of_their_own()
    {
        Assert.False(SensitiveKinds.All.HasFlag(SensitiveKinds.Faces)); // never switched on by "everything"
        Assert.True(SensitiveKinds.All.HasFlag(SensitiveKinds.PasswordFields));
        Assert.True(SensitiveKinds.Known.HasFlag(SensitiveKinds.Faces | SensitiveKinds.KeepLargestFace));
        var s = new AppSettings { AutoHideKinds = SensitiveKinds.Faces | SensitiveKinds.KeepLargestFace | SensitiveKinds.PasswordFields }.Normalize();
        Assert.Equal(SensitiveKinds.Faces | SensitiveKinds.KeepLargestFace | SensitiveKinds.PasswordFields, s.AutoHideKinds);
        Assert.Equal(SensitiveKinds.Faces | SensitiveKinds.PasswordFields, CaptureProtocol.ParseAutoHide(((int)(SensitiveKinds.Faces | SensitiveKinds.PasswordFields)).ToString(), "0").Kinds);
        Assert.Empty(SensitiveText.Find([new OcrLine([new OcrWord("jane@example.com", new RectangleF(0, 0, 10, 10))])], SensitiveKinds.Faces | SensitiveKinds.PasswordFields)); // no text kinds: nothing read
        Assert.Equal("a password box and 2 faces", SensitiveText.Words([new(SensitiveKinds.PasswordFields, default), new(SensitiveKinds.Faces, default), new(SensitiveKinds.Faces, default)]));
    }
}

/// <summary>FR-104: split screen's layouts, its commands and what keeps it.</summary>
public class SplitScreenTests
{
    private static readonly SizeF Tv = new(1920, 1080);

    [Theory]
    [InlineData((int)SplitLayout.SideBySide, 2)]
    [InlineData((int)SplitLayout.SideBySide, 4)]
    [InlineData((int)SplitLayout.Stacked, 2)]
    [InlineData((int)SplitLayout.Stacked, 3)]
    [InlineData((int)SplitLayout.Grid, 2)]
    [InlineData((int)SplitLayout.Grid, 3)]
    [InlineData((int)SplitLayout.Grid, 4)]
    [InlineData((int)SplitLayout.Focus, 2)]
    [InlineData((int)SplitLayout.Focus, 4)]
    public void Every_layout_gives_each_picture_its_own_place_on_the_tv(int layout, int pictures)
    {
        List<RectangleF> cells = SplitScreen.Cells(Tv, (SplitLayout)layout, pictures);
        Assert.Equal(pictures, cells.Count);
        foreach (RectangleF c in cells)
        {
            Assert.True(c.Left >= 0 && c.Top >= 0 && c.Right <= Tv.Width + 0.01f && c.Bottom <= Tv.Height + 0.01f, $"{c} is on the TV");
            Assert.True(c.Width > 100 && c.Height > 100, $"{c} is big enough to see");
        }
        for (int i = 0; i < cells.Count; i++)
        for (int j = i + 1; j < cells.Count; j++)
        {
            RectangleF overlap = RectangleF.Intersect(cells[i], cells[j]);
            Assert.True(overlap.Width <= 0.01f || overlap.Height <= 0.01f, $"{cells[i]} and {cells[j]} overlap");
        }
    }

    [Fact]
    public void Focus_keeps_the_main_picture_large()
    {
        List<RectangleF> cells = SplitScreen.Cells(Tv, SplitLayout.Focus, 3);
        Assert.True(cells[0].Width > 2 * cells[1].Width - 30);
        Assert.Equal(cells[1].Width, cells[2].Width);
        Assert.Single(SplitScreen.Cells(Tv, SplitLayout.Grid, 1)); // one picture: the whole TV
        Assert.Equal(new RectangleF(0, 0, 1920, 1080), SplitScreen.Cells(Tv, SplitLayout.Grid, 1)[0]);
        Assert.Equal(4, SplitScreen.Cells(Tv, SplitLayout.Grid, 9).Count); // never more than four
        Assert.Empty(SplitScreen.Cells(SizeF.Empty, SplitLayout.Grid, 2));
    }

    [Fact]
    public void Pictures_keep_their_shape_in_their_place()
    {
        RectangleF fit = SplitScreen.Fit(new SizeF(1600, 900), new RectangleF(0, 0, 800, 800));
        Assert.Equal(new RectangleF(0, 175, 800, 450), fit);
        Assert.Equal(RectangleF.Empty, SplitScreen.Fit(SizeF.Empty, new RectangleF(0, 0, 10, 10)));
    }

    [Fact]
    public void Layouts_read_cycle_describe_and_suggest()
    {
        Assert.Equal(SplitLayout.Stacked, SplitScreen.Parse("stacked"));
        Assert.Equal(SplitLayout.SideBySide, SplitScreen.Parse("side-by-side"));
        Assert.Equal(SplitLayout.SideBySide, SplitScreen.Parse("rockets"));
        Assert.Equal(SplitLayout.SideBySide, SplitScreen.Parse(null));
        Assert.Equal(SplitLayout.SideBySide, SplitScreen.Next(SplitLayout.Focus));
        Assert.All(SplitScreen.All, l => Assert.False(string.IsNullOrEmpty(SplitScreen.Words(l))));
        Assert.Equal(SplitLayout.Stacked, SplitScreen.Suggest([new SizeF(3000, 900), new SizeF(2600, 800)]));
        Assert.Equal(SplitLayout.SideBySide, SplitScreen.Suggest([new SizeF(1600, 900), new SizeF(800, 900)]));
        Assert.Equal(SplitLayout.Grid, SplitScreen.Suggest([new(1, 1), new(1, 1), new(1, 1), new(1, 1)]));
    }

    [Fact]
    public void Split_and_other_pictures_hidden_parts_reach_the_helper()
    {
        var helper = new SplitEffects();
        SourceWindow a = TestData.Window(11, "Sheet"), b = TestData.Window(12, "Notes");
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.Split(SplitLayout.Grid, [a, b])));
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.Split(SplitLayout.Grid, [])));
        Assert.True(CaptureHost.ApplyMark(helper, "split 99 window:5")); // a layout that isn't one: back to one picture
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.HiddenFor(a, [CropRegion.Create(0.1, 0.2, 0.3, 0.4)!], RedactStyle.Blurred)));
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.HiddenFor(a, [], RedactStyle.Solid)));
        Assert.True(CaptureHost.ApplyMark(helper, "hidden-for window:7 junk"));
        Assert.True(CaptureHost.ApplyMark(helper, "hidden-for nonsense 0 0.1,0.1,0.2,0.2"));
        Assert.Equal(
        [
            "split Grid 11 12", "split SideBySide", "split SideBySide",
            "hidden 11 Blurred 0.1,0.2,0.3,0.4", "hidden 11 Solid", "hidden 7 Solid",
        ], helper.Calls);
        Assert.Equal("split none", CaptureProtocol.Split(SplitLayout.Focus, []));
        Assert.Equal(3, CaptureProtocol.Split(SplitLayout.Grid, [a, b, a, b, a]).Split(' ').Length - 2); // at most three beside
    }

    [Fact]
    public void The_command_line_adds_beside_goes_back_and_chooses_the_layout()
    {
        Assert.Equal(["cli:split:Excel"], CommandLine.Parse(["--split", "Excel"]).Request!.Actions);
        Assert.Equal(["cli:split-off"], CommandLine.Parse(["--split", "OFF"]).Request!.Actions);
        Assert.Equal(["cli:layout:SideBySide"], CommandLine.Parse(["--layout", "side-by-side"]).Request!.Actions);
        Assert.Equal(["cli:layout:Grid"], CommandLine.Parse(["--layout=grid"]).Request!.Actions);
        Assert.Contains("--layout needs", CommandLine.Parse(["--layout", "diagonal"]).Error);
        Assert.Contains("--split needs", CommandLine.Parse(["--split"]).Error);
        Assert.Contains("--split", CommandLine.Usage);
        foreach (string id in new[] { "split-screen", "split-add", "split-layout" })
        {
            Assert.NotNull(ShortcutCatalog.Find(id));
            Assert.NotNull(UI.Icons.ForCommand(id));
            Assert.True(TrayMenuPlan.IsKnownCommand(id));
        }
    }

    [Fact]
    public void A_scene_keeps_split_screen_tidied()
    {
        Favourite Fav(string t) => new() { AppName = "app", Title = t };
        var scene = new Scene { Source = Fav("Main"), Inset = Fav("Cam"), SplitWith = [Fav("A"), Fav("B"), Fav("C"), Fav("D")], SplitLayout = "nonsense" };
        Scene tidy = Scene.NormalizeAll([scene])[0]!;
        Assert.Equal(3, tidy.SplitWith.Count);
        Assert.Null(tidy.Inset); // one or the other
        Assert.Equal(nameof(SplitLayout.SideBySide), tidy.SplitLayout);
        Assert.Contains("split screen of 4", tidy.Words());
        Assert.Equal(nameof(SplitLayout.Grid), new AppSettings { SplitLayout = "grid" }.Normalize().SplitLayout);
        Assert.Equal(nameof(SplitLayout.SideBySide), new AppSettings { SplitLayout = "x" }.Normalize().SplitLayout);
    }

    private sealed class SplitEffects : IEffectsEngine
    {
        public List<string> Calls { get; } = [];
        public void Click(PointF at, bool secondary) { }
        public void Pointer(PointF? at) { }
        public void Laser(PointF? at) { }
        public void ShowCursor(bool on) { }
        public void InkStart(InkStroke stroke) { }
        public void InkAdd(int id, IReadOnlyList<PointF> points) { }
        public void InkSet(int id, IReadOnlyList<PointF> points) { }
        public void InkRemove(int id) { }
        public void InkClear() { }
        public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) { }
        public void Spotlight(PointF? at) { }
        public void Caption(string? name, string? second) { }
        public void Logo(string? path) { }
        public void Qr(string? text, InsetCorner corner) { }
        public void LiveCaption(string? text) { }
        public void Inset(SourceWindow? source, InsetCorner corner, int percent) { }
        public event EventHandler<string>? InsetFailed { add { } remove { } }
        public void Split(SplitLayout layout, IReadOnlyList<SourceWindow> others) => Calls.Add($"split {layout} {string.Join(' ', others.Select(o => o.Handle))}".TrimEnd());
        public void HiddenFor(SourceWindow source, IReadOnlyList<CropRegion> regions, RedactStyle style) =>
            Calls.Add($"hidden {source.Handle} {style} {string.Join(' ', regions.Select(r => $"{r.X:0.###},{r.Y:0.###},{r.Width:0.###},{r.Height:0.###}"))}".TrimEnd());
    }
}
