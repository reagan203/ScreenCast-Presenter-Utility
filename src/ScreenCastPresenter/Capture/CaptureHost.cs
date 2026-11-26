using System.Diagnostics;
using System.Drawing.Imaging;
using ScreenCastPresenter.Core;
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
        (SensitiveKinds autoHide, RedactStyle autoStyle) = start.Length > 9 ? CaptureProtocol.ParseAutoHide(start[8], start[9]) : (SensitiveKinds.None, RedactStyle.Solid);

        uint mainThread = PInvoke.GetCurrentThreadId();
        HWND child = CreateChild(parent, size);
        using var engine = new WgcEngine(start[3] == "1", start[4] == "1", autoHide, autoStyle, start.Length > 10 ? SensitiveText.ParseWordsToken(start[10]) : null);
        engine.InsetFailed += (_, reason) => Send("inset-failed " + reason);
        engine.AutoHidden += (_, words) => Send("autohid " + words);
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
                default:
                    ApplyMark(engine, line);
                    break;
            }
        }
        PInvoke.PostThreadMessage(mainThread, WmStop, default, default); // SCP went away
    }

    /// <summary>
    /// One line about what's drawn over the picture (FR-65, FR-71 to FR-84) applied to
    /// <paramref name="engine"/>. Anything malformed is ignored. False when the line isn't about marks.
    /// </summary>
    internal static bool ApplyMark(IEffectsEngine engine, string line)
    {
        string[] p = line.Split(' ', 3);
        switch (p[0])
        {
            case "click" when p.Length > 2 && CaptureProtocol.ParsePoint(p[1]) is { } at: engine.Click(at, p[2] == "1"); return true;
            case "pointer" when p.Length > 1: engine.Pointer(CaptureProtocol.ParsePoint(p[1])); return true;
            case "laser" when p.Length > 1: engine.Laser(CaptureProtocol.ParsePoint(p[1])); return true;
            case "cursor" when p.Length > 1: engine.ShowCursor(p[1] == "1"); return true;
            case "ink-start" when ParseInkStart(line) is { } stroke: engine.InkStart(stroke); return true;
            case "ink-add" when p.Length > 2: engine.InkAdd(CaptureProtocol.ParseInt(p[1]), CaptureProtocol.ParsePoints(p[2])); return true;
            case "ink-set" when p.Length > 2: engine.InkSet(CaptureProtocol.ParseInt(p[1]), CaptureProtocol.ParsePoints(p[2])); return true;
            case "redact":
                var redact = p.Length > 1 ? CaptureProtocol.ParseRedact(line[7..]) : null;
                engine.Redact(redact?.Regions ?? [], redact?.Style ?? RedactStyle.Solid);
                return true;
            case "spotlight" when p.Length > 1: engine.Spotlight(CaptureProtocol.ParsePoint(p[1])); return true;
            case "caption" when p.Length > 1:
                string captionText = line[8..];
                if (captionText == "none") engine.Caption(null, null);
                else
                {
                    int split = captionText.IndexOf(CaptureProtocol.LineSeparator);
                    engine.Caption(split < 0 ? captionText : captionText[..split], split < 0 ? "" : captionText[(split + 1)..]);
                }
                return true;
            case "logo" when p.Length > 1:
                string logoPath = line[5..];
                engine.Logo(logoPath == "none" ? null : logoPath);
                return true;
            case "qr" when p.Length > 1:
                if (p[1] == "none") engine.Qr(null, InsetCorner.TopRight);
                else if (p.Length > 2) engine.Qr(p[2], int.TryParse(p[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int qc) && Enum.IsDefined((InsetCorner)qc) ? (InsetCorner)qc : InsetCorner.TopRight);
                return true;
            case "live": engine.LiveCaption(line.Length > 5 ? line[5..] : null); return true;
            case "live-style" when p.Length > 2:
                engine.CaptionStyle(new CaptionLook(
                    int.TryParse(p[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int size) && Enum.IsDefined((CaptionSize)size) ? (CaptionSize)size : CaptionSize.Normal,
                    int.TryParse(p[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int place) && Enum.IsDefined((CaptionPlace)place) ? (CaptionPlace)place : CaptionPlace.Bottom));
                return true;
            case "autohide" when p.Length > 2:
                (SensitiveKinds kinds, RedactStyle style) = CaptureProtocol.ParseAutoHide(p[1], p[2]);
                engine.AutoHide(kinds, style);
                return true;
            case "autohide-words" when p.Length > 1: engine.AutoHideWords(SensitiveText.ParseWordsToken(p[1])); return true;
            case "ink-remove" when p.Length > 1: engine.InkRemove(CaptureProtocol.ParseInt(p[1])); return true;
            case CaptureProtocol.InkClear: engine.InkClear(); return true;
            case "inset" when p.Length > 1:
                if (p[1] == "none") engine.Inset(null, InsetCorner.BottomRight, InsetLayout.DefaultSize);
                else if (CaptureProtocol.ParseSource(p[1]) is { } inset)
                {
                    (InsetCorner corner, int percent) = CaptureProtocol.ParseInsetPlace(p.Length > 2 ? p[2] : "");
                    engine.Inset(inset, corner, percent);
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>"ink-start id tool argb width [text]" → a new, empty mark; null if malformed (or text without words).</summary>
    internal static Core.InkStroke? ParseInkStart(string line)
    {
        string[] p = line.Split(' ', 6);
        if (p.Length < 5 || p[0] != "ink-start") return null;
        int tool = CaptureProtocol.ParseInt(p[2]);
        if (!Enum.IsDefined((Core.InkTool)tool)) return null;
        double width = CaptureProtocol.ParseDouble(p[4]);
        if (width <= 0) return null;
        var stroke = new Core.InkStroke(CaptureProtocol.ParseInt(p[1]), (Core.InkTool)tool, CaptureProtocol.ParseInt(p[3]), (float)width);
        if (stroke.Tool == Core.InkTool.Text)
        {
            if (p.Length < 6 || p[5].Trim().Length == 0) return null;
            stroke.Text = p[5];
        }
        return stroke;
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

    /// <summary>
    /// --still &lt;source&gt; &lt;cursor&gt; &lt;crop&gt; &lt;png path&gt; [&lt;hidden parts&gt;]: one frame to a PNG file,
    /// with the parts hidden from the audience (FR-74) covered. Exit 0 = saved.
    /// </summary>
    private static int RunStill(string[] args)
    {
        if (args.Length < 5) return 2;
        SourceWindow source = CaptureProtocol.ParseSource(args[1]) ?? throw new InvalidOperationException("That window or screen is gone.");
        source = source with { Crop = CaptureProtocol.ParseCrop(args[3]) };
        IReadOnlyList<CropRegion> hidden = args.Length > 5 && args[5].StartsWith("redact ", StringComparison.Ordinal)
            ? CaptureProtocol.ParseRedact(args[5]["redact ".Length..])?.Regions ?? [] : [];
        SensitiveKinds autoHide = args.Length > 6 ? CaptureProtocol.ParseAutoHide(args[6], "0").Kinds : SensitiveKinds.None;
        List<string> words = args.Length > 7 ? SensitiveText.ParseWordsToken(args[7]) : [];
        using Bitmap? image = StillCapture.CaptureAsync(source, args[2] == "1", StillWait, hidden, autoHide, words).GetAwaiter().GetResult();
        if (image is null) return 4;
        image.Save(args[4], ImageFormat.Png);
        return 0;
    }

    /// <summary>Runs the still helper from SCP. True when the PNG was written.</summary>
    /// <summary>How long a screenshot waits for a frame.</summary>
    public static readonly TimeSpan StillWait = TimeSpan.FromSeconds(3);

    /// <param name="hidden">Parts hidden from the audience (FR-74): covered in the picture too.</param>
    /// <param name="autoHide">Automatic hiding (FR-88): what it finds is covered in the picture too.</param>
    /// <param name="words">The presenter's own words for automatic hiding.</param>
    public static async Task<bool> CaptureStillToFileAsync(SourceWindow source, bool cursor, string path, IReadOnlyList<CropRegion>? hidden = null,
        SensitiveKinds autoHide = SensitiveKinds.None, IReadOnlyList<string>? words = null)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { CaptureProtocol.StillSwitch, CaptureProtocol.SourceSpec(source), cursor ? "1" : "0", CaptureProtocol.Crop(source.Crop), path,
                     CaptureProtocol.Redact(hidden ?? [], RedactStyle.Solid), ((int)autoHide).ToString(System.Globalization.CultureInfo.InvariantCulture), SensitiveText.WordsToken(words) })
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
