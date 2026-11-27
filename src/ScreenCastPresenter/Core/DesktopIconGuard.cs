namespace ScreenCastPresenter.Core;

/// <summary>What the desktop icon guard needs from Windows (the real one is Interop.DesktopIcons; tests use a fake).</summary>
internal interface IDesktopIconPlatform
{
    /// <summary>The desktop's icon list window, or 0 when there's none (Explorer not running, or restarting).</summary>
    nint FindList();

    bool IsVisible(nint list);

    void SetVisible(nint list, bool visible);
}

/// <summary>
/// Hide desktop icons (FR-113): while a whole screen is shown, the desktop's icons (file names,
/// folders, shortcuts) are hidden, and they come back as they were when it stops. Only icons this
/// guard hid are shown again: icons the presenter hid themselves (Explorer's "Show desktop icons"
/// switched off) are left alone. Explorer restarting brings a new, visible list: it's hidden again
/// on the next check. That the icons are hidden is remembered (<paramref name="remember"/>) so that
/// after a crash the next start shows them again.
/// </summary>
internal sealed class DesktopIconGuard(IDesktopIconPlatform platform, Action<bool> remember)
{
    private nint _hidden;
    private bool _wanted;

    /// <summary>The icons are hidden by this guard now.</summary>
    public bool Hiding => _hidden != 0;

    /// <summary>Hide the icons (a whole screen is shown) or bring them back. Safe to call as often as needed.</summary>
    public void Update(bool hide)
    {
        _wanted = hide;
        if (hide) HideNow();
        else ShowAgain();
    }

    /// <summary>Called now and then while hidden: a restarted Explorer's new list is hidden too.</summary>
    public void Check()
    {
        if (_wanted) HideNow();
    }

    private void HideNow()
    {
        nint list = platform.FindList();
        if (list == 0) return; // no desktop right now: looked for again on the next check
        if (list == _hidden) return; // already hidden by us
        if (!platform.IsVisible(list))
        {
            // Hidden already, and not by us: the presenter switched desktop icons off. Leave them.
            _hidden = 0;
            return;
        }
        platform.SetVisible(list, false);
        bool first = _hidden == 0;
        _hidden = list;
        if (first) remember(true);
    }

    private void ShowAgain()
    {
        if (_hidden == 0) return;
        nint list = platform.FindList();
        // The same list we hid (or Explorer's new one, which starts as the user left it): show it if hidden.
        if (list != 0 && list == _hidden && !platform.IsVisible(list)) platform.SetVisible(list, true);
        _hidden = 0;
        remember(false);
    }

    /// <summary>
    /// At start: the last run hid the icons and didn't get to show them again (it crashed or was
    /// ended). Show them now. True when that was needed.
    /// </summary>
    public static bool RepairAfterCrash(IDesktopIconPlatform platform, bool hiddenLastRun)
    {
        if (!hiddenLastRun) return false;
        nint list = platform.FindList();
        if (list == 0 || platform.IsVisible(list)) return false;
        platform.SetVisible(list, true);
        return true;
    }
}
