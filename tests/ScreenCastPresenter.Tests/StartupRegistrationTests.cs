using Microsoft.Win32;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>Uses a throwaway key under HKCU\Software, never the real Run key.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _keyPath = @"Software\ScreenCastPresenter.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public StartupRegistrationTests() => _root = Registry.CurrentUser.CreateSubKey(_keyPath);

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Enable_and_disable_write_and_remove_the_run_entry()
    {
        var startup = new StartupRegistration(_root, @"C:\Tools\SCP\ScreenCastPresenter.exe");
        Assert.False(startup.IsEnabled);
        startup.Enable();
        Assert.True(startup.IsEnabled);
        using (RegistryKey run = _root.OpenSubKey(StartupRegistration.RunKey)!)
            Assert.Equal("\"C:\\Tools\\SCP\\ScreenCastPresenter.exe\" --startup", run.GetValue(StartupRegistration.ValueName));
        startup.Disable();
        Assert.False(startup.IsRegistered);
        startup.Disable(); // twice is fine
    }

    [Fact]
    public void Turning_it_off_in_task_manager_is_respected()
    {
        var startup = new StartupRegistration(_root, @"C:\SCP.exe");
        startup.Enable();
        using (RegistryKey approved = _root.CreateSubKey(StartupRegistration.ApprovedKey))
            approved.SetValue(StartupRegistration.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.True(startup.IsRegistered);
        Assert.True(startup.DisabledInTaskManager);
        Assert.False(startup.IsEnabled);

        using (RegistryKey approved = _root.CreateSubKey(StartupRegistration.ApprovedKey))
            approved.SetValue(StartupRegistration.ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.True(startup.IsEnabled);
    }

    [Fact]
    public void A_moved_exe_is_repaired_and_an_unregistered_one_is_left_alone()
    {
        new StartupRegistration(_root, @"C:\Old\SCP.exe").Enable();
        var moved = new StartupRegistration(_root, @"D:\New place\SCP.exe");
        Assert.True(moved.RepairPath());
        Assert.False(moved.RepairPath()); // already right
        using (RegistryKey run = _root.OpenSubKey(StartupRegistration.RunKey)!)
            Assert.Equal(moved.Command, run.GetValue(StartupRegistration.ValueName));

        moved.Disable();
        Assert.False(moved.RepairPath()); // not registered: never re-enable behind the user's back
        Assert.False(moved.IsRegistered);
    }
}
