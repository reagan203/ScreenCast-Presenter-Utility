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
        // FR-90: the phone remote's network code runs in its own process, never in the app.
        if (args.Length > 0 && args[0] == Remote.RemoteHost.Switch) return Remote.RemoteHost.Run();
        // FR-84: live captions listen to the microphone in their own process, on the PC itself.
        if (args.Length > 0 && args[0] == LiveCaptions.Switch) return CaptionsHost.Run();
        // FR-73: the opt-in update check runs as its own process, so the app never uses the network.
        if (args.Length > 0 && args[0] == UpdateCheck.Switch) return UpdateHelper.Run();
        // Settings › Apps › Uninstall runs the installed exe with this switch.
        if (args.Contains(Installer.UninstallSwitch)) return RunUninstall();
        // FR-43: "--present PowerPoint --output 2" and friends.
        if (CommandLine.IsRequest(args)) return RunCommandLine(args);
        // Started for a command line (below): quiet, and never opening the picker in a copy that beat it to it.
        bool quiet = args.Contains(CommandLine.QuietStartSwitch);
        RunApp(startedByWindows: quiet || args.Contains(StartupRegistration.StartupSwitch), openPickerInRunningCopy: !quiet);
        return 0;
    }

    /// <summary>
    /// FR-43: a command-line request. A mistake is reported (exit code 2) without starting
    /// anything; otherwise it goes to the running copy, whose answer is printed (exit code 0 or
    /// 1). If none is running, ScreenCast Presenter is started as its own process first and the
    /// request goes to it the same way, so a script gets its answer at once rather than when the
    /// app is eventually closed.
    /// </summary>
    private static int RunCommandLine(string[] args)
    {
        (CliRequest? request, string? error) = CommandLine.Parse(args);
        if (request is null)
        {
            Report(error + Environment.NewLine + Environment.NewLine + CommandLine.Usage, "That command wasn't understood", problem: true);
            return 2;
        }
        if (request.Help)
        {
            Report(CommandLine.Usage, "Command line", problem: false);
            return 0;
        }
        if (SingleInstance.IsRunning) return SendToRunningCopy(args, TimeSpan.FromSeconds(8));
        if (!StartRunningCopy())
        {
            Report("ScreenCast Presenter couldn't be started.", "Command line", problem: true);
            return 1;
        }
        return SendToRunningCopy(args, TimeSpan.FromSeconds(30)); // a first start can take a few seconds
    }

    /// <summary>
    /// Starts ScreenCast Presenter as its own process, quietly. Through the shell, so it doesn't
    /// inherit this launch's console or output pipes: a script reading this launch's output must
    /// not wait for the app to close.
    /// </summary>
    private static bool StartRunningCopy()
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? "", CommandLine.QuietStartSwitch) { UseShellExecute = true });
            return started is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private static int SendToRunningCopy(string[] args, TimeSpan connectTimeout)
    {
        // A copy that is still starting may need a moment to open its end of the pipe.
        CliReply reply = CommandChannel.SendAsync(args, connectTimeout, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult()
            ?? new CliReply(false, "ScreenCast Presenter is running but didn't answer. Try again in a moment.");
        // The running copy shows its own message by the clock; the text here is for scripts.
        if (reply.Message.Length > 0) WriteToConsole(reply.Message);
        return reply.Ok ? 0 : 1;
    }

    /// <summary>
    /// Prints to the console the command came from (a GUI exe has none of its own, so it borrows
    /// its parent's). Launched from a shortcut or a button deck there is no console: problems are
    /// then shown in a small window instead, so they don't go unseen.
    /// </summary>
    private static void Report(string text, string title, bool problem)
    {
        if (WriteToConsole(text) || !problem && !Environment.UserInteractive) return;
        InitializeUi();
        try
        {
            ConfirmForm.Tell(title, text);
        }
        finally
        {
            Theme.Shutdown();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static bool WriteToConsole(string text)
    {
        bool redirected = Console.IsOutputRedirected;
        if (!redirected && !AttachConsole(-1)) return false; // ATTACH_PARENT_PROCESS
        try
        {
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            output.WriteLine();
            output.WriteLine(text);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp(bool startedByWindows, bool openPickerInRunningCopy)
    {
        // FR-29: a second launch just opens the picker in the running copy (not one started for a
        // command line: that command reaches the running copy through its pipe instead).
        using SingleInstance? instance = SingleInstance.TryAcquire(openPicker: openPickerInRunningCopy);
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
