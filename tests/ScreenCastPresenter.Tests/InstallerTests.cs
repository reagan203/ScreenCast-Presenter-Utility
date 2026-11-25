using Microsoft.Win32;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// "Add to the Start menu" and uninstall, in temporary folders under a throwaway HKCU key: never
/// the real Start menu, desktop, Apps list or Run key. Includes paths with spaces, accents and
/// Japanese, a locked installed copy, and leftovers that must survive an uninstall.
/// </summary>
public sealed class InstallerTests : IDisposable
{
    private readonly string _keyPath = @"Software\ScreenCastPresenter.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "scp-install-tests", Guid.NewGuid().ToString("N"), "Présentation 発表 ünd spaces");
    private readonly InstallLocations _places;
    private readonly FakeShortcuts _shortcuts = new();
    private readonly string _download;

    public InstallerTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_keyPath);
        _places = new InstallLocations(Path.Combine(_temp, "Programs", "ScreenCast Presenter"), Path.Combine(_temp, "Start Menu"), Path.Combine(_temp, "Desktop"));
        Directory.CreateDirectory(_places.DesktopDir);
        string downloads = Path.Combine(_temp, "Downloads");
        Directory.CreateDirectory(downloads);
        _download = Path.Combine(downloads, InstallLocations.ExeName);
        File.WriteAllBytes(_download, [1, 2, 3, 4]);
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_temp)!)!, recursive: true); }
        catch (IOException) { }
    }

    private Installer New() => new(_root, _places, _shortcuts);

    private sealed class FakeShortcuts : IShortcutWriter
    {
        public List<(string Link, string Target)> Written { get; } = [];

        public void Write(string linkPath, string targetExe, string description)
        {
            Written.Add((linkPath, targetExe));
            File.WriteAllText(linkPath, targetExe);
        }
    }

    [Fact]
    public void Only_a_single_file_exe_called_ScreenCastPresenter_is_offered_for_installing()
    {
        Assert.True(Installer.CanInstallFrom(@"C:\Users\a\Downloads\ScreenCastPresenter.exe", isSingleFile: true));
        Assert.True(Installer.CanInstallFrom(@"C:\Users\a\Downloads\screencastpresenter.EXE", isSingleFile: true));
        Assert.False(Installer.CanInstallFrom(@"C:\repo\bin\Debug\ScreenCastPresenter.exe", isSingleFile: false), "a development build needs its DLLs");
        Assert.False(Installer.CanInstallFrom(@"C:\Program Files\dotnet\dotnet.exe", isSingleFile: true), "run through dotnet.exe");
        Assert.False(Installer.CanInstallFrom(null, isSingleFile: true));
        Assert.False(Installer.CanInstallFrom("", isSingleFile: true));
    }

    [Fact]
    public void A_build_with_its_dll_next_to_it_is_not_single_file()
    {
        Assert.True(Installer.IsSingleFile(_download));
        File.WriteAllBytes(Path.ChangeExtension(_download, ".dll"), [0]);
        Assert.False(Installer.IsSingleFile(_download));
        Assert.False(Installer.IsSingleFile(null));
    }

    [Fact]
    public void Install_copies_the_exe_adds_shortcuts_and_lists_it_in_Apps()
    {
        Installer installer = New();
        Assert.False(installer.IsInstalled);
        Assert.True(installer.InstalledCopyDiffers(_download));

        InstallResult result = installer.Install(_download, "1.2.3");

        Assert.True(result.Done, result.Problem);
        Assert.True(installer.IsInstalled);
        Assert.Equal(File.ReadAllBytes(_download), File.ReadAllBytes(_places.InstalledExe));
        Assert.False(File.Exists(_places.InstalledExe + ".new"), "no half-copied file left behind");
        Assert.Contains((_places.StartMenuLink, _places.InstalledExe), _shortcuts.Written);
        Assert.Contains((_places.DesktopLink, _places.InstalledExe), _shortcuts.Written);
        Assert.True(installer.IsListedInApps);
        Assert.False(installer.InstalledCopyDiffers(_download), "the same build is installed now");
        Assert.True(installer.IsInstalledCopy(_places.InstalledExe));
        Assert.True(installer.IsInstalledCopy(_places.InstalledExe.ToUpperInvariant()), "paths compare without case");
        Assert.False(installer.IsInstalledCopy(_download));

        using RegistryKey key = _root.OpenSubKey(Installer.UninstallKey)!;
        Assert.Equal("ScreenCast Presenter", key.GetValue("DisplayName"));
        Assert.Equal("1.2.3", key.GetValue("DisplayVersion"));
        Assert.Equal($"\"{_places.InstalledExe}\" --uninstall", key.GetValue("UninstallString"));
        Assert.Equal(_places.InstallDir, key.GetValue("InstallLocation"));
        Assert.Equal(1, key.GetValue("NoModify"));
    }

    [Fact]
    public void A_different_download_can_replace_the_installed_copy()
    {
        Installer installer = New();
        Assert.True(installer.Install(_download, "1.0.0").Done);
        File.WriteAllBytes(_download, [9, 9, 9, 9, 9, 9]);
        Assert.True(installer.InstalledCopyDiffers(_download));
        Assert.True(installer.Install(_download, "1.1.0").Done);
        Assert.Equal([9, 9, 9, 9, 9, 9], File.ReadAllBytes(_places.InstalledExe));
    }

    [Fact]
    public void Installing_from_the_installed_copy_only_refreshes_the_shortcuts()
    {
        Installer installer = New();
        Assert.True(installer.Install(_download, "1.0.0").Done);
        _shortcuts.Written.Clear();
        Assert.True(installer.Install(_places.InstalledExe, "1.0.0").Done);
        Assert.Equal(2, _shortcuts.Written.Count);
    }

    [Fact]
    public void A_missing_desktop_folder_is_skipped_not_created()
    {
        Directory.Delete(_places.DesktopDir);
        Assert.True(New().Install(_download, "1.0.0").Done);
        Assert.DoesNotContain(_shortcuts.Written, w => w.Link == _places.DesktopLink);
        Assert.False(Directory.Exists(_places.DesktopDir));
    }

    [Fact]
    public void An_installed_copy_in_use_is_reported_plainly_and_left_intact()
    {
        Installer installer = New();
        Assert.True(installer.Install(_download, "1.0.0").Done);
        File.WriteAllBytes(_download, [5, 5, 5, 5, 5]);
        using (new FileStream(_places.InstalledExe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            InstallResult locked = installer.Install(_download, "1.1.0");
            Assert.False(locked.Done);
            Assert.Contains("in use", locked.Problem);
        }
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(_places.InstalledExe));
        Assert.False(File.Exists(_places.InstalledExe + ".new"));
    }

    [Fact]
    public void Start_with_Windows_follows_the_copy_into_the_Start_menu()
    {
        var fromDownloads = new StartupRegistration(_root, _download);
        fromDownloads.Enable();
        Assert.True(New().Install(_download, "1.0.0").Done);
        using RegistryKey run = _root.OpenSubKey(StartupRegistration.RunKey)!;
        Assert.Equal($"\"{_places.InstalledExe}\" --startup", run.GetValue(StartupRegistration.ValueName));
    }

    [Fact]
    public void Installing_doesnt_switch_on_start_with_Windows()
    {
        Assert.True(New().Install(_download, "1.0.0").Done);
        Assert.False(new StartupRegistration(_root, _places.InstalledExe).IsRegistered);
    }

    [Fact]
    public void Uninstall_removes_shortcuts_Apps_entry_and_startup_but_keeps_other_files()
    {
        Installer installer = New();
        Assert.True(installer.Install(_download, "1.0.0").Done);
        new StartupRegistration(_root, _places.InstalledExe).Enable();
        string keep = Path.Combine(_places.InstallDir, "notes.txt");
        File.WriteAllText(keep, "mine");

        installer.Uninstall();

        Assert.False(File.Exists(_places.StartMenuLink));
        Assert.False(File.Exists(_places.DesktopLink));
        Assert.False(installer.IsListedInApps);
        Assert.False(new StartupRegistration(_root, _places.InstalledExe).IsRegistered);
        Assert.True(File.Exists(keep));
        installer.Uninstall(); // twice is fine

        string cleanup = installer.CleanupCommand();
        Assert.Contains($"del /f /q \"{_places.InstalledExe}\"", cleanup);
        Assert.Contains($"rmdir \"{_places.InstallDir}\"", cleanup);
        Assert.DoesNotContain("/s", cleanup.Replace("/d /c", "", StringComparison.Ordinal)); // never deletes a folder's contents
    }

    [Fact]
    public void The_real_shell_writes_a_shortcut_even_on_an_accented_Japanese_path()
    {
        string link = Path.Combine(_temp, "Start Menu", "ScreenCast Presenter.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        new ShellLinkWriter().Write(link, _download, Installer.Description);
        var file = new FileInfo(link);
        Assert.True(file.Exists);
        Assert.True(file.Length > 0);
        new ShellLinkWriter().Write(link, _download, Installer.Description); // replacing an existing one is fine
    }

    [Fact]
    public void Install_problems_are_explained_in_plain_words()
    {
        Assert.Contains("in use", Installer.Explain(new IOException("x", unchecked((int)0x80070020))));
        Assert.Contains("disk space", Installer.Explain(new IOException("x", unchecked((int)0x80070070))));
        Assert.Contains("didn't allow", Installer.Explain(new UnauthorizedAccessException()));
        Assert.DoesNotContain("0x", Installer.Explain(new IOException("HRESULT 0x80004005")));
    }

    // ───────────────────────────── the tray icon by the clock ─────────────────────────────

    private string AddTrayEntry(string exe, int? promoted)
    {
        string name = Guid.NewGuid().ToString("N");
        using RegistryKey key = _root.CreateSubKey(TrayIconPin.SettingsKey + "\\" + name);
        key.SetValue("ExecutablePath", exe);
        if (promoted is { } p) key.SetValue("IsPromoted", p, RegistryValueKind.DWord);
        return name;
    }

    [Fact]
    public void Tray_icon_without_a_Windows_entry_is_unknown_and_cant_be_pinned()
    {
        Assert.Null(TrayIconPin.IsPromoted(_root, _download)); // Windows 10, or not shown yet
        Assert.False(TrayIconPin.Promote(_root, _download));
        AddTrayEntry(@"C:\Other\app.exe", 0);
        Assert.Null(TrayIconPin.IsPromoted(_root, _download));
    }

    [Fact]
    public void Tray_icon_hidden_behind_the_arrow_can_be_pinned_and_others_are_untouched()
    {
        string other = AddTrayEntry(@"C:\Other\app.exe", 0);
        AddTrayEntry(_download.ToUpperInvariant(), null); // Windows' entry: no IsPromoted value = hidden
        Assert.False(TrayIconPin.IsPromoted(_root, _download));

        Assert.True(TrayIconPin.Promote(_root, _download));
        Assert.True(TrayIconPin.IsPromoted(_root, _download));
        using RegistryKey otherKey = _root.OpenSubKey(TrayIconPin.SettingsKey + "\\" + other)!;
        Assert.Equal(0, otherKey.GetValue("IsPromoted"));
    }

    // ───────────────────────────── dialogs ─────────────────────────────

    [Fact]
    public void Confirmation_dialog_is_private_and_keyboard_friendly()
    {
        using var form = new ConfirmForm("Remove ScreenCast Presenter?", "Your recordings are kept.", "Remove it", "Cancel");
        form.CreateControl();
        Assert.True(DwmHelpers.IsExcludedFromCapture((HWND)form.Handle), "never in a screen share (SRS 4.5)");
        Assert.True(DwmHelpers.IsCloaked((HWND)form.Handle), "no white frame before the first paint (NFR-10)");
        Assert.NotNull(form.AcceptButton);
        Assert.NotNull(form.CancelButton);
        Assert.NotSame(form.AcceptButton, form.CancelButton);
        Assert.Equal("Remove it", ((Control)form.AcceptButton!).Text);
    }

    [Fact]
    public void A_notice_has_one_button_that_Esc_also_presses()
    {
        using var form = new ConfirmForm("Still busy", "Try again in a minute.", "OK", null);
        form.CreateControl();
        Assert.Same(form.AcceptButton, form.CancelButton);
    }
}
