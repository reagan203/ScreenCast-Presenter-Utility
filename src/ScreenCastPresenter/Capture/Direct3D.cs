using System.Runtime.InteropServices;
using ScreenCastPresenter.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.WinRT.Direct3D11;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;
using ComDxgiDevice = Windows.Win32.Graphics.Dxgi.IDXGIDevice;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Glue between Direct3D 11 (Vortice) and Windows.Graphics.Capture (WinRT): device creation,
/// capture items for windows and screens, and turning a captured surface into a texture.
/// </summary>
internal static class Direct3D
{
    private static readonly FeatureLevel[] Levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    // IID of Windows.Graphics.Capture.IGraphicsCaptureItem.
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>True on Windows versions with Windows.Graphics.Capture (1803+; we require 2004).</summary>
    public static bool IsCaptureSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException)
            {
                return false;
            }
        }
    }

    /// <summary>A hardware Direct3D 11 device with BGRA support (needed by Direct2D).</summary>
    public static ID3D11Device CreateDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, Levels, out ID3D11Device? device).CheckError();
        return device!;
    }

    /// <summary>The WinRT wrapper of a D3D device, which the capture frame pool needs.</summary>
    public static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using Vortice.DXGI.IDXGIDevice dxgi = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        var com = (ComDxgiDevice)Marshal.GetObjectForIUnknown(dxgi.NativePointer);
        try
        {
            PInvoke.CreateDirect3D11DeviceFromDXGIDevice(com, out Windows.Win32.System.WinRT.IInspectable inspectable).ThrowOnFailure();
            nint unknown = Marshal.GetIUnknownForObject(inspectable);
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(unknown);
            }
            finally
            {
                Marshal.Release(unknown);
                Marshal.ReleaseComObject(inspectable);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(com);
        }
    }

    /// <summary>A capture item for a window or a whole screen.</summary>
    public static unsafe GraphicsCaptureItem CreateItem(SourceWindow source)
    {
        IObjectReference factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
        try
        {
            Guid iid = CaptureItemIid;
            void* result;
            if (source.Screen is { } screen)
            {
                Rectangle b = screen.Bounds;
                HMONITOR monitor = PInvoke.MonitorFromPoint(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
                if (monitor.IsNull) throw new InvalidOperationException("That screen isn't connected any more.");
                interop.CreateForMonitor(monitor, &iid, out result);
            }
            else
            {
                interop.CreateForWindow((HWND)source.Handle, &iid, out result);
            }
            try
            {
                return GraphicsCaptureItem.FromAbi((nint)result);
            }
            finally
            {
                Marshal.Release((nint)result);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(interop);
        }
    }

    /// <summary>The D3D texture behind a captured frame's surface (no copy).</summary>
    public static unsafe ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var access = surface.As<Windows.Win32.System.WinRT.Direct3D11.IDirect3DDxgiInterfaceAccess>();
        Guid iid = typeof(ID3D11Texture2D).GUID;
        access.GetInterface(&iid, out void* texture);
        return new ID3D11Texture2D((nint)texture);
    }

    /// <summary>
    /// Turns off the yellow capture border where Windows allows it (FR-33, Windows 11). The SDK we
    /// target (19041) has no projection for this, so the call goes straight to the COM interface
    /// IGraphicsCaptureSession3::put_IsBorderRequired. Fails harmlessly on Windows 10.
    /// </summary>
    public static unsafe void TryDisableBorder(GraphicsCaptureSession session)
    {
        nint unknown = ((IWinRTObject)session).NativeObject.ThisPtr;
        Guid iid = new("f2cdd966-22ae-5ea1-9596-3a289344c3be"); // IGraphicsCaptureSession3
        if (Marshal.QueryInterface(unknown, in iid, out nint session3) != 0 || session3 == 0) return;
        try
        {
            // vtable: IUnknown (3) + IInspectable (3) + get_IsBorderRequired (6) + put_IsBorderRequired (7).
            var putIsBorderRequired = (delegate* unmanaged[Stdcall]<nint, byte, int>)(*(nint**)session3)[7];
            _ = putIsBorderRequired(session3, 0);
        }
        finally
        {
            Marshal.Release(session3);
        }
    }

    /// <summary>
    /// Where the window's client area sits inside a captured window frame, in frame pixels. WGC
    /// captures the visible window (title bar included); the DWM engine shows the client area
    /// only, so both engines show the same thing.
    /// </summary>
    public static Rectangle ClientAreaInFrame(nint hwnd, Size frameSize)
    {
        Rectangle visible = DwmHelpers.GetVisibleBounds((HWND)hwnd);
        Rectangle client = WindowEnumerator.ClientBoundsOnScreen(hwnd);
        if (visible.Width <= 0 || visible.Height <= 0 || client.IsEmpty) return new Rectangle(Point.Empty, frameSize);
        double sx = (double)frameSize.Width / visible.Width, sy = (double)frameSize.Height / visible.Height;
        var r = Rectangle.FromLTRB(
            (int)Math.Round((client.Left - visible.Left) * sx), (int)Math.Round((client.Top - visible.Top) * sy),
            (int)Math.Round((client.Right - visible.Left) * sx), (int)Math.Round((client.Bottom - visible.Top) * sy));
        r.Intersect(new Rectangle(Point.Empty, frameSize));
        return r.Width > 0 && r.Height > 0 ? r : new Rectangle(Point.Empty, frameSize);
    }
}
