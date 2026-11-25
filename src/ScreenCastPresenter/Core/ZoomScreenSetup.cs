using System.Diagnostics;

namespace ScreenCastPresenter.Core;

/// <summary>What happened when setting up the Zoom screen, in words for the presenter.</summary>
internal enum ZoomSetupOutcome { Installed, AlreadyInstalled, Cancelled, Failed, NoWinget }

/// <summary>
/// "Set up a Zoom screen" (FR-12, SRS 2.5): installs the free Virtual Display Driver (MIT) with
/// Windows' own package manager, the same command as SRS Appendix B, so a non-technical user never
/// types it. Windows shows its own permission (admin) prompt for the driver. SCP itself still makes
/// no network connections: winget downloads the driver, and only when the user asks for it.
/// </summary>
internal static class ZoomScreenSetup
{
    public const string PackageId = "VirtualDrivers.Virtual-Display-Driver";
    public const string ProjectPage = "https://github.com/VirtualDrivers/Virtual-Display-Driver";

    /// <summary>winget's exit code for "already installed".</summary>
    public const int AlreadyInstalledCode = unchecked((int)0x8A150061);

    /// <summary>
    /// winget.exe, or null when it isn't there (older Windows 10 without App Installer). It is an
    /// app execution alias in the user's WindowsApps folder.
    /// </summary>
    public static string? FindWinget(Func<string, bool> exists)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        return exists(alias) ? alias : null;
    }

    public static string Arguments =>
        $"install --id {PackageId} --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

    public static ProcessStartInfo StartInfo(string winget) => new(winget, Arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true, // no console flashing up; Windows' permission prompt still appears
    };

    /// <summary>
    /// winget passes the installer's own code through when it fails: 1602 and 1223 are "the user
    /// cancelled" (including saying No to the permission prompt).
    /// </summary>
    public static ZoomSetupOutcome Interpret(int exitCode) => exitCode switch
    {
        0 => ZoomSetupOutcome.Installed,
        AlreadyInstalledCode => ZoomSetupOutcome.AlreadyInstalled,
        1602 or 1223 or unchecked((int)0x800704C7) => ZoomSetupOutcome.Cancelled,
        _ => ZoomSetupOutcome.Failed,
    };
}
