using Microsoft.Win32;

namespace ScreenCastPresenter.Core;

/// <summary>Creates a .lnk file; the real one uses the Windows shell, tests use a fake.</summary>
internal interface IShortcutWriter
{
    void Write(string linkPath, string targetExe, string description);
}

/// <summary>Where things go. <see cref="ForCurrentUser"/> is the real set; tests pass temporary folders.</summary>
internal sealed record InstallLocations(string InstallDir, string StartMenuDir, string DesktopDir)
{
    public const string ExeName = "ScreenCastPresenter.exe";
    public const string LinkName = "ScreenCast Presenter.lnk";

    public string InstalledExe => Path.Combine(InstallDir, ExeName);
    public string StartMenuLink => Path.Combine(StartMenuDir, LinkName);
    public string DesktopLink => Path.Combine(DesktopDir, LinkName);

    /// <summary>The same places as tools/install.ps1, so either one updates the other's copy.</summary>
    public static InstallLocations ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ScreenCast Presenter"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
}

internal sealed record InstallResult(bool Done, string? Problem)
{
    public static InstallResult Ok { get; } = new(true, null);
}

/// <summary>
/// "Add to the Start menu" for people who downloaded the exe and double-clicked it: copies it to
/// %LOCALAPPDATA%\Programs\ScreenCast Presenter (no admin rights), adds Start menu and desktop
/// shortcuts, and lists it in Settings › Apps so it can be removed the usual way. Uninstall undoes
/// all of that; recordings, screenshots and settings are kept. The registry root and folders are
/// passed in so tests never touch the real ones.
/// </summary>
internal sealed class Installer(RegistryKey root, InstallLocations places, IShortcutWriter shortcuts)
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenCastPresenter";
    public const string UninstallSwitch = "--uninstall";
    public const string Description = "Show one window, screen or region on a TV, a Zoom screen or a window, privately.";

    public InstallLocations Places => places;

    /// <summary>
    /// Only a published single-file exe can be copied on its own. A development build (an exe next
    /// to its DLLs) or "dotnet app.dll" can't, so it is never offered there.
    /// </summary>
    public static bool CanInstallFrom(string? exePath, bool isSingleFile) =>
        isSingleFile && exePath is { Length: > 0 } && string.Equals(Path.GetFileName(exePath), InstallLocations.ExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A published single-file exe has no DLL of the same name next to it; a development build does.
    /// (Assembly.Location would say so too, but it is flagged as a single-file hazard at publish.)
    /// </summary>
    public static bool IsSingleFile(string? exePath) =>
        exePath is { Length: > 0 } && !File.Exists(Path.ChangeExtension(exePath, ".dll"));

    /// <summary>
    /// The installed copy is missing or isn't this build (a newer or older download), so adding or
    /// replacing it makes sense.
    /// </summary>
    public bool InstalledCopyDiffers(string exePath)
    {
        if (!IsInstalled) return true;
        try
        {
            var mine = new FileInfo(exePath);
            var theirs = new FileInfo(places.InstalledExe);
            return mine.Length != theirs.Length
                || System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileVersion != System.Diagnostics.FileVersionInfo.GetVersionInfo(places.InstalledExe).FileVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return true;
        }
    }

    /// <summary>This exe is the installed copy.</summary>
    public bool IsInstalledCopy(string? exePath) => exePath is { Length: > 0 } && SamePath(exePath, places.InstalledExe);

    /// <summary>An installed copy exists (from here or from tools/install.ps1).</summary>
    public bool IsInstalled => File.Exists(places.InstalledExe);

    public bool IsListedInApps
    {
        get
        {
            using RegistryKey? key = root.OpenSubKey(UninstallKey);
            return key?.GetValue("UninstallString") is string;
        }
    }

    /// <summary>Copies <paramref name="exePath"/> into place (unless it already is the installed copy) and adds the shortcuts and the Apps entry.</summary>
    public InstallResult Install(string exePath, string version)
    {
        try
        {
            Directory.CreateDirectory(places.InstallDir);
            if (!IsInstalledCopy(exePath))
            {
                // A copy running from there (or its capture helper) holds the file: say so plainly
                // rather than "access denied", which is what replacing a locked file reports.
                if (IsInUse(places.InstalledExe)) return new InstallResult(false, InUse);
                // Copy next to it first, then swap, so a failure never leaves half an exe behind.
                string temp = places.InstalledExe + ".new";
                File.Copy(exePath, temp, overwrite: true);
                File.Move(temp, places.InstalledExe, overwrite: true);
            }
            Directory.CreateDirectory(places.StartMenuDir);
            shortcuts.Write(places.StartMenuLink, places.InstalledExe, Description);
            if (Directory.Exists(places.DesktopDir)) shortcuts.Write(places.DesktopLink, places.InstalledExe, Description);
            Register(version);

            // "Start with Windows" set from the downloaded copy now points at the installed one.
            new StartupRegistration(root, places.InstalledExe).RepairPath();
            return InstallResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            TryDelete(places.InstalledExe + ".new");
            return new InstallResult(false, Explain(ex));
        }
    }

    private const string InUse = "The installed copy is in use. Quit ScreenCast Presenter everywhere, then try again.";

    private static bool IsInUse(string file)
    {
        if (!File.Exists(file)) return false;
        try
        {
            using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true; // sharing violation: open in another process
        }
        catch (UnauthorizedAccessException)
        {
            return false; // read-only or not ours: the copy itself will report it
        }
    }

    /// <summary>Why installing failed, in words the user can act on.</summary>
    public static string Explain(Exception ex) => ex.HResult switch
    {
        unchecked((int)0x80070020) or unchecked((int)0x80070021) => InUse,
        unchecked((int)0x80070070) or unchecked((int)0x80070027) => "There isn't enough free disk space.",
        _ when ex is UnauthorizedAccessException => "Windows didn't allow it. Your organisation may block installing apps.",
        _ => "Something on this PC got in the way. Try again in a moment.",
    };

    private void Register(string version)
    {
        using RegistryKey key = root.CreateSubKey(UninstallKey);
        string exe = places.InstalledExe;
        key.SetValue("DisplayName", "ScreenCast Presenter");
        key.SetValue("DisplayVersion", version);
        key.SetValue("DisplayIcon", $"\"{exe}\",0");
        key.SetValue("InstallLocation", places.InstallDir);
        key.SetValue("UninstallString", $"\"{exe}\" {UninstallSwitch}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        long kb = File.Exists(exe) ? new FileInfo(exe).Length / 1024 : 0;
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
    }

    /// <summary>
    /// Removes the shortcuts, the Apps entry and "start with Windows". The exe itself is still
    /// running, so it is deleted after exit by <see cref="CleanupCommand"/>.
    /// </summary>
    public void Uninstall()
    {
        TryDelete(places.StartMenuLink);
        TryDelete(places.DesktopLink);
        try
        {
            root.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            new StartupRegistration(root, places.InstalledExe).Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// A hidden command that waits a moment (for this process to exit), deletes the installed exe,
    /// and removes its folder only if it is then empty (anything else in it is left alone).
    /// </summary>
    public string CleanupCommand() =>
        $"/d /c ping 127.0.0.1 -n 4 >nul & del /f /q \"{places.InstalledExe}\" & rmdir \"{places.InstallDir}\"";

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
