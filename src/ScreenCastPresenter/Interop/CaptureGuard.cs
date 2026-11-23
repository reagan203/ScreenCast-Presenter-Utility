using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// Privacy safety net (SRS 4.5): every top-level window this process creates is excluded from
/// screen capture, except output windows. Our own forms already do this in OnHandleCreated; the
/// guard catches windows the UI framework creates by itself (menus, tooltips, drop-down lists),
/// so none of them can leak into Zoom, a recording or a whole-screen source.
/// </summary>
internal sealed class CaptureGuard : IDisposable
{
    private static readonly HashSet<nint> AllowedHandles = [];

    private readonly WINEVENTPROC _callback;
    private readonly UnhookWinEventSafeHandle _hook;

    public CaptureGuard()
    {
        _callback = OnWinEvent;
        // Out-of-context hook on our own process: callbacks arrive on this (UI) thread.
        _hook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_CREATE, PInvoke.EVENT_OBJECT_SHOW, default, _callback,
            (uint)Environment.ProcessId, 0, PInvoke.WINEVENT_OUTOFCONTEXT);
        ProtectAll(); // windows created before the guard existed
    }

    /// <summary>Output windows must stay capturable (FR-14): Zoom shares them.</summary>
    public static void AllowCapture(nint hwnd) => AllowedHandles.Add(hwnd);

    public static void Forget(nint hwnd) => AllowedHandles.Remove(hwnd);

    public static bool IsAllowed(nint hwnd) => AllowedHandles.Contains(hwnd);

    /// <summary>Applies the exclusion to every current top-level window of this process.</summary>
    public static void ProtectAll()
    {
        foreach (HWND hwnd in OwnTopLevelWindows())
            Protect(hwnd);
    }

    /// <summary>
    /// Audit for tests and the stress harness: top-level windows of this process that are
    /// visible, not allowed, and NOT excluded from capture. Must always be empty.
    /// </summary>
    public static List<nint> FindLeaks()
    {
        var leaks = new List<nint>();
        foreach (HWND hwnd in OwnTopLevelWindows())
        {
            if (!PInvoke.IsWindowVisible(hwnd) || IsAllowed(hwnd)) continue;
            if (!DwmHelpers.IsExcludedFromCapture(hwnd)) leaks.Add(hwnd);
        }
        return leaks;
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != (int)PInvoke.CHILDID_SELF || hwnd.IsNull) return;
        if (PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != hwnd) return; // child controls inherit their top-level window's setting
        Protect(hwnd);
    }

    private static void Protect(HWND hwnd)
    {
        if (IsAllowed(hwnd)) return;
        DwmHelpers.ExcludeFromCapture(hwnd); // fails harmlessly for message-only windows
    }

    private static List<HWND> OwnTopLevelWindows()
    {
        var result = new List<HWND>();
        uint pid = (uint)Environment.ProcessId;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            PInvoke.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid) result.Add(hwnd);
            return true;
        }, default);
        return result;
    }

    public void Dispose() => _hook.Dispose();
}
