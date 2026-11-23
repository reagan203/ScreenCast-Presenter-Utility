using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenCastPresenter.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace ScreenCastPresenter.Recording;

/// <summary>
/// One WASAPI capture stream delivering 48 kHz stereo float (Windows converts from the device's
/// own format): the meeting sound of one app (process loopback, FR-58), all computer sound
/// (system loopback), or the microphone (FR-53). Runs on its own thread.
///
/// Webinars are long and devices change: headphones get plugged in, a USB headset drops out.
/// Device-based streams follow the current default device (checked every 2 s) and reopen after
/// any device error, so the sound carries on instead of silently going quiet. App sound is
/// captured per process, wherever it plays, so it needs no following.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private const long BufferDuration = 2_000_000; // 200 ms, in 100-ns units
    private static readonly TimeSpan DeviceCheck = TimeSpan.FromSeconds(2);

    private readonly Func<Opened> _open;
    private readonly EDataFlow? _followDefault;
    private readonly AutoResetEvent _ready = new(false);
    private readonly Action<ReadOnlySpan<float>> _sink;
    private readonly Thread _thread;
    private readonly string _name;
    private volatile bool _stop;
    private float[] _silence = new float[4096];
    private Opened? _current;

    private sealed record Opened(IAudioClient Client, IAudioCaptureClient Capture, string? DeviceId);

    private AudioCapture(Func<Opened> open, EDataFlow? followDefault, Action<ReadOnlySpan<float>> sink, string name)
    {
        _open = open;
        _followDefault = followDefault;
        _sink = sink;
        _name = name;
        Start(_open()); // the first open throws, so the caller can say "no microphone found"
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Everything the computer plays, on whichever speakers or headphones are the default.</summary>
    public static AudioCapture SystemLoopback(Action<ReadOnlySpan<float>> sink) =>
        new(() => OpenDefault(EDataFlow.eRender, PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK), EDataFlow.eRender, sink, "SCP system audio");

    /// <summary>The default microphone.</summary>
    public static AudioCapture Microphone(Action<ReadOnlySpan<float>> sink) =>
        new(() => OpenDefault(EDataFlow.eCapture, 0), EDataFlow.eCapture, sink, "SCP microphone");

    /// <summary>
    /// Only what <paramref name="processId"/> and its child processes play (FR-58), e.g. the
    /// webinar's browser, so the user's own notifications and music stay out of the recording.
    /// Windows 10 build 20348+ / Windows 11; throws elsewhere.
    /// </summary>
    public static AudioCapture ProcessLoopback(uint processId, Action<ReadOnlySpan<float>> sink) =>
        new(() => OpenProcess(processId), null, sink, "SCP app audio");

    private static unsafe Opened OpenProcess(uint processId)
    {
        var activation = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK };
        activation.ProcessLoopbackParams.TargetProcessId = processId;
        activation.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

        var blob = new PROPVARIANT();
        blob.vt = VARENUM.VT_BLOB;
        blob.Anonymous.Anonymous.Anonymous.blob.cbSize = (uint)sizeof(AUDIOCLIENT_ACTIVATION_PARAMS);
        blob.Anonymous.Anonymous.Anonymous.blob.pBlobData = (byte*)&activation;

        var handler = new ActivationHandler();
        PInvoke.ActivateAudioInterfaceAsync(PInvoke.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, typeof(IAudioClient).GUID, blob, handler, out IActivateAudioInterfaceAsyncOperation operation)
            .ThrowOnFailure();
        if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows didn't start app audio capture.");

        HRESULT result;
        operation.GetActivateResult(&result, out object activated);
        result.ThrowOnFailure();
        var client = (IAudioClient)activated;
        Initialise(client, PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK);
        return new Opened(client, CaptureClientOf(client), null);
    }

    private static unsafe Opened OpenDefault(EDataFlow flow, uint extraFlags)
    {
        IMMDeviceEnumerator enumerator = NewEnumerator();
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out IMMDevice device);
            try
            {
                Guid clientIid = typeof(IAudioClient).GUID;
                device.Activate(&clientIid, CLSCTX.CLSCTX_ALL, null, out object clientObject);
                var client = (IAudioClient)clientObject;
                Initialise(client, extraFlags);
                return new Opened(client, CaptureClientOf(client), IdOf(device));
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static unsafe IMMDeviceEnumerator NewEnumerator()
    {
        Guid clsid = typeof(MMDeviceEnumerator).GUID, iid = typeof(IMMDeviceEnumerator).GUID;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_ALL, &iid, out object enumerator).ThrowOnFailure();
        return (IMMDeviceEnumerator)enumerator;
    }

    private static unsafe string? IdOf(IMMDevice device)
    {
        PWSTR id;
        device.GetId(&id);
        try
        {
            return id.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)id.Value);
        }
    }

    /// <summary>The current default device's id, or null if there is none.</summary>
    private static string? DefaultDeviceId(EDataFlow flow)
    {
        IMMDeviceEnumerator enumerator = NewEnumerator();
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out IMMDevice device);
            try
            {
                return IdOf(device);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (COMException)
        {
            return null; // no device of that kind right now
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Shared mode, event-driven, converted by Windows to 48 kHz stereo float.</summary>
    private static unsafe void Initialise(IAudioClient client, uint extraFlags)
    {
        var format = new WAVEFORMATEX
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = AudioMixer.Channels,
            nSamplesPerSec = AudioMixer.SampleRate,
            wBitsPerSample = 32,
            nBlockAlign = AudioMixer.Channels * 4,
            nAvgBytesPerSec = AudioMixer.SampleRate * AudioMixer.Channels * 4,
            cbSize = 0,
        };
        uint flags = extraFlags | PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                     PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration, 0, &format, null);
    }

    private static unsafe IAudioCaptureClient CaptureClientOf(IAudioClient client)
    {
        Guid iid = typeof(IAudioCaptureClient).GUID;
        client.GetService(&iid, out void* pointer);
        try
        {
            return (IAudioCaptureClient)Marshal.GetObjectForIUnknown((nint)pointer);
        }
        finally
        {
            Marshal.Release((nint)pointer);
        }
    }

    private void Start(Opened opened)
    {
        opened.Client.SetEventHandle((HANDLE)_ready.SafeWaitHandle.DangerousGetHandle());
        opened.Client.Start();
        _current = opened;
    }

    private void Close()
    {
        if (_current is not { } c) return;
        _current = null;
        try
        {
            c.Client.Stop();
        }
        catch (COMException)
        {
            // already gone
        }
        Marshal.ReleaseComObject(c.Capture);
        Marshal.ReleaseComObject(c.Client);
    }

    private void Loop()
    {
        var sinceCheck = Stopwatch.StartNew();
        while (!_stop)
        {
            if (_current is null)
            {
                // Lost the device: try again every second until one is available.
                try
                {
                    Start(_open());
                    DiagLog.Write($"{_name}: reopened on {_current?.DeviceId ?? "the app"}");
                }
                catch (Exception ex) when (ex is COMException or TimeoutException or InvalidCastException)
                {
                    _ready.WaitOne(1000);
                    continue;
                }
            }

            _ready.WaitOne(100);
            try
            {
                Drain(_current!);
            }
            catch (COMException ex)
            {
                // Device unplugged or invalidated: reopen on whatever is the default now.
                DiagLog.Write($"{_name}: device lost ({ex.HResult:X8})");
                Close();
                continue;
            }

            if (_followDefault is { } flow && sinceCheck.Elapsed >= DeviceCheck)
            {
                sinceCheck.Restart();
                string? now = DefaultDeviceId(flow);
                if (now is not null && now != _current?.DeviceId)
                {
                    DiagLog.Write($"{_name}: default device changed");
                    Close(); // e.g. headphones plugged in: follow them
                }
            }
        }
        Close();
    }

    private unsafe void Drain(Opened c)
    {
        c.Capture.GetNextPacketSize(out uint packet);
        while (packet > 0 && !_stop)
        {
            byte* data;
            c.Capture.GetBuffer(&data, out uint frames, out uint flags, null, null);
            int samples = (int)frames * AudioMixer.Channels;
            if ((flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
            {
                if (_silence.Length < samples) _silence = new float[samples];
                _sink(_silence.AsSpan(0, samples)); // always zero: never written to
            }
            else
            {
                _sink(new ReadOnlySpan<float>(data, samples));
            }
            c.Capture.ReleaseBuffer(frames);
            c.Capture.GetNextPacketSize(out packet);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _ready.Set();
        if (_thread.Join(2000)) _ready.Dispose(); // only once the thread can't touch it any more
        else Debug.WriteLine($"{_name} didn't stop in time.");
    }

    /// <summary>Completion callback for ActivateAudioInterfaceAsync. Agile: Windows may call it on any thread.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new();

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Done.Set();
    }
}
