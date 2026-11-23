namespace ScreenCastPresenter.Core;

/// <summary>
/// Opt-in diagnostics for the stress harness: appends to the file named by SCP_DIAG_LOG and does
/// nothing otherwise. Local only (no network, no telemetry). Free of WinForms, so the capture and
/// recording helpers can use it too.
/// </summary>
internal static class DiagLog
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("SCP_DIAG_LOG") is { Length: > 0 } p ? p : null;
    private static readonly object Lock = new();

    public static bool Enabled => Path is not null;

    public static void Write(string message)
    {
        if (Path is null) return;
        lock (Lock)
        {
            try
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}\n");
            }
            catch (IOException)
            {
                // diagnostics must never break anything
            }
        }
    }
}
