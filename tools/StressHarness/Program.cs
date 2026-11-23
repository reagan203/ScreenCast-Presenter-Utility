using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace StressHarness;

/// <summary>
/// Load, stress and soak tests for ScreenCast Presenter (SRS 8.1, rev 2).
///
///   StressHarness [quick|full|smoke|picker|ui|switches|storm|flood|hostile|pressure|flash|soak] [--count N] [--minutes N]
///
/// Drives the real app only through its global shortcuts, with test windows from TestWindowHost,
/// and checks after every scenario: no crash, UI responsive, privacy audit clean, at most one
/// output window, memory and handle growth within budget. Writes a CSV of samples next to the exe.
/// It opens and moves windows on this desktop: run it when you aren't using the PC.
/// </summary>
internal static partial class Program
{
    private const double MemoryBudgetMb = 5;        // NFR-09
    private const double ResponseBudgetMs = 250;    // UI thread round trip
    private static readonly string[] DarkFavourites = ["SCP dark A", "SCP dark B", "SCP test normal"];

    private static readonly List<string> Failures = [];
    private static readonly List<Sample> Samples = [];
    private static string _scpExe = "", _hostExe = "";
    private static string? _outputDevice;
    private static Rectangle _outputScreen;

    [LibraryImport("user32.dll")]
    private static partial nint SetProcessDpiAwarenessContext(nint value);

    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(-4); // per-monitor v2: physical pixels, same as SCP

        string scenario = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "quick";
        int count = IntArg(args, "--count", 0);
        double minutes = IntArg(args, "--minutes", 10);

        if (Process.GetProcessesByName("LogonUI").Length > 0)
        {
            Console.WriteLine("The PC is locked. Windows blocks simulated key presses on a locked desktop, so the results would be meaningless. Unlock it and run again.");
            return 3;
        }
        (_scpExe, _hostExe) = FindExecutables();
        var screens = FlashMonitor.ListScreens();
        var output = screens.FirstOrDefault(s => !s.Primary);
        if (output.Device is null) output = screens.First(s => s.Primary);
        _outputDevice = output.Device;
        _outputScreen = output.Bounds;
        Console.WriteLine($"SCP: {_scpExe}\nScreens: {string.Join(", ", screens.Select(s => $"{s.Device} {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? " (main)" : "")}"))}\nOutput: {_outputDevice}\n");

        var clock = Stopwatch.StartNew();
        switch (scenario)
        {
            case "smoke": Smoke(); break;
            case "picker": Picker(Or(count, 20)); break;
            case "ui": Ui(Or(count, 10)); break;
            case "switches": Switches(Or(count, 200)); break;
            case "storm": Storm(Or(count, 2000)); break;
            case "flood": Flood(Or(count, 300)); break;
            case "hostile": Hostile(Or(count, 100)); break;
            case "pressure": Pressure(Or(count, 100)); break;
            case "flash": Flash(Or(count, 100)); break;
            case "soak": Soak(TimeSpan.FromMinutes(minutes)); break;
            case "record": Record(); break;
            case "window": FloatingWindow(); break;
            case "quick":
                Smoke(); Picker(20); Ui(10); Switches(200); Storm(2000); Flood(300); Hostile(50); Pressure(60); Flash(100); Record(); FloatingWindow();
                break;
            case "full":
                Smoke(); Picker(50); Ui(30); Switches(1000); Storm(10000); Flood(300); Hostile(1000); Pressure(300); Flash(100); Record(); FloatingWindow();
                break;
            default:
                Console.WriteLine("Unknown scenario.");
                return 2;
        }

        WriteCsv();
        Console.WriteLine($"\nFinished in {clock.Elapsed:mm\\:ss}.");
        if (Failures.Count == 0)
        {
            Console.WriteLine("RESULT: PASS");
            return 0;
        }
        Console.WriteLine($"RESULT: FAIL ({Failures.Count})");
        foreach (string f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }

    // ───────────────────────────── scenarios ─────────────────────────────

    /// <summary>Present via "show the window I'm in", blank, dim, favourites, stop.</summary>
    private static void Smoke()
    {
        Scenario("smoke", (scp, host) =>
        {
            Native.CtrlAlt('1'); Wait(600);
            Check(scp.OutputWindows().Count == 1, "smoke: favourite 1 didn't open an output window");
            Native.CtrlAlt('B'); Wait(400);
            Native.CtrlAlt('B'); Wait(400);
            Native.CtrlAltShift('B'); Wait(500);
            Native.CtrlAltShift('B'); Wait(500);
            for (int i = 0; i < 5; i++) { Native.CtrlAlt('D'); Wait(150); }
            Native.CtrlAlt('2'); Wait(500);
            Native.CtrlAlt((char)0x08); Wait(500); // back to previous source
            Native.CtrlAlt('F'); Wait(1800);           // freeze: hands DWM over to the WGC helper
            Check(scp.OutputWindows().Count == 1, "smoke: output gone after freeze");
            Console.WriteLine($"    frozen: helper running = {scp.HelperCount}, helper memory {scp.HelperMb:F1} MB");
            Native.CtrlAlt('F'); Wait(500);            // go live
            Native.CtrlAltShift('S'); Wait(3000);      // screenshot of what is shown
            int shots = Directory.Exists(scp.ScreenshotDir) ? Directory.GetFiles(scp.ScreenshotDir, "*.png").Length : 0;
            Check(shots == 1, $"smoke: expected 1 screenshot, found {shots}");
            Native.CtrlAlt('S'); Wait(500);
            Check(scp.OutputWindows().Count == 0, "smoke: output still visible after stop");
        });
    }

    /// <summary>
    /// M4: record what the TV shows through switches, blank and pause (FR-55, FR-54), then a
    /// background recording of a window that stops by itself when the window closes (FR-57, FR-59).
    /// Checks both files exist and are playable-sized, and that no recorder is left running.
    /// </summary>
    private static void Record()
    {
        Scenario("record", (scp, host) =>
        {
            Native.CtrlAlt('1'); Wait(700);
            Native.CtrlAlt('R'); Wait(4000);              // record what the TV shows (helper start ~2.5 s)
            Check(scp.HelperCount >= 1, "record: no recorder running after Ctrl+Alt+R");
            Probe(scp, "recording");
            double recordingMb = scp.SettledMb();
            Native.CtrlAlt('2'); Wait(800);               // switch: the recording follows the output
            Native.CtrlAlt('B'); Wait(600); Native.CtrlAlt('B'); Wait(600);
            Native.CtrlAltShift('R'); Wait(1500);         // pause
            Native.CtrlAltShift('R'); Wait(1500);         // resume
            Native.CtrlAlt('R');                          // stop
            Check(WaitFor(() => Files(scp).Length == 1, 8000), $"record: expected 1 recording after stop, found {Files(scp).Length}");
            Console.WriteLine($"    SCP while recording: {recordingMb:F1} MB; first file {Sizes(scp)}");

            Native.CtrlAlt('1'); Wait(700);
            Native.CtrlAlt('R'); Wait(4000);              // record the TV again...
            Native.CtrlAlt('S');                          // ...and stop presenting: the recording ends with it
            Check(WaitFor(() => Files(scp).Length == 2, 8000), "record: stopping the presentation didn't end its recording");

            host.Send($"front {host["SCP test normal"]}"); Wait(400);
            Native.CtrlAlt('R'); Wait(900);               // not presenting: opens the record picker
            Native.Chord(0x0D); Wait(4000);               // Enter: record the first window (the one in front)
            Check(scp.HelperCount >= 1, "record: background recording didn't start");
            host.Send($"close {host["SCP test normal"]}"); // the webinar ends
            Check(WaitFor(() => Files(scp).Length == 3, 8000), "record: recording didn't stop by itself when its window closed");
            Check(WaitFor(() => scp.HelperCount == 0, 5000), "record: recorder still running after the window closed");
            foreach (string f in Files(scp))
                Check(new FileInfo(f).Length > 1000, $"record: {Path.GetFileName(f)} is only {new FileInfo(f).Length} bytes");
            Console.WriteLine($"    files: {Sizes(scp)}");
        });

        static string[] Files(ScpProcess scp) => Directory.Exists(scp.RecordingDir) ? Directory.GetFiles(scp.RecordingDir, "*.mp4") : [];
        static string Sizes(ScpProcess scp) => string.Join(", ", Files(scp).Select(f => $"{new FileInfo(f).Length / 1024} KB"));
    }

    /// <summary>
    /// M5: Ctrl+Alt+O reaches the floating window (FR-11); it is a normal-sized window, not a
    /// whole screen; switching sources keeps it; its ✕ stops showing; and the output goes back
    /// to the screen afterwards.
    /// </summary>
    private static void FloatingWindow()
    {
        Scenario("floating window", (scp, _) =>
        {
            Native.CtrlAlt('1'); Wait(800);
            bool floating = false;
            for (int i = 0; i < 6 && !floating; i++)
            {
                Native.CtrlAlt('O'); Wait(1000);
                floating = scp.OutputWindows() is [var h] && IsFloating(h);
            }
            Check(floating, "window: Ctrl+Alt+O never reached the floating window");
            if (!floating) return;
            Probe(scp, "floating");
            nint window = scp.OutputWindows()[0];
            Native.CtrlAlt('2'); Wait(700);
            Check(scp.OutputWindows() is [var same] && same == window, "window: switching sources replaced the floating window");

            Native.SendMessageTimeout(window, 0x0010 /* WM_CLOSE */, 0, 0, 0x0002, 2000, out nint _); // the ✕
            Check(WaitFor(() => scp.OutputWindows().Count == 0, 3000), "window: closing the floating window didn't stop showing");

            // Back to fullscreen for the next scenarios: cycle until the output fills a screen again.
            Native.CtrlAlt('1'); Wait(800);
            for (int i = 0; i < 6 && scp.OutputWindows() is [var h] && IsFloating(h); i++) { Native.CtrlAlt('O'); Wait(1000); }
            Check(scp.OutputWindows() is [var full] && !IsFloating(full), "window: couldn't switch back to a full screen");
        });

        static bool IsFloating(nint hwnd) =>
            Native.GetWindowRect(hwnd, out Native.RECT r) && (r.Right - r.Left < _outputScreen.Width || r.Bottom - r.Top < _outputScreen.Height);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    /// <summary>NFR-08: the picker is on screen within 200 ms of the shortcut; privacy audit while it's open.</summary>
    private static void Picker(int count)
    {
        Scenario($"picker x{count}", (scp, _) =>
        {
            var times = new List<double>();
            for (int i = 0; i < count; i++)
            {
                var clock = Stopwatch.StartNew();
                Native.CtrlAlt('P');
                nint picker = 0;
                while (clock.ElapsedMilliseconds < 3000)
                {
                    picker = Native.TopLevelWindows(scp.Id, visibleOnly: true)
                        .FirstOrDefault(h => Native.Title(h) == "ScreenCast Presenter picker" && !Native.IsCloaked(h));
                    if (picker != 0) break;
                    Thread.Sleep(2);
                }
                double ms = clock.Elapsed.TotalMilliseconds;
                Check(picker != 0, "picker: didn't open within 3 s");
                times.Add(ms);
                Wait(400);
                Probe(scp, $"picker open {i}"); // scrim, panel, thumbnails: all must be excluded from capture
                Native.Chord(Native.VK_ESCAPE);
                Wait(500);
            }
            times.Sort();
            double median = times[times.Count / 2], worst = times[^1];
            Console.WriteLine($"    picker open: median {median:F0} ms, worst {worst:F0} ms (first {times.Min():F0} ms)");
            Check(median <= 200, $"picker: median open time {median:F0} ms exceeds 200 ms (NFR-08)");
        });
    }

    /// <summary>Command palette and shortcuts page: open/close repeatedly, privacy audit while open.</summary>
    private static void Ui(int count)
    {
        Scenario($"palette and shortcuts x{count}", (scp, _) =>
        {
            for (int i = 0; i < count; i++)
            {
                Native.Chord(Native.VK_CONTROL, Native.VK_MENU, 0x0D); Wait(500);
                Check(Native.TopLevelWindows(scp.Id, true).Any(h => Native.Title(h) == "ScreenCast Presenter command palette"), "ui: palette didn't open");
                Probe(scp, $"palette open {i}");
                Native.Chord(Native.VK_ESCAPE); Wait(300);

                Native.CtrlAlt('K'); Wait(700);
                Check(Native.TopLevelWindows(scp.Id, true).Any(h => Native.Title(h).StartsWith("Keyboard shortcuts", StringComparison.Ordinal)), "ui: shortcuts page didn't open");
                Probe(scp, $"shortcuts open {i}");
                Native.Chord(Native.VK_ESCAPE); Wait(400);
            }
        });
    }

    /// <summary>Alternating favourites, each switch allowed to finish (full fades).</summary>
    private static void Switches(int count)
    {
        Scenario($"switches x{count}", (scp, _) =>
        {
            var settled = new List<double>();
            for (int i = 0; i < count; i++)
            {
                Native.CtrlAlt(i % 2 == 0 ? '1' : '2');
                Wait(260);
                if (i % 50 == 0) Probe(scp, $"switch {i}");
                if (i > 0 && i % 100 == 0)
                {
                    // Settled checkpoint: pause so SCP compacts memory, then measure. A leak climbs
                    // checkpoint after checkpoint; a one-off cost (JIT, caches) levels off.
                    Wait(4000);
                    settled.Add(scp.SettledMb());
                }
            }
            if (settled.Count >= 3)
            {
                Console.WriteLine($"    settled memory every 100 switches: {string.Join(" → ", settled.Select(m => m.ToString("F1", CultureInfo.InvariantCulture)))} MB");
                double lateGrowth = settled[^1] - settled[settled.Count / 2];
                Check(lateGrowth < 2.0, $"switches: memory still climbing in the second half ({lateGrowth:F1} MB): looks like a leak");
            }
        });
    }

    /// <summary>Shortcut storm: thousands of presses with no pause; must coalesce, not queue.</summary>
    private static void Storm(int presses)
    {
        Scenario($"storm x{presses}", (scp, _) =>
        {
            char[] keys = ['1', '2', 'B', 'B', 'D', '3', (char)0x22 /* PgDn */, (char)0x21 /* PgUp */];
            var random = new Random(42);
            for (int i = 0; i < presses; i++)
            {
                Native.CtrlAlt(keys[random.Next(keys.Length)]);
                if (i % 500 == 0) Probe(scp, $"storm {i}");
            }
            // Backlog check: after the storm stops, SCP must settle within a second.
            var settle = Stopwatch.StartNew();
            Wait(1000);
            double response = scp.ResponseMs();
            Check(response < ResponseBudgetMs, $"storm: still busy {settle.ElapsedMilliseconds} ms after the last key ({response:F0} ms round trip)");
        });
    }

    /// <summary>Hundreds of open windows: enumeration and cycling stay fast.</summary>
    private static void Flood(int windows)
    {
        var specs = DarkSpecs().Concat(Enumerable.Range(0, windows).Select(i => $"normal:Flood {i:000}")).ToArray();
        Scenario($"flood {windows} windows", (scp, _) =>
        {
            for (int i = 0; i < 30; i++)
            {
                var clock = Stopwatch.StartNew();
                Native.CtrlAlt((char)0x22); // next window: enumerates everything
                Probe(scp, $"cycle {i}");
                Wait(150);
                Check(clock.ElapsedMilliseconds < 1500, $"flood: cycling took {clock.ElapsedMilliseconds} ms");
            }
            Native.CtrlAlt('P'); Wait(800); // picker lists them all
            Probe(scp, "picker with flood");
            Native.Chord(Native.VK_ESCAPE); Wait(300);
        }, specs);
    }

    /// <summary>Awkward sources: resizing, retitling, huge, tiny, hung, and closing mid-fade.</summary>
    private static void Hostile(int closeCycles)
    {
        string[] specs = [.. DarkSpecs(), "resizer:Hostile resizer", "retitler:Hostile retitler", "big:Hostile big", "tiny:Hostile tiny"];
        Scenario($"hostile (close mid-fade x{closeCycles})", (scp, host) =>
        {
            // Present each awkward window via the palette-free route: favourites are fixed, so cycle to them.
            for (int i = 0; i < 12; i++) { Native.CtrlAlt((char)0x22); Wait(300); }
            Probe(scp, "after hostile windows");

            // Hung source: its app stops responding for 6 s while SCP shows it. SCP must not freeze.
            Native.CtrlAlt('1'); Wait(400);
            host.SendNoWait("hang 6000");
            for (int i = 0; i < 10; i++)
            {
                Native.CtrlAlt('B'); Wait(250);
                Probe(scp, "source hung");
            }
            host.DrainReplies(1);

            // Source closes while SCP is switching to it.
            for (int i = 0; i < closeCycles; i++)
            {
                Native.CtrlAlt('1'); Wait(220);
                Native.CtrlAlt('2'); Wait(40 + i % 80); // close somewhere during the fade
                host.Send($"close {host["SCP dark B"]}");
                host.Send("open dark:SCP dark B");
                if (i % 25 == 0) Probe(scp, $"close mid-fade {i}");
            }
            Wait(500);
            Check(scp.OutputWindows().Count <= 1, "hostile: more than one output window");
        }, specs);
    }

    /// <summary>All CPU cores at 100% while switching.</summary>
    private static void Pressure(int count)
    {
        Scenario($"cpu pressure x{count}", (scp, _) =>
        {
            using var cts = new CancellationTokenSource();
            var burners = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() =>
            {
                double x = 1;
                while (!cts.IsCancellationRequested) x = Math.Sqrt(x + 1);
                return x;
            })).ToList();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Native.CtrlAlt(i % 2 == 0 ? '1' : '2');
                    Wait(300);
                    if (i % 10 == 0) Probe(scp, $"pressure {i}", budgetMs: ResponseBudgetMs * 2);
                }
            }
            finally
            {
                cts.Cancel();
                Task.WaitAll([.. burners]);
            }
        });
    }

    /// <summary>NFR-16: zero flash frames in N switches between two dark windows, recorded at display rate.</summary>
    private static void Flash(int count)
    {
        Scenario($"flash x{count}", (scp, _) =>
        {
            Native.CtrlAlt('1'); Wait(800);
            using var monitor = new FlashMonitor(_outputScreen);
            monitor.Start();
            Wait(300);
            for (int i = 0; i < count; i++)
            {
                Native.CtrlAlt(i % 2 == 0 ? '2' : '1');
                Wait(320);
            }
            Native.CtrlAlt('B'); Wait(300); Native.CtrlAlt('B'); Wait(300); // blank and back
            (int frames, int flashes, double maxBright, string? error) = monitor.Stop();
            Console.WriteLine($"    captured {frames} frames, flash frames: {flashes}, brightest frame: {maxBright:P1} of pixels over #303030");
            Check(error is null, $"flash: capture failed: {error}");
            Check(frames > count, $"flash: only {frames} frames captured; the measurement isn't valid");
            Check(flashes == 0, $"flash: {flashes} frames brighter than the flash threshold");
        });
    }

    /// <summary>Long run: a switch every 2 s, blank every 10th, sampled every 10 s; memory and handles must not drift.</summary>
    private static void Soak(TimeSpan duration)
    {
        Scenario($"soak {duration.TotalMinutes:F0} min", (scp, _) =>
        {
            var clock = Stopwatch.StartNew();
            var lastSample = TimeSpan.Zero;
            int i = 0;
            while (clock.Elapsed < duration)
            {
                Native.CtrlAlt(i % 2 == 0 ? '1' : '2');
                if (i % 10 == 5) { Wait(300); Native.CtrlAlt('B'); Wait(300); Native.CtrlAlt('B'); }
                i++;
                Wait(2000);
                if (clock.Elapsed - lastSample >= TimeSpan.FromSeconds(10))
                {
                    lastSample = clock.Elapsed;
                    Probe(scp, $"soak {clock.Elapsed:hh\\:mm\\:ss}");
                }
            }
        }, memoryBudgetMb: MemoryBudgetMb);
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    private static IEnumerable<string> DarkSpecs() => ["dark:SCP dark A", "darker:SCP dark B", "normal:SCP test normal"];

    private static void Scenario(string name, Action<ScpProcess, TestHost> body, string[]? specs = null, double memoryBudgetMb = MemoryBudgetMb)
    {
        Console.WriteLine($"▶ {name}");
        int failuresBefore = Failures.Count;
        using var host = new TestHost(_hostExe, specs ?? [.. DarkSpecs()]);
        using ScpProcess scp = ScpProcess.Start(_scpExe, _outputDevice, DarkFavourites);

        // Warm up every feature once so first-use costs (JIT, WinForms type setup, font caches)
        // don't count as growth: present, switch, blank, picker, stop.
        Native.CtrlAlt('1'); Wait(500); Native.CtrlAlt('2'); Wait(500);
        Native.CtrlAlt('B'); Wait(300); Native.CtrlAlt('B'); Wait(300);
        Native.CtrlAlt('P'); Wait(700); Native.Chord(Native.VK_ESCAPE); Wait(300);
        Native.CtrlAlt('S'); Wait(4000);
        Sample baseline = scp.Measure() with { PrivateMb = scp.SettledMb() };
        Samples.Add(baseline);

        try
        {
            body(scp, host);
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: harness error: {ex.Message}");
        }

        Native.CtrlAlt('S');
        Wait(4000); // SCP compacts memory ~3 s after going quiet; measure after that
        Check(!scp.HasExited, $"{name}: SCP crashed (exit code {(scp.HasExited ? scp.ExitCode : 0)})");
        if (scp.HasExited) return;

        Sample end = scp.Measure() with { PrivateMb = scp.SettledMb() };
        Samples.Add(end);
        double growth = end.PrivateMb - baseline.PrivateMb;
        Console.WriteLine($"    memory {baseline.PrivateMb:F1} → {end.PrivateMb:F1} MB, handles {baseline.Handles} → {end.Handles}, GDI {baseline.Gdi} → {end.Gdi}, USER {baseline.User} → {end.User}");
        Check(growth < memoryBudgetMb, $"{name}: memory grew {growth:F1} MB (budget {memoryBudgetMb} MB)");
        Check(end.Handles <= baseline.Handles * 1.02 + 30, $"{name}: handles grew {baseline.Handles} → {end.Handles}");
        Check(end.Gdi <= baseline.Gdi * 1.02 + 10, $"{name}: GDI objects grew {baseline.Gdi} → {end.Gdi}");
        Check(end.User <= baseline.User * 1.02 + 10, $"{name}: USER objects grew {baseline.User} → {end.User}");
        Check(scp.OutputWindows().Count == 0, $"{name}: output still showing after stop");
        Check(scp.HelperCount == 0, $"{name}: {scp.HelperCount} capture helper(s) still running after stop");
        Probe(scp, "end");
        Console.WriteLine(Failures.Count == failuresBefore ? "    ok" : $"    {Failures.Count - failuresBefore} problem(s)");
    }

    /// <summary>Responsiveness, privacy and single-output checks at one moment.</summary>
    private static void Probe(ScpProcess scp, string when, double budgetMs = ResponseBudgetMs)
    {
        if (scp.HasExited)
        {
            Check(false, $"SCP crashed ({when})");
            return;
        }
        Sample s = scp.Measure();
        Samples.Add(s);
        Check(s.ResponseMs < budgetMs, $"UI thread took {s.ResponseMs:F0} ms to respond ({when})");
        foreach (string leak in scp.PrivacyLeaks()) Check(false, $"privacy leak ({when}): {leak}");
        Check(scp.OutputWindows().Count <= 1, $"more than one output window ({when})");
    }

    private static void Check(bool ok, string failure)
    {
        if (ok || Failures.Contains(failure)) return;
        Failures.Add(failure);
        Console.WriteLine("    ✗ " + failure);
    }

    private static void Wait(int ms) => Thread.Sleep(ms);

    private static int Or(int value, int fallback) => value > 0 ? value : fallback;

    private static int IntArg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }

    private static (string Scp, string Host) FindExecutables()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ScreenCastPresenter.slnx"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("Run the harness from inside the repository.");

        string Find(string project, string exe)
        {
            foreach (string config in new[] { "Release", "Debug" })
            {
                string path = Path.Combine(dir, project, "bin", config, "net10.0-windows10.0.19041.0", exe);
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException($"Build {project} first (dotnet build -c Release).");
        }
        return (Find(Path.Combine("src", "ScreenCastPresenter"), "ScreenCastPresenter.exe"), Find(Path.Combine("tools", "TestWindowHost"), "TestWindowHost.exe"));
    }

    private static void WriteCsv()
    {
        var csv = new StringBuilder("time,private_mb,handles,gdi,user,response_ms\n");
        foreach (Sample s in Samples)
            csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{s.Time:O},{s.PrivateMb:F2},{s.Handles},{s.Gdi},{s.User},{s.ResponseMs:F1}"));
        string path = Path.Combine(AppContext.BaseDirectory, $"stress-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(path, csv.ToString());
        Console.WriteLine($"Samples: {path}");
    }
}
