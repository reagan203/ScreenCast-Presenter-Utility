using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// Shortcuts on the TV (FR-101) and zoom that follows typing (FR-99). Both only look at the
/// keyboard and the text cursor while they're on and something is shown; neither installs a
/// keyboard hook, and typed text is never read or shown.
/// </summary>
internal sealed partial class ScpApp
{
    private readonly System.Windows.Forms.Timer _keyTimer = new() { Interval = KeyDisplay.PollMs };
    private readonly System.Windows.Forms.Timer _keyClear = new() { Interval = (int)KeyDisplay.ShownFor.TotalMilliseconds };
    private HashSet<Keys> _keysDown = [];
    private bool _keyTimerHooked;
    private CaretTracker? _caret;
    private Point _lastCursor;
    private long _pointerMovedAt;

    /// <summary>Settings › On the TV, the tray and "--on show-keys": shortcuts pressed in what's shown appear on the TV.</summary>
    public void SetShowKeys(bool on)
    {
        Settings.ShowKeys = on;
        Save();
        UpdateKeyWatch();
        _tray.Refresh();
        if (on && _session.State == SessionState.Presenting && _session.Effects is null) _ = GuardAsync(ReshowAsync); // only the helper draws them
        Message(on ? "Shortcuts you press in what's shown appear on the TV, like Ctrl + C. What you type never does." : "Shortcuts no longer show on the TV.", BadgeDot.Accent);
    }

    /// <summary>Settings › On the TV › Zoom follows.</summary>
    public void SetZoomFollows(ZoomFollows follows)
    {
        Settings.ZoomFollowsMode = follows;
        Save();
        UpdateCaretTracker();
        Message($"Zooming in follows {ZoomFollow.Words(follows)}.", BadgeDot.Accent);
    }

    /// <summary>Looks at the keyboard only while shortcuts are shown and something is.</summary>
    private void UpdateKeyWatch()
    {
        if (!_keyTimerHooked)
        {
            _keyTimerHooked = true;
            _keyTimer.Tick += (_, _) => Guard(OnKeyTick);
            _keyClear.Tick += (_, _) => Guard(() =>
            {
                _keyClear.Stop();
                _session.Effects?.KeyCombo(null);
            });
        }
        bool want = !_quitting && Settings.ShowKeys && _session.State == SessionState.Presenting;
        if (want == _keyTimer.Enabled) return;
        if (want)
        {
            _keysDown = KeysDownNow(); // keys already held aren't presses
            _keyTimer.Start();
        }
        else
        {
            _keyTimer.Stop();
            _keyClear.Stop();
            _session.Effects?.KeyCombo(null);
        }
    }

    private static HashSet<Keys> KeysDownNow()
    {
        var down = new HashSet<Keys>();
        foreach (Keys k in KeyDisplay.Watched)
            if ((Windows.Win32.PInvoke.GetAsyncKeyState((int)k) & 0x8000) != 0) down.Add(k);
        return down;
    }

    private static HotkeyModifiers ModifiersNow()
    {
        static bool Down(Keys k) => (Windows.Win32.PInvoke.GetAsyncKeyState((int)k) & 0x8000) != 0;
        HotkeyModifiers m = HotkeyModifiers.None;
        if (Down(Keys.ControlKey)) m |= HotkeyModifiers.Control;
        if (Down(Keys.Menu)) m |= HotkeyModifiers.Alt;
        if (Down(Keys.ShiftKey)) m |= HotkeyModifiers.Shift;
        if (Down(Keys.LWin) || Down(Keys.RWin)) m |= HotkeyModifiers.Win;
        return m;
    }

    private void OnKeyTick()
    {
        HashSet<Keys> now = KeysDownNow();
        List<Keys> pressed = KeyDisplay.NewlyDown(_keysDown, now);
        _keysDown = now;
        if (pressed.Count == 0 || _session.Effects is not { } effects || _session.Source is not { } source) return;
        // Only in what's shown, and only while the TV shows the picture: the presenter's private work stays private.
        if (_session.IsFrozen || _session.IsBlanked || _session.IsHeld || !WorkingInShown(source)) return;
        HotkeyModifiers modifiers = ModifiersNow();
        foreach (Keys key in pressed)
        {
            var hotkey = new Hotkey(modifiers, key);
            if (IsOwnShortcut(hotkey)) continue; // SCP's own keys aren't the lesson
            if (KeyDisplay.Describe(key, modifiers, KeyboardLayoutProbe.TypesCharacter) is not { } words) continue;
            effects.KeyCombo(words);
            _keyClear.Stop();
            _keyClear.Start();
        }
    }

    /// <summary>The presenter is working in what's shown: the window itself, or a window on the screen shown.</summary>
    private bool WorkingInShown(SourceWindow source)
    {
        nint root = WindowEnumerator.ForegroundRoot();
        if (root == 0) return false;
        if (source.IsWindow) return root == source.Handle;
        if (source.Screen is { } screen)
        {
            Rectangle bounds = WindowEnumerator.ClientBoundsOnScreen(root);
            return bounds.Width > 0 && screen.Bounds.Contains(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)
                && WindowEnumerator.GetActiveWindow() is { } active && !IsNeverShown(active);
        }
        return false; // a camera or a picture: the keyboard isn't in it
    }

    private bool IsOwnShortcut(Hotkey hotkey) => _hotkeys.Bindings.Any(b => b.Status == ShortcutStatus.Active && b.Hotkey == hotkey);

    /// <summary>The text cursor is tracked only while zoomed in and following typing.</summary>
    private void UpdateCaretTracker()
    {
        bool want = !_quitting && _zoom > 0 && Settings.ZoomFollowsMode != ZoomFollows.Pointer && _session.State == SessionState.Presenting;
        if (want && _caret is null) _caret = new CaretTracker();
        else if (!want && _caret is not null)
        {
            _caret.Dispose();
            _caret = null;
        }
    }

    /// <summary>Where the zoomed view goes now (FR-99), or null to stay.</summary>
    private PointF? ZoomTarget(SourceWindow source, (PointF At, bool Over)? pointer)
    {
        long now = Environment.TickCount64;
        Point cursor = Cursor.Position;
        if (cursor != _lastCursor)
        {
            _lastCursor = cursor;
            _pointerMovedAt = now;
        }
        PointF? at = pointer is { Over: true } p ? p.At : null;
        ZoomFollows mode = Settings.ZoomFollowsMode;
        if (mode == ZoomFollows.Pointer) return at;
        UpdateCaretTracker();
        PointF? caret = null;
        long caretMovedAt = 0;
        if (_caret?.Latest is { } c && !source.IsOffScreen)
        {
            Rectangle area = source.Screen is { } screen ? screen.Bounds
                : c.Window == source.Handle ? WindowEnumerator.ClientBoundsOnScreen(source.Handle) : Rectangle.Empty;
            caret = ZoomFollow.On(c.Caret, area);
            caretMovedAt = c.MovedAt;
        }
        return ZoomFollow.Choose(mode, at, TimeSpan.FromMilliseconds(now - _pointerMovedAt), caret, TimeSpan.FromMilliseconds(now - caretMovedAt));
    }
}
