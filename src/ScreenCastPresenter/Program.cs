using System.Diagnostics;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize(); // PerMonitorV2 DPI, visual styles (from the csproj)

#pragma warning disable WFO5001 // Dark color mode is marked experimental; it gives us dark scrollbars, combo boxes and menus.
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001

        // Resilience: an exception on the UI thread must never take the app down.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using var controller = new SessionController();
        using var tray = new TrayIcon(controller);

        Application.ThreadException += (_, e) => controller.RecoverFromError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Debug.WriteLine(e.ExceptionObject);

        tray.QuitRequested += async (_, _) =>
        {
            tray.ClosePicker();
            await controller.StopAsync();
            Application.ExitThread();
        };

        // First run of M1: open the picker straight away. Closing it leaves SCP in the tray.
        tray.ShowPicker();
        Application.Run();
    }
}
