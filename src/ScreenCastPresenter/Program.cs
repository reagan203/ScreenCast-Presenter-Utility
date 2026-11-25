using System.Diagnostics;
using System.Runtime.CompilerServices;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The same exe doubles as the capture helper process (GPU work kept out of the app).
        // Dispatch before anything touches WinForms, so the helper never loads it.
        if (args.Length > 0 && (args[0] == CaptureProtocol.HostSwitch || args[0] == CaptureProtocol.StillSwitch || args[0] == Recording.RecordingProtocol.RecordSwitch))
            return CaptureHost.Run(args);
        // Settings › Apps › Uninstall runs the installed exe with this switch.
        if (args.Contains(Installer.UninstallSwitch)) return RunUninstall();
        RunApp(startedByWindows: args.Contains(StartupRegistration.StartupSwitch));
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp(bool startedByWindows)
    {
        // FR-29: a second launch just opens the picker in the running copy.
        using SingleInstance? instance = SingleInstance.TryAcquire();
        if (instance is null) return;

        SettingsStore store = InitializeUi();
        string? cleanup;
        using (var app = new ScpApp(store))
        {
            Application.ThreadException += (_, e) => app.HandleError(e.Exception);
            app.QuitRequested += (_, _) => Application.ExitThread();
            instance.ListenForSecondLaunch(app.OpenPicker);
            instance.ListenForQuit(app.RequestQuit); // an uninstall from Settings › Apps

            app.Start(startedByWindows);
            Application.Run();
            cleanup = app.CleanupAfterExit;
        }
        Theme.Shutdown();
        if (cleanup is not null) StartCleanup(cleanup);
    }

    /// <summary>WinForms, the UI thread's sync context, settings and theme: shared by the app and the uninstaller.</summary>
    private static SettingsStore InitializeUi()
    {
        ApplicationConfiguration.Initialize(); // PerMonitorV2 DPI, visual styles (from the csproj)

        // Resilience: an exception on the UI thread must never take the app down.
        // Must come before anything creates a control (the sync context below creates one).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Debug.WriteLine(e.ExceptionObject);

        // Async continuations, SystemEvents and theme changes all come back to this UI thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        // Tests and the stress harness point SCP at a throwaway settings file, never the user's.
        string settingsPath = Environment.GetEnvironmentVariable("SCP_SETTINGS_PATH") is { Length: > 0 } custom
            ? custom
            : SettingsStore.DefaultPath;
        var store = new SettingsStore(settingsPath);
        store.Load();
        Theme.Initialize(store.Current.ThemeMode);
        Theme.TextScale = (float)store.Current.TextSize;
        Theme.UseWindowsAccent = store.Current.UseWindowsAccent;
        return store;
    }

    /// <summary>
    /// Uninstall from Settings › Apps: ask, close the running copy (it finishes a recording first),
    /// remove the shortcuts and the Apps entry, then delete the exe once this process has exited.
    /// Recordings, screenshots and settings are kept.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunUninstall()
    {
        InitializeUi();
        try
        {
            if (!ConfirmForm.Ask("Remove ScreenCast Presenter?",
                    "This removes it from the Start menu, the desktop and Settings › Apps, and closes it if it's running. Your recordings, screenshots and settings are kept.",
                    "Remove it"))
                return 1;
            if (!SingleInstance.AskRunningCopyToQuit(TimeSpan.FromSeconds(30)))
            {
                ConfirmForm.Tell("ScreenCast Presenter is still busy",
                    "It may be saving a recording. Quit it from the icon by the clock, then try again.");
                return 1;
            }
            var installer = new Installer(Microsoft.Win32.Registry.CurrentUser, InstallLocations.ForCurrentUser(), new Interop.ShellLinkWriter());
            installer.Uninstall();
            StartCleanup(installer.CleanupCommand());
            return 0;
        }
        finally
        {
            Theme.Shutdown();
        }
    }

    /// <summary>Deletes the installed exe after this process exits (a hidden cmd that waits a few seconds first).</summary>
    private static void StartCleanup(string arguments)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Not the install folder, or cmd would hold it open and it couldn't be removed.
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex); // the shortcuts and Apps entry are gone; only the file stays behind
        }
    }
}
