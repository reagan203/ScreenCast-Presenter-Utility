using System.Diagnostics;
using System.Drawing.Imaging;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// The capture helper process: the same exe started with --capture-host (or --still for a
/// screenshot). It owns everything GPU: the graphics driver alone costs ~26 MB that a process
/// never gets back, so keeping it out of SCP keeps SCP's idle memory at ~14 MB (NFR-01), and a
/// driver crash or reset only takes the helper down (SRS 4.4).
///
/// The helper draws into a child window it places inside SCP's output window, so the output
/// keeps its cloaking, position, topmost and privacy behaviour. No WinForms is loaded here.
/// </summary>
internal static class CaptureHost
{
    private const uint WmStop = PInvoke.WM_APP + 1;
    private static WNDPROC? _wndProc; // keep the delegate alive while the window exists
    private static readonly object WriteLock = new();

    // UTF-8 both ways, whatever the console code page: file paths may contain any character
    // (e.g. a user folder with an accent). A byte-order mark from the other side is skipped.
    private static readonly StreamReader Input = new(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
    private static readonly StreamWriter Output = new(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };

    /// <summary>Stream settings for SCP's side of the pipe, matching the helper's.</summary>
    public static void UseUtf8(ProcessStartInfo info)
    {
        info.StandardInputEncoding = new System.Text.UTF8Encoding(false);
        info.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
    }

    public static int Run(string[] args)
    {
        // Physical pixels, like SCP, so window and frame coordinates agree.
        PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try
        {
            return args[0] switch
            {
                CaptureProtocol.StillSwitch => RunStill(args),
                Recording.RecordingProtocol.RecordSwitch => RunRecorder(),
                _ => RunPresenter(),
            };
        }
        catch (Exception ex)
        {
            Send("failed " + ex.Message);
            return 3;
        }
    }

    private static void Send(string line)
    {
        lock (WriteLock)
        {
            try
            {
                Output.WriteLine(line);
                Output.Flush();
            }
            catch (IOException)
            {
                // SCP is gone; we'll exit when stdin closes.
            }
        }
    }

    // ───────────────────────────── presenting ─────────────────────────────

    private static unsafe int RunPresenter()
    {
        // start <parent> <source> <cursor> <frozen> <crop> <w> <h>
        string[] start = (Input.ReadLine() ?? "").Split(' ');
        if (start.Length < 8 || start[0] != "start") return 2;
        var parent = (HWND)(nint)long.Parse(start[1], System.Globalization.CultureInfo.InvariantCulture);
        SourceWindow source = CaptureProtocol.ParseSource(start[2]) ?? throw new InvalidOperationException("That window or screen is gone.");
        source = source with { Crop = CaptureProtocol.ParseCrop(start[5]) };
        var size = new Size(CaptureProtocol.ParseInt(start[6]), CaptureProtocol.ParseInt(start[7]));

        uint mainThread = PInvoke.GetCurrentThreadId();
        HWND child = CreateChild(parent, size);
        using var engine = new WgcEngine(start[3] == "1", start[4] == "1");
        engine.Failed += (_, reason) =>
        {
            Send("failed " + reason);
            PInvoke.PostThreadMessage(mainThread, WmStop, default, default);
        };

        engine.StartAsync(source, child, size).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) Send("ready");
            else
            {
                Send("failed " + (t.Exception?.GetBaseException().Message ?? "capture didn't start"));
                PInvoke.PostThreadMessage(mainThread, WmStop, default, default);
            }
        }, TaskScheduler.Default);

        var commands = new Thread(() => ReadCommands(engine, child, mainThread)) { IsBackground = true, Name = "SCP capture commands" };
        commands.Start();

        // Message loop for the child window; ends on stop, stdin closing, or SCP's window going away.
        while (PInvoke.GetMessage(out MSG msg, HWND.Null, 0, 0) > 0)
        {
            if (msg.message == WmStop) break;
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
        engine.Dispose(); // presents a last black frame before letting go
        if (PInvoke.IsWindow(child)) PInvoke.DestroyWindow(child);
        return 0;
    }

    private static void ReadCommands(WgcEngine engine, HWND child, uint mainThread)
    {
        string? line;
        while ((line = Input.ReadLine()) is not null)
        {
            string[] p = line.Split(' ', 3);
            switch (p[0])
            {
                case "opacity" when p.Length > 1: engine.SetOpacity(CaptureProtocol.ParseDouble(p[1])); break;
                case "freeze" when p.Length > 1: engine.SetFrozen(p[1] == "1"); break;
                case "crop" when p.Length > 1: engine.SetCrop(CaptureProtocol.ParseCrop(p[1])); break;
                case "changed": engine.OnSourceChanged(); break;
                case "message" when p.Length > 1: engine.SetIdleMessage(p.Length > 2 && p[2].Length > 0 ? p[2] : null, CaptureProtocol.ParseDouble(p[1])); break;
                case "size" when p.Length > 2:
                    var size = new Size(CaptureProtocol.ParseInt(p[1]), CaptureProtocol.ParseInt(p[2]));
                    PInvoke.SetWindowPos(child, HWND.Null, 0, 0, size.Width, size.Height,
                        SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
                    engine.OnOutputResized(size);
                    break;
                case "stop":
                    PInvoke.PostThreadMessage(mainThread, WmStop, default, default);
                    return;
            }
        }
        PInvoke.PostThreadMessage(mainThread, WmStop, default, default); // SCP went away
    }

    /// <summary>
    /// A disabled child window filling SCP's output window. Disabled = it never takes input, so
    /// the two processes' input queues never wait on each other.
    /// </summary>
    private static unsafe HWND CreateChild(HWND parent, Size size)
    {
        _wndProc = (hwnd, msg, wParam, lParam) =>
        {
            if (msg == PInvoke.WM_DESTROY) PInvoke.PostQuitMessage(0); // parent destroyed: we're done
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        };
        HINSTANCE instance = (HINSTANCE)(nint)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        Color black = UI.Theme.OutputBlack;
        fixed (char* className = "SCPCaptureSurface")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = instance,
                lpszClassName = className,
                hbrBackground = PInvoke.CreateSolidBrush(new COLORREF((uint)(black.R | black.G << 8 | black.B << 16))),
            };
            PInvoke.RegisterClassEx(in wc);
            HWND child = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_NOACTIVATE, className, null,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_DISABLED | WINDOW_STYLE.WS_CLIPSIBLINGS,
                0, 0, size.Width, size.Height, parent, HMENU.Null, instance, null);
            if (child.IsNull) throw new InvalidOperationException("Couldn't create the capture surface.");
            return child;
        }
    }

    // ───────────────────────────── recording ─────────────────────────────

    /// <summary>
    /// --record: reads one "record …" line, records until "stop", the recorded window closing, or
    /// stdin closing (SCP exited or crashed: the file is still finished properly, FR-61).
    /// </summary>
    private static int RunRecorder()
    {
        Recording.RecordingRequest? request = Recording.RecordingProtocol.ParseStart(Input.ReadLine());
        if (request is null)
        {
            Send(Recording.RecordingProtocol.Failed("That window or screen is gone."));
            return 2;
        }
        using var recorder = new Recording.Recorder(request, Send);
        recorder.Start();
        var commands = new Thread(() =>
        {
            string? line;
            while ((line = Input.ReadLine()) is not null)
            {
                switch (line.Trim())
                {
                    case "pause": recorder.Pause(); break;
                    case "resume": recorder.Resume(); break;
                    case "stop": recorder.Stop(); return;
                }
            }
            recorder.Stop(); // SCP went away
        }) { IsBackground = true, Name = "SCP recording commands" };
        commands.Start();
        recorder.Wait();
        return recorder.End == Recording.RecordingEnd.Error ? 5 : 0;
    }

    // ───────────────────────────── screenshots ─────────────────────────────

    /// <summary>--still &lt;source&gt; &lt;cursor&gt; &lt;crop&gt; &lt;png path&gt;: one frame to a PNG file. Exit 0 = saved.</summary>
    private static int RunStill(string[] args)
    {
        if (args.Length < 5) return 2;
        SourceWindow source = CaptureProtocol.ParseSource(args[1]) ?? throw new InvalidOperationException("That window or screen is gone.");
        source = source with { Crop = CaptureProtocol.ParseCrop(args[3]) };
        using Bitmap? image = StillCapture.CaptureAsync(source, args[2] == "1", TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        if (image is null) return 4;
        image.Save(args[4], ImageFormat.Png);
        return 0;
    }

    /// <summary>Runs the still helper from SCP. True when the PNG was written.</summary>
    public static async Task<bool> CaptureStillToFileAsync(SourceWindow source, bool cursor, string path)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { CaptureProtocol.StillSwitch, CaptureProtocol.SourceSpec(source), cursor ? "1" : "0", CaptureProtocol.Crop(source.Crop), path })
            info.ArgumentList.Add(a);
        using Process? helper = Process.Start(info);
        if (helper is null) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await helper.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            helper.Kill();
            return false;
        }
        return helper.ExitCode == 0 && File.Exists(path);
    }
}
