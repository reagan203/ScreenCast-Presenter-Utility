using System.Runtime.InteropServices;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Windows' pop-up notifications appear on the main screen, so they are part of a whole main
/// screen being shown or recorded. Windows lets no ordinary app switch on Do not disturb (starting
/// a focus session needs a token from Microsoft), so SCP reminds the presenter once, with a button
/// that opens the setting, and only when Windows says pop-ups would show right now.
/// </summary>
internal static partial class NotificationReminder
{
    /// <summary>QUNS_ACCEPTS_NOTIFICATIONS: nothing (Do not disturb, presentation mode, a full-screen app) holds them back.</summary>
    private const int AcceptsNotifications = 5;

    /// <summary>Remind now? Once per run, when it's on, pop-ups would show, and the audience sees the main screen.</summary>
    public static bool ShouldRemind(bool enabled, bool alreadyReminded, bool popUpsWouldShow, bool showsMainScreen, bool privatePreview) =>
        enabled && !alreadyReminded && popUpsWouldShow && showsMainScreen && !privatePreview;

    /// <summary>Where Windows keeps Do not disturb: Settings › Notifications (Windows 11), Focus assist (Windows 10).</summary>
    public static string SettingsUri(bool windows11) => windows11 ? "ms-settings:notifications" : "ms-settings:quiethours";

    /// <summary>Windows would show a pop-up notification now. False when it can't tell (then there's nothing to remind about).</summary>
    public static bool PopUpsWouldShow()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 && state == AcceptsNotifications;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
