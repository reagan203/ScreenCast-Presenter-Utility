using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StressHarness;

/// <summary>One measurement of the SCP process.</summary>
internal sealed record Sample(DateTime Time, double PrivateMb, int Handles, uint Gdi, uint User, double ResponseMs, int Threads = 0);

/// <summary>The SCP app under test, started with throwaway settings and favourites for the test windows.</summary>
internal sealed class ScpProcess : IDisposable
{
    public const string OutputTitle = "ScreenCast Presenter output";

    private readonly Process _process;
    private readonly string _dir;
    private readonly string _exe;

    private ScpProcess(Process process, string dir, string exe)
    {
        _process = process;
        _dir = dir;
        _exe = exe;
    }

    public string SettingsPath => Path.Combine(_dir, "settings.json");

    /// <summary>
    /// Runs the exe as a command line (FR-43), as a script or a button deck would: it reaches this
    /// running copy through its command pipe. The exit code and what it printed.
    /// </summary>
    public (int ExitCode, string Output) RunCommandLine(params string[] args)
    {
        var info = new ProcessStartInfo(_exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment["SCP_SETTINGS_PATH"] = SettingsPath;
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't run the command line.");
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill();
            return (-1, "timed out");
        }
        return (process.ExitCode, output.Trim());
    }

    public int Id => _process.Id;
    public string ScreenshotDir => Path.Combine(_dir, "shots");
    public string RecordingDir => Path.Combine(_dir, "recs");

    /// <summary>The app's diagnostic log (it says why something couldn't be done).</summary>
    public static string DiagLog => Path.Combine(AppContext.BaseDirectory, "scp-diag.log");

    /// <summary>Capture-helper processes still running (SCP's own exe in helper mode).</summary>
    public int HelperCount => Math.Max(0, Process.GetProcessesByName("ScreenCastPresenter").Length - 1);

    /// <summary>Total private memory of running helpers.</summary>
    public double HelperMb => Process.GetProcessesByName("ScreenCastPresenter").Where(p => p.Id != _process.Id).Sum(p => p.PrivateMemorySize64) / (1024.0 * 1024.0);
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    /// <param name="favourites">Titles of TestWindowHost windows for favourite slots 1, 2, 3…</param>
    /// <param name="configure">More settings for one scenario (e.g. a never-show list).</param>
    public static ScpProcess Start(string exe, string? outputDevice, string[] favourites, Action<JsonObject>? configure = null)
    {
        if (Process.GetProcessesByName("ScreenCastPresenter").Length > 0)
            throw new InvalidOperationException("Close the running ScreenCast Presenter first: the harness needs its shortcuts.");

        string dir = Path.Combine(Path.GetTempPath(), "scp-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var slots = new JsonArray();
        for (int i = 0; i < 9; i++)
            slots.Add(i < favourites.Length ? new JsonObject { ["AppName"] = "TestWindowHost", ["Title"] = favourites[i] } : null);
        var settings = new JsonObject
        {
            ["OutputDevice"] = outputDevice,
            ["FirstRunDone"] = true,
            ["ShowBadge"] = Environment.GetEnvironmentVariable("SCP_HARNESS_NO_OVERLAYS") != "1",
            ["ShowOutline"] = Environment.GetEnvironmentVariable("SCP_HARNESS_NO_OVERLAYS") != "1",
            ["CaptureEngine"] = Environment.GetEnvironmentVariable("SCP_HARNESS_ENGINE") ?? "Automatic",
            ["ScreenshotFolder"] = Path.Combine(dir, "shots"),
            ["RecordingFolder"] = Path.Combine(dir, "recs"),
            ["RecordSound"] = "App",
            ["Favourites"] = slots,
        };
        configure?.Invoke(settings);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.Environment["SCP_SETTINGS_PATH"] = path;
        info.Environment["SCP_DIAG_LOG"] = DiagLog;
        Process process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start SCP.");
        var scp = new ScpProcess(process, dir, exe);
        scp.WaitUntilReady();
        return scp;
    }

    private void WaitUntilReady()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (_process.HasExited) throw new InvalidOperationException($"SCP exited during startup (code {_process.ExitCode}).");
            if (Native.TopLevelWindows(_process.Id, visibleOnly: false).Count > 0)
            {
                Thread.Sleep(1500); // startup message, shortcut registration
                return;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("SCP didn't create a window within 15 s.");
    }

    /// <summary>
    /// UI-thread round trip: how long SCP takes to process a message. A frozen UI thread shows up
    /// here long before anything crashes.
    /// </summary>
    public double ResponseMs()
    {
        nint window = Native.TopLevelWindows(_process.Id, visibleOnly: false).FirstOrDefault();
        if (window == 0) return double.NaN;
        var clock = Stopwatch.StartNew();
        nint ok = Native.SendMessageTimeout(window, 0 /* WM_NULL */, 0, 0, 0x2 /* SMTO_ABORTIFHUNG */, 5000, out _);
        return ok == 0 ? 5000 : clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Private memory once SCP has settled: the lowest of five readings over two seconds. A single
    /// reading can land just before SCP's compaction and over-state memory by several MB.
    /// </summary>
    public double SettledMb()
    {
        double lowest = double.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            _process.Refresh();
            lowest = Math.Min(lowest, _process.PrivateMemorySize64 / (1024.0 * 1024.0));
            Thread.Sleep(500);
        }
        return lowest;
    }

    public Sample Measure()
    {
        _process.Refresh();
        return new Sample(DateTime.Now, _process.PrivateMemorySize64 / (1024.0 * 1024.0), _process.HandleCount,
            Native.GetGuiResources(_process.Handle, 0), Native.GetGuiResources(_process.Handle, 1), ResponseMs(), _process.Threads.Count);
    }

    /// <summary>Visible SCP windows titled as output windows.</summary>
    public List<nint> OutputWindows() =>
        Native.TopLevelWindows(_process.Id, visibleOnly: true).Where(h => Native.Title(h) == OutputTitle).ToList();

    /// <summary>Visible SCP windows (other than outputs) that screen capture could see. Must always be empty.</summary>
    public List<string> PrivacyLeaks()
    {
        var leaks = new List<string>();
        foreach (nint h in Native.TopLevelWindows(_process.Id, visibleOnly: true))
        {
            string title = Native.Title(h);
            if (title == OutputTitle || Native.IsCloaked(h)) continue;
            Native.GetWindowRect(h, out Native.RECT r);
            if (r.Width <= 0 || r.Height <= 0) continue;
            if (!Native.GetWindowDisplayAffinity(h, out uint affinity) || affinity != Native.WDA_EXCLUDEFROMCAPTURE)
                leaks.Add($"window {h} '{title}' {r.Width}x{r.Height} affinity=0x{affinity:X}");
        }
        return leaks;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        _process.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>TestWindowHost in its own process: SCP treats its windows like any other app's.</summary>
internal sealed class TestHost : IDisposable
{
    private readonly Process _process;
    private readonly Dictionary<string, nint> _byTitle = [];

    public TestHost(string exe, params string[] specs)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (string spec in specs) info.ArgumentList.Add(spec);
        _process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start TestWindowHost.");

        string? line;
        while ((line = _process.StandardOutput.ReadLine()) is not null && line != "READY")
            Record(line);
    }

    public IReadOnlyDictionary<string, nint> Windows => _byTitle;

    public nint this[string title] => _byTitle[title];

    public void Send(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
        string? line;
        while ((line = _process.StandardOutput.ReadLine()) is not null)
        {
            if (line.StartsWith("OK ", StringComparison.Ordinal)) return;
            Record(line);
        }
    }

    /// <summary>Sends without waiting for the reply (e.g. "hang", which blocks the host).</summary>
    public void SendNoWait(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    public void DrainReplies(int expected)
    {
        for (int i = 0; i < expected; i++)
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line is null) return;
            if (!line.StartsWith("OK ", StringComparison.Ordinal)) { Record(line); i--; }
        }
    }

    private void Record(string line)
    {
        // HWND <handle> <kind> <title>
        string[] parts = line.Split(' ', 4);
        if (parts.Length >= 3 && parts[0] == "HWND")
            _byTitle[parts.Length == 4 ? parts[3] : parts[2]] = (nint)long.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("exit");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // already gone
        }
        _process.Dispose();
    }
}
