using System.Diagnostics;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // FR-29: a second launch just opens the picker in the running copy.
        using SingleInstance? instance = SingleInstance.TryAcquire();
        if (instance is null) return;

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

        using var app = new ScpApp(store);
        Application.ThreadException += (_, e) => app.HandleError(e.Exception);
        app.QuitRequested += (_, _) => Application.ExitThread();
        instance.ListenForSecondLaunch(app.OpenPicker);

        app.Start();
        Application.Run();
        Theme.Shutdown();
    }
}
