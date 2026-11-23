using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Registers global shortcuts (FR-21) on a hidden message-only window and reports which ones
/// Windows refused because another app owns them (FR-21a). Pressing a shortcut never moves
/// focus, so the presented app doesn't react.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HwndMessage = -3; // HWND_MESSAGE: invisible, never in any window list

    private readonly MessageWindow _window;
    private readonly Dictionary<int, string> _idToAction = [];
    private int _nextId = 1;

    public HotkeyManager()
    {
        _window = new MessageWindow(this);
        _window.CreateHandle(new CreateParams { Parent = HwndMessage, Caption = "SCP hotkeys" });
    }

    /// <summary>Action id of the pressed shortcut.</summary>
    public event EventHandler<string>? Pressed;

    public IReadOnlyList<ShortcutBinding> Bindings { get; private set; } = [];

    /// <summary>Re-registers everything from scratch according to <paramref name="resolved"/>.</summary>
    public IReadOnlyList<ShortcutBinding> Apply(IReadOnlyList<ShortcutBinding> resolved)
    {
        UnregisterAll();
        var result = new List<ShortcutBinding>(resolved.Count);
        foreach (ShortcutBinding binding in resolved)
        {
            if (binding.Status != ShortcutStatus.Pending || binding.Hotkey is not { } hotkey)
            {
                result.Add(binding);
                continue;
            }

            if (!Register(hotkey, binding.Action.Id))
            {
                result.Add(binding with { Status = ShortcutStatus.Taken, Note = "Used by another app. Pick different keys." });
                continue;
            }
            // Number-pad digits as a silent extra; fine if another app already has them.
            if (hotkey.NumpadAlias is { } alias) Register(alias, binding.Action.Id);
            result.Add(binding with { Status = ShortcutStatus.Active });
        }
        return Bindings = result;
    }

    private bool Register(Hotkey hotkey, string actionId)
    {
        int id = _nextId++;
        // MOD_NOREPEAT: holding the keys down fires once, not a stream of repeats.
        var modifiers = (HOT_KEY_MODIFIERS)(uint)hotkey.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (!PInvoke.RegisterHotKey((HWND)_window.Handle, id, modifiers, (uint)hotkey.Key)) return false;
        _idToAction[id] = actionId;
        return true;
    }

    private void UnregisterAll()
    {
        foreach (int id in _idToAction.Keys) PInvoke.UnregisterHotKey((HWND)_window.Handle, id);
        _idToAction.Clear();
        _nextId = 1;
    }

    private void OnHotkey(int id)
    {
        if (_idToAction.TryGetValue(id, out string? action)) Pressed?.Invoke(this, action);
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.DestroyHandle();
    }

    private sealed class MessageWindow(HotkeyManager owner) : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)PInvoke.WM_HOTKEY) owner.OnHotkey((int)m.WParam);
            base.WndProc(ref m);
        }
    }
}

/// <summary>
/// Detects shortcuts that would type a character on one of the user's keyboard layouts. On AltGr
/// layouts Ctrl+Alt acts as AltGr (e.g. Ctrl+Alt+4 = € on UK, Ctrl+Alt+7 = { on German), and a
/// global shortcut there would stop the user typing that character anywhere (FR-21a).
/// </summary>
internal static class KeyboardLayoutProbe
{
    private const byte KeyDown = 0x80;
    private const uint DontChangeKeyboardState = 0x4; // ToUnicodeEx flag (Windows 10 1607+): keeps dead-key state intact

    public static unsafe bool TypesCharacter(Hotkey hotkey)
    {
        int count = PInvoke.GetKeyboardLayoutList(0, null);
        if (count <= 0) return false;
        var layouts = new HKL[count];
        fixed (HKL* p = layouts) count = PInvoke.GetKeyboardLayoutList(count, p);

        Span<byte> state = stackalloc byte[256];
        state.Clear();
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) state[(int)Keys.ControlKey] = state[(int)Keys.LControlKey] = KeyDown;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) state[(int)Keys.Menu] = state[(int)Keys.RMenu] = KeyDown;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) state[(int)Keys.ShiftKey] = state[(int)Keys.LShiftKey] = KeyDown;

        Span<char> buffer = stackalloc char[8];
        for (int i = 0; i < count; i++)
        {
            HKL layout = layouts[i];
            uint scanCode = PInvoke.MapVirtualKeyEx((uint)hotkey.Key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC, layout);
            int result;
            fixed (byte* s = state)
            fixed (char* b = buffer)
                result = PInvoke.ToUnicodeEx((uint)hotkey.Key, scanCode, s, b, buffer.Length, DontChangeKeyboardState, layout);

            if (result < 0) return true; // a dead key (accent) on this layout
            for (int c = 0; c < result; c++)
            {
                // Control characters (Ctrl+letter) and whitespace aren't "typing a character".
                if (!char.IsControl(buffer[c]) && !char.IsWhiteSpace(buffer[c])) return true;
            }
        }
        return false;
    }
}
