using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-68: a webcam or document camera as what is shown.</summary>
public class CameraTests
{
    // A real camera id, as Windows writes it: backslashes, #, &, braces.
    private const string UsbId = @"\\?\USB#VID_046D&PID_085E&MI_00#7&2a3b4c5d&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
    private static readonly CameraDevice DocCam = new(UsbId, "HUE HD Pro Document Camera");

    [Fact]
    public void A_camera_is_neither_a_window_nor_a_screen()
    {
        SourceWindow camera = SourceWindow.ForCamera(DocCam);
        Assert.True(camera.IsCamera);
        Assert.False(camera.IsWindow);
        Assert.False(camera.IsScreen);
        Assert.Equal(0, camera.Handle);
        Assert.Equal("HUE HD Pro Document Camera", camera.Title);
        Assert.Equal(SourceWindow.CameraApp, camera.AppName);
        Assert.Equal("Camera", SourceWindow.ForCamera(new CameraDevice("x", "")).Title);
    }

    [Fact]
    public void The_same_camera_is_the_same_source_whatever_its_name()
    {
        SourceWindow a = SourceWindow.ForCamera(DocCam), b = SourceWindow.ForCamera(DocCam with { Name = "" });
        Assert.True(a.IsSameSource(b));
        Assert.False(a.IsSameSource(SourceWindow.ForCamera(new CameraDevice("other", "Webcam"))));
        Assert.False(a.IsSameSource(TestData.Window(0, "Camera", "Camera"))); // a window with handle 0 isn't it
        Assert.False(TestData.Window(0, "x").IsSameSource(a));
        Assert.False(a.IsSameSource(SourceWindow.ForScreen(TestData.Laptop)));
    }

    [Fact]
    public void Its_id_reaches_the_helper_intact()
    {
        string spec = CaptureProtocol.SourceSpec(SourceWindow.ForCamera(DocCam));
        Assert.DoesNotContain(' ', spec); // the start line is split on spaces
        SourceWindow parsed = CaptureProtocol.ParseSource(spec, [])!;
        Assert.Equal(UsbId, parsed.Camera!.Id);
        Assert.Equal(spec, CaptureProtocol.Start(42, SourceWindow.ForCamera(DocCam), false, false, new Size(1920, 1080)).Split(' ')[2]);
    }

    [Theory]
    [InlineData("camera:")]
    [InlineData("camera:%zz")]
    public void A_broken_camera_spec_is_refused_or_kept_as_given(string spec)
    {
        SourceWindow? parsed = CaptureProtocol.ParseSource(spec, []);
        if (parsed is not null) Assert.True(parsed.IsCamera); // never mistaken for a window or a screen
    }

    [Fact]
    public void Showing_a_camera_always_goes_through_the_helper() =>
        Assert.True(CaptureChoice.DrawsPointerAndScreens(isScreen: !SourceWindow.ForCamera(DocCam).IsWindow, startFrozen: false, preferWgc: false, showPointer: false));

    [Fact]
    public void A_favourite_camera_is_found_again_by_its_id()
    {
        Favourite fav = FavouriteMatcher.FromSource(SourceWindow.ForCamera(DocCam));
        Assert.Equal(UsbId, fav.CameraId);
        Assert.Null(fav.ScreenDevice);
        Assert.True(fav.Matches(FavouriteMatcher.FromSource(SourceWindow.ForCamera(DocCam with { Name = "Renamed" }))));
        Assert.False(fav.Matches(FavouriteMatcher.FromSource(SourceWindow.ForCamera(new CameraDevice("other", DocCam.Name)))));
        Assert.False(fav.Matches(new Favourite { AppName = SourceWindow.CameraApp, Title = DocCam.Name })); // a window called "Camera" isn't it
    }

    [Fact]
    public void Cameras_are_found_by_part_of_their_name()
    {
        List<CameraDevice> cameras = [new("a", "Integrated Camera"), DocCam, new("c", "Logitech BRIO")];
        Assert.Equal(UsbId, Cameras.Find(cameras, "document")!.Id);
        Assert.Equal("c", Cameras.Find(cameras, "brio")!.Id);
        Assert.Equal("a", Cameras.Find(cameras, "integrated camera")!.Id);
        Assert.Null(Cameras.Find(cameras, "Nikon"));
        Assert.Null(Cameras.Find(cameras, " "));
    }

    [Fact]
    public async Task Asking_Windows_for_cameras_never_throws()
    {
        List<CameraDevice> cameras = await Cameras.ListAsync(fresh: true); // none, one or several: depends on this PC
        Assert.All(cameras, c => Assert.False(string.IsNullOrEmpty(c.Id)));
        Assert.Equal(cameras.Count, cameras.Select(c => c.Id).Distinct().Count());
    }

    // ───────────────────────────── picture size ─────────────────────────────

    [Fact]
    public void Full_HD_at_thirty_frames_is_preferred()
    {
        CameraFormat? best = CameraFormats.Choose([new(640, 480, 30), new(1920, 1080, 30), new(1920, 1080, 60), new(3840, 2160, 30), new(1280, 720, 30)]);
        Assert.Equal(new CameraFormat(1920, 1080, 30), best);
    }

    [Fact]
    public void A_sharper_picture_wins_over_more_frames_once_smooth_enough()
    {
        Assert.Equal(new CameraFormat(1920, 1080, 15), CameraFormats.Choose([new(1280, 720, 30), new(1920, 1080, 15)]));
        Assert.Equal(new CameraFormat(1280, 720, 30), CameraFormats.Choose([new(1280, 720, 30), new(1920, 1080, 5)])); // 5 fps is a slideshow
    }

    [Fact]
    public void A_slow_camera_still_gets_its_best()
    {
        // Nothing reaches 15 fps: the sharpest that fits Full HD.
        Assert.Equal(new CameraFormat(1280, 960, 5), CameraFormats.Choose([new(800, 600, 5), new(1280, 960, 5)]));
        // 1600 × 1200 is taller than Full HD, so the one that fits wins.
        Assert.Equal(new CameraFormat(800, 600, 5), CameraFormats.Choose([new(800, 600, 5), new(1600, 1200, 5)]));
    }

    [Fact]
    public void Only_bigger_than_Full_HD_takes_the_smallest()
    {
        Assert.Equal(new CameraFormat(2592, 1944, 15), CameraFormats.Choose([new(3264, 2448, 15), new(2592, 1944, 15)]));
    }

    [Fact]
    public void A_document_camera_standing_portrait_fits_too()
    {
        Assert.Equal(new CameraFormat(1080, 1920, 30), CameraFormats.Choose([new(1080, 1920, 30), new(720, 1280, 30)]));
    }

    [Fact]
    public void Nonsense_offers_leave_the_camera_alone()
    {
        Assert.Null(CameraFormats.Choose([]));
        Assert.Null(CameraFormats.Choose([new(0, 0, 30), new(1920, 1080, double.NaN)]));
    }
}

/// <summary>A camera that can't start says why, in the words the helper used.</summary>
public class CameraSessionTests
{
    [Fact]
    public async Task A_camera_that_cant_start_explains_itself()
    {
        var platform = new FakePlatform { FailNextEngine = true, OfferFallback = false };
        var session = new SessionController(platform);
        var messages = new List<string>();
        session.Message += (_, m) => messages.Add(m);
        await session.PresentAsync(SourceWindow.ForCamera(new CameraDevice("cam", "Webcam")), TestData.Tv, true);
        Assert.NotEqual(SessionState.Presenting, session.State);
        Assert.Contains(messages, m => m.StartsWith("Couldn't show “Webcam”.", StringComparison.Ordinal));
    }
}

/// <summary>
/// Opt-in, because it switches a real camera on (its light comes on for a moment):
/// set SCP_CAMERA_TESTS=1. Starts the first camera in the helper's way and waits for a frame.
/// </summary>
public class RealCameraTests
{
    [Fact]
    public async Task The_first_camera_delivers_a_frame()
    {
        if (Environment.GetEnvironmentVariable("SCP_CAMERA_TESTS") != "1") return;
        List<CameraDevice> cameras = await Cameras.ListAsync(fresh: true);
        if (cameras.Count == 0) return;
        using var arrived = new ManualResetEventSlim();
        using CameraFeed feed = await Task.Run(() => CameraFeed.Start(cameras[0].Id, arrived.Set));
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(10)), "no frame within 10 s");
        Size size = Size.Empty;
        Assert.True(feed.TakeLatest((pixels, stride, s) =>
        {
            size = s;
            Assert.True(pixels.Length >= stride * s.Height);
        }));
        Assert.True(size.Width > 0 && size.Height > 0);
        Assert.True(size.Width <= CameraFormats.MaxWidth || size.Height <= CameraFormats.MaxWidth);
    }
}
