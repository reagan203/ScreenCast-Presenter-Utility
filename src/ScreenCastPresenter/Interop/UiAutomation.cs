using System.Runtime.InteropServices;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// Windows UI Automation, only what SCP needs: the password boxes in a window (FR-98) and where the
/// focused element is (zoom following typing, FR-99). Called through raw pointers, like
/// <see cref="RawCom"/>: these run several times a second for as long as something is shown, and
/// every object through .NET's built-in COM wrappers leaves memory behind. Method slots are from
/// UIAutomationClient.h (Windows SDK 10.0.26100). One instance per thread; not thread-safe.
/// </summary>
internal sealed unsafe partial class UiAutomation : IDisposable
{
    private static readonly Guid ClsidCUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IidIUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    // IUIAutomation
    private const int SlotElementFromHandle = 6, SlotGetFocusedElement = 8, SlotCreatePropertyCondition = 23;

    // IUIAutomationElement
    private const int SlotFindAll = 6, SlotCurrentProcessId = 20, SlotCurrentIsOffscreen = 38, SlotCurrentBoundingRectangle = 43;

    // IUIAutomationElementArray
    private const int SlotLength = 3, SlotGetElement = 4;

    private const int IsPasswordPropertyId = 30019;
    private const int TreeScopeDescendants = 4;
    private const ushort VtBool = 11;
    private const uint CoinitMultithreaded = 0, ClsctxInprocServer = 1;

    /// <summary>At most this many password boxes are covered in one window.</summary>
    public const int MaxBoxes = 32;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Vt;
        public ushort Reserved1, Reserved2, Reserved3;
        public long Data;
        public long Data2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private nint _uia, _isPassword;

    private UiAutomation(nint uia, nint isPassword)
    {
        _uia = uia;
        _isPassword = isPassword;
    }

    /// <summary>UI Automation for the calling thread (made multithreaded if it isn't yet); null when Windows can't give it.</summary>
    public static UiAutomation? Create()
    {
        _ = CoInitializeEx(0, CoinitMultithreaded); // S_FALSE or "changed mode" are both fine: COM is ready either way
        if (CoCreateInstance(ClsidCUIAutomation, 0, ClsctxInprocServer, IidIUIAutomation, out nint uia) < 0 || uia == 0) return null;
        var value = new Variant { Vt = VtBool, Data = 0xFFFF }; // VARIANT_TRUE
        nint condition;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, Variant, nint*, int>)Slot(uia, SlotCreatePropertyCondition))(uia, IsPasswordPropertyId, value, &condition);
        if (hr < 0 || condition == 0)
        {
            Marshal.Release(uia);
            return null;
        }
        return new UiAutomation(uia, condition);
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    /// <summary>
    /// The password boxes in <paramref name="window"/> that are on screen, in screen coordinates
    /// (physical pixels for a DPI-aware caller). Empty when there are none or the window can't say.
    /// </summary>
    public List<Rectangle> PasswordBoxes(nint window)
    {
        var boxes = new List<Rectangle>();
        if (_uia == 0 || window == 0) return boxes;
        nint element = 0, found = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_uia, SlotElementFromHandle))(_uia, window, &element) < 0 || element == 0) return boxes;
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Slot(element, SlotFindAll))(element, TreeScopeDescendants, _isPassword, &found) < 0 || found == 0) return boxes;
            int count;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(found, SlotLength))(found, &count) < 0) return boxes;
            for (int i = 0; i < count && boxes.Count < MaxBoxes; i++)
            {
                nint box;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(found, SlotGetElement))(found, i, &box) < 0 || box == 0) continue;
                try
                {
                    if (BoundsOf(box) is { } r && !IsOffscreen(box)) boxes.Add(r);
                }
                finally
                {
                    Marshal.Release(box);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or SEHException)
        {
            System.Diagnostics.Debug.WriteLine(ex); // an app that went away mid-question
        }
        finally
        {
            if (found != 0) Marshal.Release(found);
            if (element != 0) Marshal.Release(element);
        }
        return boxes;
    }

    /// <summary>Where the focused element is (screen coordinates) when it belongs to process <paramref name="processId"/>; null otherwise.</summary>
    public Rectangle? FocusedIn(uint processId)
    {
        if (_uia == 0) return null;
        nint element = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_uia, SlotGetFocusedElement))(_uia, &element) < 0 || element == 0) return null;
            int pid;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, SlotCurrentProcessId))(element, &pid) < 0 || (uint)pid != processId) return null;
            return BoundsOf(element);
        }
        catch (Exception ex) when (ex is COMException or SEHException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
        finally
        {
            if (element != 0) Marshal.Release(element);
        }
    }

    private static Rectangle? BoundsOf(nint element)
    {
        Rect r;
        if (((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(element, SlotCurrentBoundingRectangle))(element, &r) < 0) return null;
        long width = (long)r.Right - r.Left, height = (long)r.Bottom - r.Top;
        if (width < 1 || height < 1 || width > 100_000 || height > 100_000) return null; // empty (not laid out) or nonsense
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static bool IsOffscreen(nint element)
    {
        int offscreen;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(element, SlotCurrentIsOffscreen))(element, &offscreen) >= 0 && offscreen != 0;
    }

    public void Dispose()
    {
        if (_isPassword != 0) Marshal.Release(_isPassword);
        if (_uia != 0) Marshal.Release(_uia);
        _isPassword = _uia = 0;
    }
}
