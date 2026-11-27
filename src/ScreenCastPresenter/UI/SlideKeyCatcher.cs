using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Clicker keys (FR-132): while on, Page Down and Page Up (and, if chosen, the arrow keys) are taken
/// by ScreenCast Presenter, from any keyboard or presenter clicker, and sent to what's shown, so the
/// slides move even while the presenter works in another window. Taken with RegisterHotKey, like the
/// app's own shortcuts: no keyboard hook, nothing injected into other apps. Held keys don't repeat.
/// </summary>
internal sealed class SlideKeyCatcher : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int FirstId = 0xB100;

    private static readonly (VIRTUAL_KEY Key, bool Forward, bool Arrow)[] Keys =
    [
        (VIRTUAL_KEY.VK_NEXT, true, false),
        (VIRTUAL_KEY.VK_PRIOR, false, false),
        (VIRTUAL_KEY.VK_RIGHT, true, true),
        (VIRTUAL_KEY.VK_DOWN, true, true),
        (VIRTUAL_KEY.VK_LEFT, false, true),
        (VIRTUAL_KEY.VK_UP, false, true),
    ];

    private readonly List<int> _registered = [];

    public SlideKeyCatcher() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // a message-only window

    /// <summary>A key was pressed: true for next, false for previous.</summary>
    public event EventHandler<bool>? Pressed;

    public bool IsOn => _registered.Count > 0;

    /// <summary>Takes the keys (true when Page Down and Page Up could both be taken).</summary>
    public bool Start(bool arrows)
    {
        Stop();
        bool pages = true;
        for (int i = 0; i < Keys.Length; i++)
        {
            if (Keys[i].Arrow && !arrows) continue;
            int id = FirstId + i;
            if (PInvoke.RegisterHotKey((HWND)Handle, id, HOT_KEY_MODIFIERS.MOD_NOREPEAT, (uint)Keys[i].Key)) _registered.Add(id);
            else if (!Keys[i].Arrow) pages = false;
        }
        return pages;
    }

    /// <summary>Gives the keys back.</summary>
    public void Stop()
    {
        foreach (int id in _registered) PInvoke.UnregisterHotKey((HWND)Handle, id);
        _registered.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            int index = (int)m.WParam - FirstId;
            if (index >= 0 && index < Keys.Length) Pressed?.Invoke(this, Keys[index].Forward);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Stop();
        DestroyHandle();
    }
}
