using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScreenCastPresenter.Core;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Speech;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Live captions (FR-84), the helper side: the program started with <see cref="LiveCaptions.Switch"/>
/// listens to the microphone with Windows' own speech recognizer, which works on the PC itself
/// (nothing is sent anywhere), and prints what it hears, one line per guess:
/// "ready", "partial words…", "final words.", or "error reason". It stops when the app closes its
/// input (or the app goes away). A separate process, so the recognizer never runs inside the app.
/// </summary>
internal static class CaptionsHost
{
    private static readonly Guid InprocRecognizer = new("41B89B6B-9399-11D2-9623-00C04F8EE628");
    private static readonly Guid MicrophoneInput = new("CF3D2E50-53F2-11D2-960C-00C04F8EE628");

    private const int Recognition = 38, Hypothesis = 39, Reserved1 = 30, Reserved2 = 33;
    private const int LParamIsPointer = 1, LParamIsObject = 2, LParamIsString = 3, LParamIsToken = 4;
    private const uint WholePhrase = 0xFFFFFFFF;

    public static int Run()
    {
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        using var stop = new ManualResetEventSlim();
        // The app closing our input (or going away, which closes it too) is the signal to stop.
        var watcher = new Thread(() =>
        {
            try
            {
                using Stream input = Console.OpenStandardInput();
                var buffer = new byte[64];
                while (input.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch (IOException) { }
            stop.Set();
        }) { IsBackground = true, Name = "captions: input" };
        watcher.Start();

        int exit = 0;
        // Its own free-threaded thread: SAPI's objects then need no message loop.
        var worker = new Thread(() => exit = Listen(output, stop)) { IsBackground = true, Name = "captions: speech" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
        return exit;
    }

    private static int Listen(StreamWriter output, ManualResetEventSlim stop)
    {
        ISpRecognizer? recognizer = null;
        ISpRecoContext? context = null;
        ISpRecoGrammar? grammar = null;
        object? microphone = null;
        try
        {
            recognizer = (ISpRecognizer)Create(InprocRecognizer);
            recognizer.SetRecognizer(null!); // Windows' default recognizer, for the language set in Windows
            microphone = Create(MicrophoneInput); // the default microphone
            recognizer.SetInput(microphone, true);
            recognizer.CreateRecoContext(out context);
            context.SetNotifyWin32Event();
            ulong interest = 1UL << Recognition | 1UL << Hypothesis | 1UL << Reserved1 | 1UL << Reserved2;
            context.SetInterest(interest, interest);
            context.CreateGrammar(0, out grammar);
            grammar.LoadDictation(default, SPLOADOPTIONS.SPLO_STATIC);
            grammar.SetDictationState(SPRULESTATE.SPRS_ACTIVE);
            Say(output, "ready");

            while (!stop.IsSet)
            {
                context.WaitForNotifyEvent(200);
                while (Next(context) is { } ev)
                {
                    string? line = ev.Kind switch
                    {
                        Recognition => "final " + ev.Text,
                        Hypothesis => "partial " + ev.Text,
                        _ => null,
                    };
                    if (line is not null && !Say(output, line)) return 0; // the app is gone
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            Say(output, "error " + Explain(ex));
            return 1;
        }
        finally
        {
            Release(grammar);
            Release(context);
            Release(microphone);
            Release(recognizer);
        }
    }

    /// <summary>The next waiting event with its words (for a guess or a result); null when none is waiting.</summary>
    private static unsafe (int Kind, string Text)? Next(ISpRecoContext context)
    {
        SPEVENT ev = default;
        uint fetched = 0;
        context.GetEvents(1, &ev, ref fetched);
        if (fetched == 0) return null;
        int kind = (int)ev.eEventId;
        nint lParam = (nint)ev.lParam;
        string text = "";
        try
        {
            if (kind is Recognition or Hypothesis && (int)ev.elParamType == LParamIsObject && lParam != 0)
                text = TextOf((ISpRecoResult)Marshal.GetObjectForIUnknown(lParam));
        }
        finally
        {
            // What the event holds is ours to free (what SpClearEvent does in C++).
            if (lParam != 0)
            {
                switch ((int)ev.elParamType)
                {
                    case LParamIsObject or LParamIsToken: Marshal.Release(lParam); break;
                    case LParamIsPointer or LParamIsString: Marshal.FreeCoTaskMem(lParam); break;
                }
            }
        }
        return (kind, text);
    }

    private static unsafe string TextOf(ISpRecoResult result)
    {
        try
        {
            PWSTR words = default;
            result.GetText(WholePhrase, WholePhrase, true, &words, null);
            try
            {
                return words.Value is null ? "" : new string(words.Value);
            }
            finally
            {
                if (words.Value is not null) Marshal.FreeCoTaskMem((nint)words.Value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(result);
        }
    }

    private static object Create(Guid clsid) =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)
        ?? throw new InvalidOperationException("Windows' speech recognizer couldn't be started.");

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    /// <summary>Writes one line for the app; false once nobody is reading.</summary>
    private static bool Say(StreamWriter output, string line)
    {
        try
        {
            output.WriteLine(CaptureProtocol.OneLine(line));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Why it couldn't listen, in plain words.</summary>
    internal static string Explain(Exception ex) => ex switch
    {
        COMException { HResult: unchecked((int)0x80045077) or unchecked((int)0x8004503A) } => // SPERR_NOT_FOUND, SPERR_RECOGNIZER_NOT_FOUND
            "Windows' speech recognition isn't installed for your language. Add a speech language in Windows Settings › Time & language › Speech.",
        COMException { HResult: unchecked((int)0x80045056) or unchecked((int)0x8004503B) or unchecked((int)0x80045049) } =>
            "No microphone could be used. Connect one, and allow apps to use it in Windows Settings › Privacy & security › Microphone.",
        COMException { HResult: unchecked((int)0x80070005) } =>
            "Windows didn't allow the microphone. Allow desktop apps to use it in Windows Settings › Privacy & security › Microphone.",
        _ => "Windows' speech recognition couldn't start (" + ex.Message + ").",
    };
}
