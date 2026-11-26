using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-90: next and previous slide reach the shown window as Page Down and Page Up, without taking the focus.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class SlideKeysTests
{
    /// <summary>A hidden window on its own thread with a message loop, counting the keys it gets.</summary>
    private sealed class KeyCatcher : NativeWindow, IDisposable
    {
        private const int WmKeyDown = 0x0100;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly List<Keys> _keys = [];

        public KeyCatcher()
        {
            _thread = new Thread(() =>
            {
                CreateHandle(new CreateParams { Caption = "SCP slide keys test" });
                _ready.Set();
                Application.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(10));
        }

        public List<Keys> Keys
        {
            get
            {
                lock (_keys) return [.. _keys];
            }
        }

        private const int WmClose = 0x8001; // WM_APP + 1: close down on the window's own thread

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmKeyDown) lock (_keys) _keys.Add((Keys)(int)m.WParam);
            if (m.Msg == WmClose)
            {
                // Destroyed properly, on its own thread, before the loop ends: a window left to die with
                // its thread upsets the next swap chain made in this process.
                DestroyHandle();
                Application.ExitThread();
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            nint handle = Handle;
            if (handle != 0) Windows.Win32.PInvoke.PostMessage((Windows.Win32.Foundation.HWND)handle, WmClose, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
        }
    }

    [Fact]
    public async Task Next_and_previous_go_to_the_window_as_page_down_and_page_up()
    {
        using var window = new KeyCatcher();
        Assert.True(SlideKeys.Send(window.Handle, forward: true));
        Assert.True(SlideKeys.Send(window.Handle, forward: false));
        Assert.True(SlideKeys.Send(window.Handle, forward: true));
        for (int i = 0; i < 50 && window.Keys.Count < 3; i++) await Task.Delay(20);
        Assert.Equal([Keys.PageDown, Keys.PageUp, Keys.PageDown], window.Keys);
    }

    [Fact]
    public void A_window_that_has_gone_takes_nothing() => Assert.False(SlideKeys.Send(0x7FFF_FFF0, forward: true));
}
