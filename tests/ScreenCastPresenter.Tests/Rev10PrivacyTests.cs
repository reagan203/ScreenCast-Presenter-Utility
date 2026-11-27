using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-112: a whole screen shown stops short of its taskbar, a hidden taskbar included.</summary>
public class TaskbarCropTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void A_taskbar_at_the_bottom_is_left_out()
    {
        CropRegion crop = TaskbarCrop.ClearOf(Screen, new Rectangle(0, 0, 1920, 1032))!;
        Assert.Equal(0, crop.X, 6);
        Assert.Equal(0, crop.Y, 6);
        Assert.Equal(1, crop.Width, 6);
        Assert.Equal(1032 / 1080.0, crop.Height, 6);
        Assert.Equal(new Rectangle(0, 0, 1920, 1032), crop.ToPixels(Screen.Size));
    }

    [Fact]
    public void A_taskbar_on_either_side_or_on_top_is_left_out_too()
    {
        Assert.Equal(new Rectangle(62, 0, 1858, 1080), TaskbarCrop.ClearOf(Screen, new Rectangle(62, 0, 1858, 1080))!.ToPixels(Screen.Size));
        Assert.Equal(new Rectangle(0, 0, 1858, 1080), TaskbarCrop.ClearOf(Screen, new Rectangle(0, 0, 1858, 1080))!.ToPixels(Screen.Size));
        Assert.Equal(new Rectangle(0, 40, 1920, 1040), TaskbarCrop.ClearOf(Screen, new Rectangle(0, 40, 1920, 1040))!.ToPixels(Screen.Size));
    }

    [Fact]
    public void A_screen_to_the_left_of_the_main_one_works_in_its_own_coordinates()
    {
        var left = new Rectangle(-2560, -200, 2560, 1440);
        CropRegion crop = TaskbarCrop.ClearOf(left, new Rectangle(-2560, -200, 2560, 1392))!;
        Assert.Equal(new Rectangle(0, 0, 2560, 1392), crop.ToPixels(left.Size));
    }

    [Fact]
    public void A_taskbar_that_hides_itself_is_left_out_at_its_full_height()
    {
        // The work area is the whole screen (it hides), but it pops up over the bottom 48 pixels.
        CropRegion crop = TaskbarCrop.ClearOf(Screen, Screen, [(ScreenEdge.Bottom, 48)])!;
        Assert.Equal(new Rectangle(0, 0, 1920, 1032), crop.ToPixels(Screen.Size));
        // Both: a docked bar and a hiding one on another edge.
        Assert.Equal(new Rectangle(0, 0, 1872, 1032), TaskbarCrop.ClearOf(Screen, new Rectangle(0, 0, 1920, 1032), [(ScreenEdge.Right, 48)])!.ToPixels(Screen.Size));
    }

    [Fact]
    public void No_taskbar_or_nonsense_leaves_the_screen_whole()
    {
        Assert.Null(TaskbarCrop.ClearOf(Screen, Screen));
        Assert.Null(TaskbarCrop.ClearOf(Screen, Screen, []));
        Assert.Null(TaskbarCrop.ClearOf(Screen, new Rectangle(5000, 5000, 100, 100))); // a work area off the screen: nothing known
        Assert.Null(TaskbarCrop.ClearOf(Rectangle.Empty, Screen));
        Assert.Null(TaskbarCrop.ClearOf(Screen, Screen, [(ScreenEdge.Bottom, -40)]));
    }

    [Fact]
    public void A_bar_never_swallows_the_screen()
    {
        CropRegion crop = TaskbarCrop.ClearOf(Screen, Screen, [(ScreenEdge.Bottom, 5000), (ScreenEdge.Top, 5000)])!;
        Assert.True(crop.Height >= 0.19, $"left {crop.Height:P0} of the height");
        Assert.InRange(crop.Y, 0.39, 0.41);
    }

    [Fact]
    public void A_chosen_part_is_kept_clear_of_the_taskbar()
    {
        CropRegion clear = TaskbarCrop.ClearOf(Screen, new Rectangle(0, 0, 1920, 1032))!;
        Assert.Equal((clear, false), TaskbarCrop.Inside(null, clear));
        Assert.Equal((CropRegion.Full, false), TaskbarCrop.Inside(CropRegion.Full, null));

        CropRegion top = CropRegion.Create(0.1, 0.1, 0.5, 0.5)!;
        Assert.Equal((top, false), TaskbarCrop.Inside(top, clear)); // all above the taskbar: unchanged

        (CropRegion? overlap, bool empty) = TaskbarCrop.Inside(CropRegion.Create(0, 0.5, 1, 0.5), clear);
        Assert.False(empty);
        Assert.Equal(new Rectangle(0, 540, 1920, 492), overlap!.ToPixels(Screen.Size)); // trimmed at the taskbar

        Assert.Equal((null, true), TaskbarCrop.Inside(CropRegion.Create(0, 0.97, 1, 0.03), clear)); // all taskbar: nothing may be shown
        Assert.Equal((null, true), TaskbarCrop.Inside(CropRegion.Create(0, 0.95, 1, 0.05), clear)); // a sliver above it: too small, never more instead
    }

    [Fact]
    public void Thickness_is_the_bars_short_side()
    {
        Assert.Equal(48, TaskbarCrop.Thickness(ScreenEdge.Bottom, new Rectangle(0, 1078, 1920, 48)));
        Assert.Equal(62, TaskbarCrop.Thickness(ScreenEdge.Left, new Rectangle(-60, 0, 62, 1080)));
        Assert.Equal(0, TaskbarCrop.Thickness(ScreenEdge.Top, new Rectangle(0, 0, 100, -5)));
    }

    [Fact]
    public void This_PCs_real_taskbar_is_found_without_trouble()
    {
        foreach (DisplayMonitor m in DisplayHelpers.GetMonitors())
        {
            List<(ScreenEdge, int)> hidden = Taskbars.AutoHiddenOn(m.Bounds);
            Assert.All(hidden, h => Assert.InRange(h.Item2, 1, m.Bounds.Height));
            if (Taskbars.ClearArea(m) is { } clear)
            {
                Assert.False(clear.IsFull);
                Assert.True(clear.Width > 0.5 && clear.Height > 0.5, $"{m.Name}: {clear}");
                // The work area is always inside what's shown: never more is left out than Windows reserves (plus a bar that hides).
                Rectangle shown = clear.ToScreen(m.Bounds);
                if (hidden.Count == 0) Assert.Equal(Rectangle.Intersect(m.Bounds, m.WorkArea), shown);
            }
        }
    }

    [Fact]
    public void The_presenters_own_crop_is_told_apart_from_the_taskbar_one()
    {
        CropRegion clear = TaskbarCrop.ClearOf(Screen, new Rectangle(0, 0, 1920, 1032))!;
        SourceWindow screen = SourceWindow.ForScreen(TestData.Laptop) with { Crop = clear, AutoCrop = clear };
        Assert.Null(screen.ChosenCrop);
        CropRegion part = CropRegion.Create(0.2, 0.2, 0.4, 0.4)!;
        Assert.Equal(part, (screen with { Crop = part }).ChosenCrop);
        Assert.Equal(part, SourceWindow.ForScreen(TestData.Laptop).With(part).ChosenCrop);
    }
}

internal static class SourceTestExtensions
{
    public static SourceWindow With(this SourceWindow s, CropRegion crop) => s with { Crop = crop };
}

/// <summary>A screen's crop travels to the helper with it, so a pane or the small picture leaves out the taskbar too, and hidden parts can't go missing.</summary>
public class ScreenSpecTests
{
    private static readonly DisplayMonitor[] Monitors = [TestData.Laptop, TestData.Tv];

    [Fact]
    public void A_screen_with_part_of_it_shown_carries_the_part()
    {
        CropRegion clear = TaskbarCrop.ClearOf(TestData.Laptop.Bounds, TestData.Laptop.WorkArea)!;
        SourceWindow screen = SourceWindow.ForScreen(TestData.Laptop) with { Crop = clear };
        string spec = CaptureProtocol.SourceSpec(screen);
        Assert.StartsWith(@"screen:\\.\DISPLAY1@", spec);
        SourceWindow back = CaptureProtocol.ParseSource(spec, Monitors)!;
        Assert.True(back.IsSameSource(screen));
        Assert.Equal(clear, back.Crop);
        Assert.Equal(spec, CaptureProtocol.SourceSpec(back)); // round trip exactly
        Assert.Equal(@"screen:\\.\DISPLAY1", CaptureProtocol.SourceSpec(SourceWindow.ForScreen(TestData.Laptop)));
    }

    [Fact]
    public void Hidden_parts_are_kept_by_the_source_whatever_part_is_shown()
    {
        SourceWindow whole = SourceWindow.ForScreen(TestData.Laptop);
        SourceWindow part = whole with { Crop = CropRegion.Create(0, 0, 1, 0.9555555555) };
        Assert.Equal(CaptureProtocol.CoverKey(whole), CaptureProtocol.CoverKey(part));
        Assert.Equal(@"screen:\\.\DISPLAY1", CaptureProtocol.CoverKey(part));
        Assert.Equal("window:42", CaptureProtocol.CoverKey(TestData.Window(42, "x")));
    }

    [Fact]
    public void A_part_that_cant_be_read_never_shows_more()
    {
        Assert.Null(CaptureProtocol.ParseSource(@"screen:\\.\DISPLAY1@nonsense", Monitors));
        Assert.Null(CaptureProtocol.ParseSource(@"screen:\\.\DISPLAY1@", Monitors));
        Assert.Null(CaptureProtocol.ParseSource(@"screen:\\.\DISPLAY9@0,0,1,0.9", Monitors));
    }

    [Fact]
    public void Videos_travel_with_any_file_name()
    {
        string path = @"C:\Vidéos\Lobby loop (final) 100%.mp4";
        SourceWindow video = SourceWindow.ForVideo(path);
        string spec = CaptureProtocol.SourceSpec(video);
        Assert.DoesNotContain(' ', spec);
        SourceWindow back = CaptureProtocol.ParseSource(spec)!;
        Assert.True(back.IsVideo);
        Assert.Equal(path, back.Video);
        Assert.True(back.IsOffScreen);
        Assert.False(back.IsWindow);
        Assert.True(back.IsSameSource(video));
        Assert.False(back.IsSameSource(SourceWindow.ForPicture(path)));
        Assert.Null(CaptureProtocol.ParseSource("video:relative.mp4"));
        Assert.Null(CaptureProtocol.ParseSource("video:"));
    }
}

/// <summary>FR-113: desktop icons hidden while a whole screen is shown, and only ours shown again.</summary>
public class DesktopIconGuardTests
{
    private sealed class FakeDesktop : IDesktopIconPlatform
    {
        public nint List = 100;
        public readonly Dictionary<nint, bool> Visible = new() { [100] = true };
        public int Changes;

        public nint FindList() => List;
        public bool IsVisible(nint list) => Visible.GetValueOrDefault(list);

        public void SetVisible(nint list, bool visible)
        {
            Visible[list] = visible;
            Changes++;
        }
    }

    [Fact]
    public void Icons_are_hidden_and_come_back()
    {
        var desktop = new FakeDesktop();
        var remembered = new List<bool>();
        var guard = new DesktopIconGuard(desktop, remembered.Add);
        guard.Update(true);
        Assert.False(desktop.Visible[100]);
        Assert.True(guard.Hiding);
        guard.Update(true); // again: nothing more
        guard.Check();
        Assert.Equal(1, desktop.Changes);
        guard.Update(false);
        Assert.True(desktop.Visible[100]);
        Assert.False(guard.Hiding);
        Assert.Equal([true, false], remembered); // remembered for a crash, then cleared
    }

    [Fact]
    public void Icons_the_presenter_hid_are_left_alone()
    {
        var desktop = new FakeDesktop();
        desktop.Visible[100] = false; // "Show desktop icons" is off in Explorer
        var remembered = new List<bool>();
        var guard = new DesktopIconGuard(desktop, remembered.Add);
        guard.Update(true);
        guard.Update(false);
        Assert.False(desktop.Visible[100]); // never shown by us
        Assert.Equal(0, desktop.Changes);
        Assert.Empty(remembered);
    }

    [Fact]
    public void A_restarted_explorers_new_icons_are_hidden_too()
    {
        var desktop = new FakeDesktop();
        var guard = new DesktopIconGuard(desktop, _ => { });
        guard.Update(true);
        desktop.List = 200; // Explorer restarted: a new, visible list
        desktop.Visible[200] = true;
        guard.Check();
        Assert.False(desktop.Visible[200]);
        guard.Update(false);
        Assert.True(desktop.Visible[200]);
    }

    [Fact]
    public void No_desktop_at_the_time_is_looked_for_again_later()
    {
        var desktop = new FakeDesktop { List = 0 };
        var guard = new DesktopIconGuard(desktop, _ => { });
        guard.Update(true);
        Assert.False(guard.Hiding);
        desktop.List = 100;
        guard.Check();
        Assert.True(guard.Hiding);
        Assert.False(desktop.Visible[100]);
    }

    [Fact]
    public void After_a_crash_the_next_start_shows_them_again()
    {
        var desktop = new FakeDesktop();
        desktop.Visible[100] = false;
        Assert.False(DesktopIconGuard.RepairAfterCrash(desktop, hiddenLastRun: false));
        Assert.False(desktop.Visible[100]);
        Assert.True(DesktopIconGuard.RepairAfterCrash(desktop, hiddenLastRun: true));
        Assert.True(desktop.Visible[100]);
        Assert.False(DesktopIconGuard.RepairAfterCrash(desktop, hiddenLastRun: true)); // already showing
        Assert.False(DesktopIconGuard.RepairAfterCrash(new FakeDesktop { List = 0 }, hiddenLastRun: true));
    }

    [Fact]
    public void The_real_desktop_list_is_found_and_never_touched_by_the_tests()
    {
        nint list = DesktopIcons.Instance.FindList();
        if (list == 0) return; // no Explorer desktop here (a server or a test session)
        Assert.Equal("SysListView32", DesktopIcons.ClassOf((HWND)list));
        _ = DesktopIcons.Instance.IsVisible(list); // asked, not changed
        Assert.False(DesktopIcons.Instance.IsVisible(0));
    }
}

/// <summary>FR-114: Open and Save boxes are told apart from other dialog boxes.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class FileBoxTests
{
    [Fact]
    public void File_and_folder_boxes_are_recognised_by_what_they_hold()
    {
        Assert.True(FileBoxes.IsFileBox("#32770", ["DUIViewWndClassName", "DirectUIHWND"]));
        Assert.True(FileBoxes.IsFileBox("#32770", ["Button", "SHELLDLL_DefView", "SysListView32"]));
        Assert.True(FileBoxes.IsFileBox("#32770", ["Static", "SysTreeView32", "Button"])); // Browse for folder
        Assert.False(FileBoxes.IsFileBox("#32770", ["DirectUIHWND", "CtrlNotifySink"])); // a task dialog
        Assert.False(FileBoxes.IsFileBox("#32770", ["Static", "Button", "Button"])); // a message box
        Assert.False(FileBoxes.IsFileBox("CabinetWClass", ["SHELLDLL_DefView"])); // an Explorer window: shown only if chosen
        Assert.False(FileBoxes.IsFileBox("#32770", []));
    }

    [Fact]
    public void Only_a_box_on_the_part_shown_counts()
    {
        VisibleWindow box = new(1, "notepad", new Rectangle(100, 100, 600, 400));
        Assert.Equal(box, FileBoxes.FindOnScreen([box], new Rectangle(0, 0, 1920, 1080)));
        Assert.Null(FileBoxes.FindOnScreen([box], new Rectangle(1920, 0, 1920, 1080)));
        Assert.Null(FileBoxes.FindOnScreen([box], Rectangle.Empty));
        Assert.Null(FileBoxes.FindOnScreen([new VisibleWindow(2, "x", Rectangle.Empty)], new Rectangle(0, 0, 1920, 1080)));
    }

    [Fact]
    public void A_real_open_box_is_a_file_box_and_a_real_message_box_isnt()
    {
        string title = "SCP open box test " + Guid.NewGuid().ToString("N")[..6];
        Assert.True(OnItsOwnThread(() =>
        {
            using var dialog = new OpenFileDialog { Title = title };
            dialog.ShowDialog();
        }, title, out List<string> openClasses), "the Open box opened");
        Assert.True(FileBoxes.IsFileBox(FileBoxes.DialogClass, openClasses), "classes: " + string.Join(", ", openClasses.Distinct()));

        string caption = "SCP message test " + Guid.NewGuid().ToString("N")[..6];
        Assert.True(OnItsOwnThread(() => MessageBox.Show("Hello", caption), caption, out List<string> messageClasses), "the message box opened");
        Assert.False(FileBoxes.IsFileBox(FileBoxes.DialogClass, messageClasses), "classes: " + string.Join(", ", messageClasses.Distinct()));
    }

    /// <summary>Shows a dialog on its own thread, reads its child classes once it's up, then closes it.</summary>
    private static unsafe bool OnItsOwnThread(Action show, string title, out List<string> classes)
    {
        var thread = new Thread(() => show()) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        HWND dialog = HWND.Null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            dialog = PInvoke.FindWindow("#32770", title);
            if (!dialog.IsNull && PInvoke.IsWindowVisible(dialog)) break;
            Thread.Sleep(50);
        }
        classes = [];
        if (dialog.IsNull) return false;
        Thread.Sleep(400); // its contents fill in
        classes = FileBoxFinder.ChildClasses(dialog);
        PInvoke.PostMessage(dialog, 0x0010 /* WM_CLOSE */, 0, 0);
        thread.Join(TimeSpan.FromSeconds(5));
        return true;
    }
}

/// <summary>FR-134: a meeting app's border round a whole screen.</summary>
public class ShareWatchTests
{
    private static readonly List<WatchedScreen> Screens =
    [
        new(@"\\.\DISPLAY1", "Screen 1", new Rectangle(0, 0, 1920, 1080), Safe: false),
        new(@"\\.\DISPLAY2", "Screen 2", new Rectangle(1920, 0, 1920, 1080), Safe: true), // the TV showing only the chosen source
    ];

    private static ShareFacts Border(string app, Rectangle r, bool layered = true, bool through = true, bool top = true) => new(1, app, r, layered, through, top);

    [Fact]
    public void A_meeting_app_sharing_a_whole_screen_is_found()
    {
        WholeScreenShare share = Assert.Single(ShareWatch.Find([Border("Zoom", new Rectangle(-4, -4, 1928, 1088))], Screens));
        Assert.Equal(("Zoom", @"\\.\DISPLAY1", "Screen 1"), (share.App, share.Device, share.ScreenName));
        Assert.Equal("Teams", Assert.Single(ShareWatch.Find([Border("ms-teams", new Rectangle(0, 0, 1920, 1080))], Screens)).App);
        Assert.Equal("Webex", Assert.Single(ShareWatch.Find([Border("CiscoCollabHost", new Rectangle(2, 2, 1916, 1076))], Screens)).App);
    }

    [Fact]
    public void Sharing_a_safe_screen_or_something_smaller_isnt_a_problem()
    {
        Assert.Empty(ShareWatch.Find([Border("Zoom", new Rectangle(1920, 0, 1920, 1080))], Screens)); // the TV: only the chosen source is on it
        Assert.Empty(ShareWatch.Find([Border("Zoom", new Rectangle(200, 200, 800, 600))], Screens)); // a window shared
        Assert.Empty(ShareWatch.Find([Border("Zoom", new Rectangle(-100, -100, 2200, 1300))], Screens)); // too far out
    }

    [Fact]
    public void Only_meeting_apps_borders_count()
    {
        Assert.Empty(ShareWatch.Find([Border("notepad", new Rectangle(0, 0, 1920, 1080))], Screens));
        Assert.Empty(ShareWatch.Find([Border("Zoom", new Rectangle(0, 0, 1920, 1080), layered: false)], Screens)); // Zoom's own full-screen window
        Assert.Empty(ShareWatch.Find([Border("Zoom", new Rectangle(0, 0, 1920, 1080), through: false)], Screens));
        Assert.Empty(ShareWatch.Find([Border("Zoom", new Rectangle(0, 0, 1920, 1080), top: false)], Screens));
        Assert.Null(ShareWatch.MeetingName(null));
        Assert.Equal("Zoom", ShareWatch.MeetingName("ZOOM"));
    }

    [Fact]
    public void Each_share_is_said_once_while_it_lasts()
    {
        var first = ShareWatch.Find([Border("Zoom", new Rectangle(0, 0, 1920, 1080)), Border("Zoom", new Rectangle(0, 0, 1920, 1080))], Screens);
        Assert.Single(first); // two border windows, one share
        Assert.Single(ShareWatch.New([], first));
        Assert.Empty(ShareWatch.New(first, first));
        var teams = ShareWatch.Find([Border("Teams", new Rectangle(0, 0, 1920, 1080))], Screens);
        Assert.Single(ShareWatch.New(first, [.. first, .. teams]));
    }
}

/// <summary>FR-133: a browser shows only its page.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class BrowserPageTests
{
    private static readonly Rectangle Client = new(100, 100, 1600, 900);

    [Fact]
    public void The_page_is_cropped_from_the_window()
    {
        CropRegion crop = BrowserPage.Crop(Client, new Rectangle(100, 220, 1600, 780))!;
        Assert.Equal(new Rectangle(100, 220, 1600, 780), crop.ToScreen(Client));
    }

    [Fact]
    public void Nothing_to_leave_out_or_nothing_believable_shows_the_window_as_it_is()
    {
        Assert.Null(BrowserPage.Crop(Client, null));
        Assert.Null(BrowserPage.Crop(Client, Client)); // full screen (F11)
        Assert.Null(BrowserPage.Crop(Client, new Rectangle(101, 101, 1598, 895))); // as good as all of it
        Assert.Null(BrowserPage.Crop(Client, new Rectangle(100, 100, 300, 300))); // a side panel, not the page
        Assert.Null(BrowserPage.Crop(Client, new Rectangle(5000, 5000, 100, 100))); // somewhere else
        Assert.Null(BrowserPage.Crop(Rectangle.Empty, new Rectangle(0, 0, 10, 10)));
        // A page reaching past the window is kept to the window.
        Assert.Equal(new Rectangle(100, 220, 1600, 780), BrowserPage.Crop(Client, new Rectangle(50, 220, 1800, 900))!.ToScreen(Client));
    }

    [Fact]
    public void Tiny_moves_dont_move_the_picture()
    {
        CropRegion a = CropRegion.Create(0, 0.1, 1, 0.9)!;
        Assert.True(BrowserPage.Same(a, CropRegion.Create(0, 0.1005, 1, 0.8995)));
        Assert.False(BrowserPage.Same(a, CropRegion.Create(0, 0.15, 1, 0.85)));
        Assert.True(BrowserPage.Same(null, null));
        Assert.False(BrowserPage.Same(a, null));
    }

    [Fact]
    public async Task A_real_document_area_is_found_by_ui_automation()
    {
        using var host = new DocumentForm();
        nint handle = host.Handle;
        Rectangle? found = await Task.Run(() =>
        {
            using UiAutomation? uia = UiAutomation.Create();
            return uia?.DocumentIn(handle);
        });
        Assert.NotNull(found);
        Rectangle expected = host.DocumentOnScreen;
        Assert.True(Math.Abs(found!.Value.X - expected.X) <= 6 && Math.Abs(found.Value.Y - expected.Y) <= 6 && Math.Abs(found.Value.Width - expected.Width) <= 12,
            $"Windows put it at {found}; it's at {expected}");
        CropRegion crop = BrowserPage.Crop(host.ClientOnScreen, found)!;
        Assert.InRange(crop.Y, 0.25, 0.4); // below the "address bar"
        Assert.Null(await Task.Run(() =>
        {
            using UiAutomation? uia = UiAutomation.Create();
            return uia?.DocumentIn(0);
        }));
    }

    [Fact]
    public void The_watcher_reports_the_page_and_follows_it()
    {
        using var host = new DocumentForm();
        var seen = new List<CropRegion?>();
        var context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        using var watcher = new BrowserPageWatcher((_, crop) => { lock (seen) seen.Add(crop); });
        watcher.Watch(host.Handle);
        bool Pump(Func<bool> until)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                lock (seen) if (until()) return true;
                Thread.Sleep(20);
            }
            return false;
        }
        Assert.True(Pump(() => seen.Count > 0 && seen[^1] is not null), "the page was reported");
        host.Run(() => host.Document.Top = 140); // the "bookmarks bar" appeared
        Assert.True(Pump(() => seen[^1] is { Y: > 0.33 }), "followed the page down"); // 120 → 140 of 400 pixels
        watcher.Watch(0);
    }

    /// <summary>A window with a strip at the top (like an address bar) and a document below it.</summary>
    private sealed class DocumentForm : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Form? _form;

        public DocumentForm()
        {
            _thread = new Thread(() =>
            {
                _form = new Form
                {
                    Text = "SCP page test " + Guid.NewGuid().ToString("N")[..6],
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(80, 80),
                    ClientSize = new Size(600, 400),
                    ShowInTaskbar = false,
                };
                _form.Controls.Add(new TextBox { Location = new Point(10, 10), Width = 560, Text = "https://example.com" });
                Document = new RichTextBox { Location = new Point(0, 120), Size = new Size(600, 280), Text = "The page" };
                _form.Controls.Add(Document);
                _form.Shown += (_, _) => _ready.Set();
                Application.Run(_form);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)), "the window opened");
            Thread.Sleep(200);
        }

        public RichTextBox Document { get; private set; } = null!;
        public nint Handle => (nint)_form!.Invoke(() => _form.Handle);
        public Rectangle DocumentOnScreen => (Rectangle)_form!.Invoke(() => Document.RectangleToScreen(Document.ClientRectangle));
        public Rectangle ClientOnScreen => (Rectangle)_form!.Invoke(() => _form.RectangleToScreen(_form.ClientRectangle));
        public void Run(Action action) => _form!.Invoke(action);

        public void Dispose()
        {
            _form?.Invoke(() => _form.Close());
            _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
        }
    }
}

/// <summary>FR-119: Follow me follows only to the apps chosen, never to a never-show app.</summary>
public class FollowMeTests
{
    private static readonly string[] Apps = ["POWERPNT", "chrome"];

    [Fact]
    public void Switching_to_a_chosen_app_follows()
    {
        Assert.True(FollowMe.ShouldFollow("chrome", 2, false, 1, Apps, []));
        Assert.True(FollowMe.ShouldFollow("powerpnt", 2, false, 1, Apps, [])); // any capitals
    }

    [Fact]
    public void Anything_else_stays_private()
    {
        Assert.False(FollowMe.ShouldFollow("OUTLOOK", 2, false, 1, Apps, [])); // not chosen
        Assert.False(FollowMe.ShouldFollow("chrome", 1, false, 1, Apps, [])); // already shown
        Assert.False(FollowMe.ShouldFollow("chrome", 2, true, 1, Apps, [])); // minimised
        Assert.False(FollowMe.ShouldFollow(null, 2, false, 1, Apps, []));
        Assert.False(FollowMe.ShouldFollow("chrome", 0, false, 1, Apps, []));
        Assert.False(FollowMe.ShouldFollow("chrome", 2, false, 1, Apps, ["Chrome"])); // never shown wins, whatever the list says
    }

    [Fact]
    public void The_list_is_tidied()
    {
        List<string> apps = FollowMe.Normalize(["  POWERPNT ", "powerpnt", "", null, "KeePass", new string('x', 500), "chrome"], ["keepass"]);
        Assert.Equal(["POWERPNT", "chrome"], apps);
        Assert.Equal(FollowMe.MaxApps, FollowMe.Normalize(Enumerable.Range(0, 50).Select(i => "app" + i)).Count);
        Assert.True(FollowMe.Settle > TimeSpan.FromMilliseconds(200), "Alt+Tab passing through other windows isn't followed");
    }
}
