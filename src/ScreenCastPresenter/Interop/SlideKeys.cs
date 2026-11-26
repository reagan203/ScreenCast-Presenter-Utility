using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// Next and previous slide (FR-90), like a presenter clicker: Page Down and Page Up, which
/// PowerPoint, Google Slides, Keynote in a browser and PDF readers all take. When what's shown is
/// the active window the keys are typed into it; otherwise they're posted to it directly, so the
/// presenter's own work keeps the focus.
/// </summary>
internal static class SlideKeys
{
    public const VIRTUAL_KEY Next = VIRTUAL_KEY.VK_NEXT, Previous = VIRTUAL_KEY.VK_PRIOR;

    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101;

    /// <summary>True when the key went somewhere.</summary>
    public static bool Send(nint window, bool forward)
    {
        if (window == 0 || !PInvoke.IsWindow((HWND)window)) return false;
        VIRTUAL_KEY key = forward ? Next : Previous;
        if (PInvoke.GetForegroundWindow() == (HWND)window) return Type(key);
        // Posted: the scan code and "extended key" bit as a real key press has them.
        uint scan = PInvoke.MapVirtualKey((uint)key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        nint down = (nint)(1 | (scan << 16) | (1 << 24)), up = (nint)(down | unchecked((int)0xC0000000));
        return PInvoke.PostMessage((HWND)window, WmKeyDown, (nuint)key, down) && PInvoke.PostMessage((HWND)window, WmKeyUp, (nuint)key, up);
    }

    private static unsafe bool Type(VIRTUAL_KEY key)
    {
        INPUT* inputs = stackalloc INPUT[2];
        inputs[0] = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        inputs[0].Anonymous.ki = new KEYBDINPUT { wVk = key, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY };
        inputs[1] = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        inputs[1].Anonymous.ki = new KEYBDINPUT { wVk = key, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP };
        return PInvoke.SendInput(2, inputs, sizeof(INPUT)) == 2;
    }
}
