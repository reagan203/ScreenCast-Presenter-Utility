using System.Runtime.InteropServices;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// Raw COM calls for code that runs many times a second for hours (recording).
///
/// Every COM object that reaches .NET through the built-in interop gets a runtime callable
/// wrapper (RCW), and each one leaves ~200 bytes of native memory behind even after
/// Marshal.ReleaseComObject and a full GC (measured: 222 bytes per sample+buffer pair, against 23
/// bytes with raw pointers). At 30 audio writes and 30 captured frames a second that is ~0.7 MB a
/// minute, far over NFR-09a over a two-hour webinar. These paths therefore use raw pointers and
/// vtable calls. CsWin32 can't generate them: with marshalling on (which the rest of the app
/// needs), it only emits the RCW signatures, so the two factory functions are declared here.
/// </summary>
internal static unsafe partial class RawCom
{
    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMemoryBuffer(uint cbMaxLength, out nint buffer);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateSample(out nint sample);

    // vtable slots (IUnknown = 0–2).
    private const int MediaBufferLock = 3, MediaBufferUnlock = 4, MediaBufferSetCurrentLength = 6;
    private const int SampleSetSampleTime = 36, SampleSetSampleDuration = 38, SampleAddBuffer = 42; // after IMFAttributes' 30 methods
    private const int SinkWriterWriteSample = 6;
    private const int DxgiInterfaceAccessGetInterface = 3;

    private static readonly Guid SinkWriterIid = new("3137f1cd-fe5e-4805-a5d8-fb477448cb3d");
    private static readonly Guid DxgiInterfaceAccessIid = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>A raw IMFSinkWriter pointer for <see cref="WriteBytes"/>. Release with <see cref="Marshal.Release"/>.</summary>
    public static nint SinkWriterPointer(object writer)
    {
        nint unknown = Marshal.GetIUnknownForObject(writer);
        try
        {
            Check(Marshal.QueryInterface(unknown, in SinkWriterIid, out nint pointer));
            return pointer;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Copies <paramref name="data"/> into a new memory sample and hands it to the sink writer, with no RCWs.</summary>
    public static void WriteBytes(nint sinkWriter, uint stream, ReadOnlySpan<byte> data, long time, long duration)
    {
        Check(MFCreateMemoryBuffer((uint)data.Length, out nint buffer));
        nint sample = 0;
        try
        {
            byte* target;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, MediaBufferLock))(buffer, &target, null, null));
            data.CopyTo(new Span<byte>(target, data.Length));
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, MediaBufferUnlock))(buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, MediaBufferSetCurrentLength))(buffer, (uint)data.Length));

            Check(MFCreateSample(out sample));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, SampleAddBuffer))(sample, buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SampleSetSampleTime))(sample, time));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SampleSetSampleDuration))(sample, duration));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(sinkWriter, SinkWriterWriteSample))(sinkWriter, stream, sample));
        }
        finally
        {
            if (sample != 0) Marshal.Release(sample);
            Marshal.Release(buffer);
        }
    }

    /// <summary>
    /// The D3D interface behind a WinRT surface (IDirect3DDxgiInterfaceAccess::GetInterface),
    /// as an owned raw pointer (the caller wraps or releases it).
    /// </summary>
    public static nint DxgiInterface(nint surface, in Guid iid)
    {
        Check(Marshal.QueryInterface(surface, in DxgiInterfaceAccessIid, out nint access));
        try
        {
            nint result;
            fixed (Guid* id = &iid)
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(access, DxgiInterfaceAccessGetInterface))(access, id, &result));
            return result;
        }
        finally
        {
            Marshal.Release(access);
        }
    }
}
