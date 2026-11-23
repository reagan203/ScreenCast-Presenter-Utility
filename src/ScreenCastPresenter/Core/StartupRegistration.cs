using Microsoft.Win32;

namespace ScreenCastPresenter.Core;

/// <summary>
/// FR-42 "Start with Windows": a value in the current user's Run key (no admin rights, no
/// scheduled task). The registry is the single source of truth, so turning it off in Task
/// Manager's Startup apps is respected and shown rather than overridden. The root key is passed
/// in so tests use a throwaway key instead of the user's real one.
/// </summary>
internal sealed class StartupRegistration(RegistryKey root, string exePath)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "ScreenCast Presenter";

    /// <summary>Started by Windows at sign-in: no welcome message.</summary>
    public const string StartupSwitch = "--startup";

    /// <summary>The command Windows runs at sign-in.</summary>
    public string Command => $"\"{exePath}\" {StartupSwitch}";

    /// <summary>SCP is registered to start (whatever Task Manager says).</summary>
    public bool IsRegistered
    {
        get
        {
            using RegistryKey? run = root.OpenSubKey(RunKey);
            return run?.GetValue(ValueName) is string;
        }
    }

    /// <summary>
    /// Registered, but switched off in Task Manager › Startup apps (Explorer keeps that in
    /// StartupApproved: an odd first byte means disabled).
    /// </summary>
    public bool DisabledInTaskManager
    {
        get
        {
            using RegistryKey? approved = root.OpenSubKey(ApprovedKey);
            return IsRegistered && approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
        }
    }

    /// <summary>Starts at sign-in for real: registered and not switched off in Task Manager.</summary>
    public bool IsEnabled => IsRegistered && !DisabledInTaskManager;

    public void Enable()
    {
        using RegistryKey run = root.CreateSubKey(RunKey);
        run.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using RegistryKey? run = root.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// SCP is portable: if the exe was moved since it was registered, point the entry at the new
    /// location so sign-in doesn't silently fail. Returns true when it was updated.
    /// </summary>
    public bool RepairPath()
    {
        using RegistryKey? run = root.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(ValueName) is not string current || current == Command) return false;
        run.SetValue(ValueName, Command, RegistryValueKind.String);
        return true;
    }
}
