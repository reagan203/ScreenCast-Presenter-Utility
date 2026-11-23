using System.Diagnostics;
using System.Drawing;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StressHarness;

/// <summary>
/// Records one screen at display rate with Desktop Duplication (what Zoom and recorders see:
/// capture-excluded windows are absent) and measures each frame against the SRS flash definition:
/// more than 10% of the screen brighter than #303030 (NFR-16).
/// </summary>
internal sealed class FlashMonitor : IDisposable
{
    public const double BrightLuma = 0x30;
    public const double FlashFraction = 0.10;

    private readonly Rectangle _screen;
    private readonly Thread _thread;
    private readonly List<(TimeSpan At, double Bright)> _frames = [];
    private volatile bool _stop;
    private string? _error;

    public FlashMonitor(Rectangle screen)
    {
        _screen = screen;
        _thread = new Thread(Run) { IsBackground = true, Name = "FlashMonitor" };
    }

    public void Start() => _thread.Start();

    /// <summary>All screens in physical pixels, from DXGI (independent of this process's DPI mode).</summary>
    public static List<(string Device, Rectangle Bounds, bool Primary)> ListScreens()
    {
        var screens = new List<(string, Rectangle, bool)>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        OutputDescription d = output!.Description;
                        var r = Rectangle.FromLTRB(d.DesktopCoordinates.Left, d.DesktopCoordinates.Top, d.DesktopCoordinates.Right, d.DesktopCoordinates.Bottom);
                        screens.Add((d.DeviceName, r, r.Left == 0 && r.Top == 0));
                    }
                }
            }
        }
        return screens;
    }

    public (int Frames, int Flashes, double MaxBright, string? Error) Stop()
    {
        _stop = true;
        _thread.Join(5000);
        lock (_frames)
        {
            int flashes = _frames.Count(f => f.Bright > FlashFraction);
            double max = _frames.Count == 0 ? 0 : _frames.Max(f => f.Bright);
            return (_frames.Count, flashes, max, _error);
        }
    }

    private void Run()
    {
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            (IDXGIAdapter1 adapter, IDXGIOutput1 output) = FindOutput(factory);
            using (adapter)
            using (output)
            {
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                    out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
                using (device)
                using (context)
                {
                    Capture(output, device!, context!);
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private (IDXGIAdapter1, IDXGIOutput1) FindOutput(IDXGIFactory1 factory)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
            {
                var r = output!.Description.DesktopCoordinates;
                if (r.Left == _screen.Left && r.Top == _screen.Top)
                {
                    IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();
                    return (adapter, output1);
                }
                output.Dispose();
            }
            adapter.Dispose();
        }
        throw new InvalidOperationException($"No display output at {_screen.Location}.");
    }

    private void Capture(IDXGIOutput1 output, ID3D11Device device, ID3D11DeviceContext context)
    {
        var clock = Stopwatch.StartNew();
        IDXGIOutputDuplication duplication = output.DuplicateOutput(device);
        ID3D11Texture2D? staging = null;
        try
        {
            while (!_stop)
            {
                SharpGen.Runtime.Result result = duplication.AcquireNextFrame(100, out OutduplFrameInfo info, out IDXGIResource? resource);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    duplication.Dispose();
                    duplication = output.DuplicateOutput(device); // resolution change, secure desktop, etc.
                    continue;
                }
                result.CheckError();

                using (resource)
                {
                    if (info.LastPresentTime != 0) // 0 = only the mouse moved
                    {
                        using ID3D11Texture2D frame = resource!.QueryInterface<ID3D11Texture2D>();
                        Texture2DDescription desc = frame.Description;
                        if (staging is null || staging.Description.Width != desc.Width || staging.Description.Height != desc.Height)
                        {
                            staging?.Dispose();
                            desc.Usage = ResourceUsage.Staging;
                            desc.BindFlags = BindFlags.None;
                            desc.CPUAccessFlags = CpuAccessFlags.Read;
                            desc.MiscFlags = ResourceOptionFlags.None;
                            staging = device.CreateTexture2D(desc);
                        }
                        context.CopyResource(staging, frame);
                        double bright = BrightFraction(context, staging, (int)desc.Width, (int)desc.Height);
                        lock (_frames) _frames.Add((clock.Elapsed, bright));
                    }
                }
                duplication.ReleaseFrame();
            }
        }
        finally
        {
            staging?.Dispose();
            duplication.Dispose();
        }
    }

    /// <summary>Share of (sampled) pixels brighter than #303030, by Rec.709 luma.</summary>
    private static unsafe double BrightFraction(ID3D11DeviceContext context, ID3D11Texture2D staging, int width, int height)
    {
        MappedSubresource map = context.Map(staging, 0, MapMode.Read);
        try
        {
            const int step = 8; // every 8th pixel each way: plenty for a 10% threshold
            int bright = 0, total = 0;
            byte* rows = (byte*)map.DataPointer;
            for (int y = 0; y < height; y += step)
            {
                byte* row = rows + (long)y * map.RowPitch;
                for (int x = 0; x < width; x += step)
                {
                    byte* px = row + x * 4; // BGRA
                    double luma = 0.0722 * px[0] + 0.7152 * px[1] + 0.2126 * px[2];
                    if (luma > BrightLuma) bright++;
                    total++;
                }
            }
            return total == 0 ? 0 : (double)bright / total;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(2000);
    }
}
