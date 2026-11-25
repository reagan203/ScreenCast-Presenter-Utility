using Microsoft.Win32;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Windows 11 hides a new tray icon behind the ^ arrow, where people new to SCP never find it.
/// This is the same switch as Settings › Personalisation › Taskbar › Other system tray icons
/// (also used by tools/install.ps1). Windows creates the entry only after the icon has been shown
/// once, and Windows 10 keeps no such entry at all; then nothing is changed and the first-run
/// picture explains where the icon is. The root is passed in so tests use a throwaway key.
/// </summary>
internal static class TrayIconPin
{
    public const string SettingsKey = @"Control Panel\NotifyIconSettings";

    /// <summary>True: shown on the taskbar. False: hidden behind ^. Null: Windows has no entry for this exe.</summary>
    public static bool? IsPromoted(RegistryKey root, string exePath)
    {
        try
        {
            foreach ((RegistryKey key, _) in Entries(root, exePath, writable: false))
            {
                using (key) return key.GetValue("IsPromoted") is int promoted && promoted != 0;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return null;
    }

    /// <summary>Shows the icon on the taskbar. Returns false when Windows has no entry for it yet (or refused).</summary>
    public static bool Promote(RegistryKey root, string exePath)
    {
        bool done = false;
        try
        {
            foreach ((RegistryKey key, _) in Entries(root, exePath, writable: true))
            {
                using (key)
                {
                    key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    done = true;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
        return done;
    }

    private static IEnumerable<(RegistryKey Key, string Name)> Entries(RegistryKey root, string exePath, bool writable)
    {
        using RegistryKey? list = root.OpenSubKey(SettingsKey);
        if (list is null) yield break;
        foreach (string name in list.GetSubKeyNames())
        {
            RegistryKey? key = list.OpenSubKey(name, writable);
            if (key is null) continue;
            if (key.GetValue("ExecutablePath") is string path && string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                yield return (key, name);
            else key.Dispose();
        }
    }
}
